using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace FarmerScala.Compiler;

public sealed record Diagnostic(string file, int line, int column, int endLine, int endColumn, string message);
public sealed record Compilation(bool ok, string? artifact, string? sourceMap, string hash, IReadOnlyList<Diagnostic> diagnostics, double elapsedMs, bool cached = false);

/// <summary>One persistent compiler JVM. Raw source is never executed by this service.</summary>
public sealed class CompilerService(string sdk, string toolchain) : IDisposable
{
    private SbtSession? session;
    private string? activeOutput;
    private readonly SemaphoreSlim gate = new(1);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Regex SourceName = new(@"^[\p{L}_][\p{L}\p{N}_-]*(\.[\p{L}_][\p{L}\p{N}_-]*)*$", RegexOptions.CultureInvariant);

    public async Task<Compilation> CompileAsync(string project, string entry, string output, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        var timer = Stopwatch.StartNew();
        try
        {
            project = Path.GetFullPath(project);
            output = Path.GetFullPath(output);
            if (!Directory.Exists(project))
                throw new DirectoryNotFoundException("Scala project source directory is missing.");
            if (!Directory.Exists(sdk))
                throw new DirectoryNotFoundException("The bundled Scala game SDK is missing.");
            if (Path.GetFileName(entry) != entry || !entry.EndsWith(".scala", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The entry must name a project .scala file.");
            var files = Directory.GetFiles(project, "*.scala").Order(StringComparer.Ordinal).ToArray();
            if (!files.Any(f => string.Equals(Path.GetFileName(f), entry, StringComparison.Ordinal)))
                return new(false, null, null, "", [new(entry, 1, 1, 1, 1, "The entry file is not present in this project.")], timer.Elapsed.TotalMilliseconds);
            if (files.Length > 512)
                throw new InvalidDataException("A Scala project can contain at most 512 source windows.");
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!SourceName.IsMatch(name) || name.Split('.').Any(part => part.StartsWith("__Farmer", StringComparison.OrdinalIgnoreCase)))
                    return new(false, null, null, "", [new(Path.GetFileName(file), 1, 1, 1, 1, "Use dot-separated source names beginning with a letter or underscore, containing letters, digits, underscores or hyphens.")], timer.Elapsed.TotalMilliseconds);
                if (new FileInfo(file).Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("A source window exceeds 2 MiB.");
            }
            var moduleNames = files.Select(f => Path.GetFileNameWithoutExtension(f)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string module in moduleNames)
                for (int dot = module.IndexOf('.'); dot >= 0; dot = module.IndexOf('.', dot + 1))
                    if (moduleNames.Contains(module[..dot]))
                        return new(false, null, null, "", [new(module + ".scala", 1, 1, 1, 1, "Module '" + module[..dot] + "' is also used as a namespace. Rename one of these files.")], timer.Elapsed.TotalMilliseconds);
            var sdkFiles = Directory.GetFiles(sdk, "*.scala", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            var sources = files.ToDictionary(f => Path.GetFileName(f)!, f => File.ReadAllText(f, Utf8), StringComparer.Ordinal);
            string hash = Hash(entry, sources, sdkFiles);
            string published = Path.Combine(output, "artifacts", hash);
            string artifact = Path.Combine(published, "main.js");
            if (ValidArtifact(artifact, hash) && File.Exists(Path.Combine(output, "compiler-classpath.json")))
                return new(true, artifact, artifact + ".map", hash, [], timer.Elapsed.TotalMilliseconds, true);

            string generated = Path.Combine(output, "src", "main", "scala");
            Directory.CreateDirectory(generated);
            var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                string name = Path.GetFileNameWithoutExtension(source.Key);
                string target = Path.Combine(generated, source.Key!);
                desired.Add(target);
                // Prefix deliberately stays two lines. Scala is inside explicit braces;
                // unchanged user indentation and columns remain valid in Scala 3.
                WriteChanged(target, Prefix(name) + source.Value.Replace("\r\n", "\n") + "\n def __farmerRun(): Unit = ()\n}\n");
            }
            string entryModule = Path.GetFileNameWithoutExtension(entry);
            string cases = string.Join("\n", files.Select(f => { var n = Path.GetFileNameWithoutExtension(f); return "case " + JsonSerializer.Serialize(n) + " => _root_.__farmer_player." + string.Join(".", n.Split('.').Select(part => "`" + part + "`")) + ".__farmerRun()"; }));
            string exporter = Path.Combine(generated, "__FarmerEntry.scala");
            desired.Add(exporter);
            WriteChanged(exporter, "import scala.scalajs.js.annotation.JSExportTopLevel\nobject __FarmerEntry {\n @JSExportTopLevel(\"runFarm\") def runFarm(name: String): String = { (if name.isEmpty then " + JsonSerializer.Serialize(entryModule) + " else name.stripSuffix(\".scala\")) match {\n" + cases + "\ncase _ => throw new IllegalArgumentException(\"Unknown Scala source window: \" + name)\n}; \"null\" }\n}\n");
            foreach (string stale in Directory.GetFiles(generated, "*.scala"))
                if (!desired.Contains(stale))
                    File.Delete(stale); // generated directory only; never project input.
            string sdkTarget = Path.Combine(output, "sdk");
            Directory.CreateDirectory(sdkTarget);
            var sdkDesired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in sdkFiles)
            {
                string target = Path.Combine(sdkTarget, Path.GetRelativePath(sdk, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                sdkDesired.Add(target);
                WriteChanged(target, File.ReadAllText(source, Utf8));
            }
            foreach (string stale in Directory.GetFiles(sdkTarget, "*.scala", SearchOption.AllDirectories))
                if (!sdkDesired.Contains(stale))
                    File.Delete(stale);
            Directory.CreateDirectory(Path.Combine(output, "project"));
            WriteChanged(Path.Combine(output, "project", "plugins.sbt"), "addSbtPlugin(\"org.scala-js\" % \"sbt-scalajs\" % \"1.22.0\")\n");
            WriteChanged(Path.Combine(output, "project", "build.properties"), "sbt.version=1.11.7\n");
            WriteChanged(Path.Combine(output, "build.sbt"), BuildDefinition);

            if (session == null || activeOutput != output || session.HasExited)
            {
                session?.Dispose();
                session = new SbtSession(output, toolchain);
                activeOutput = output;
                await session.ReadyAsync(cancellation);
            }
            var log = await session.BuildAsync(cancellation);
            string linked = Path.Combine(output, "linked", "main.js");
            if (!log.Success || !File.Exists(linked))
            {
                var diagnostics = ParseDiagnostics(log.Lines, files);
                if (diagnostics.Count == 0)
                    diagnostics.Add(new(entry, 1, 1, 1, 1, "Compilation failed.\n" + string.Join("\n", log.Lines.TakeLast(20))));
                return new(false, null, null, hash, diagnostics, timer.Elapsed.TotalMilliseconds);
            }
            // Hash-addressed immutable outputs let the running generation finish while
            // the compiler prepares the next generation. Failure never returns old JS.
            Directory.CreateDirectory(published);
            File.Copy(linked, artifact, true);
            if (File.Exists(linked + ".map"))
            {
                // Embed our generated sources so error navigation still works after
                // deployment, cache relocation, or subsequent source edits.
                var map = JsonNode.Parse(File.ReadAllText(linked + ".map", Utf8))!.AsObject();
                var contents = new JsonArray();
                foreach (var source in map["sources"]!.AsArray())
                {
                    string path = source!.GetValue<string>();
                    if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
                    {
                        if (!uri.IsFile)
                        {
                            contents.Add(null);
                            continue;
                        }
                        path = uri.LocalPath;
                    }
                    else
                        path = Path.Combine(Path.GetDirectoryName(linked)!, path);
                    bool inside = Path.GetFullPath(path).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    contents.Add(inside && File.Exists(path) ? JsonValue.Create(File.ReadAllText(path, Utf8)) : null);
                }
                map["sourcesContent"] = contents;
                WriteChanged(artifact + ".map", map.ToJsonString());
            }
            WriteChanged(Path.Combine(published, "sources.json"), JsonSerializer.Serialize(new
            {
                prefixLines = 2,
                entry,
                sources
            }));
            File.Copy(Path.Combine(output, "compiler-classpath.json"), Path.Combine(published, "compiler-classpath.json"), true);
            WriteChanged(artifact + ".editor-map.json", JsonSerializer.Serialize(new
            {
                sources = files.Select(f => new { generated = Path.Combine(generated, Path.GetFileName(f)), file = Path.GetFileName(f), lineOffset = 2, columnOffset = 0 })
            }));
            WriteChanged(Path.Combine(published, "build.json"), JsonSerializer.Serialize(new
            {
                hash,
                artifactSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifact))),
                entry,
                scala = "3.9.0",
                scalaJs = "1.22.0",
                createdUtc = DateTime.UtcNow
            }));
            return new(true, artifact, artifact + ".map", hash, [], timer.Elapsed.TotalMilliseconds);
        }
        finally { gate.Release(); }
    }

    private static string Prefix(string name)
    {
        string[] parts = name.Split('.');
        return "package __farmer_player; " + string.Concat(parts.Take(parts.Length - 1).Select(part => "package `" + part + "`; "))
            + "object `" + parts[parts.Length - 1] + "` {\nimport _root_.game.api.*; import _root_.__farmer_player.*\n";
    }

    private static string Hash(string entry, IReadOnlyDictionary<string, string> sources, string[] sdkFiles)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string s)
        {
            hash.AppendData(Utf8.GetBytes(s));
            hash.AppendData([0]);
        }
        Add("farmer-scala-wrapper-v4-dotted:3.9.0:1.22.0");
        Add(entry);
        foreach (var source in sources)
        {
            Add(source.Key);
            Add(source.Value);
        }
        foreach (string file in sdkFiles)
        {
            Add(Path.GetFileName(file));
            Add(File.ReadAllText(file, Utf8));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static void WriteChanged(string path, string text)
    {
        if (!File.Exists(path) || File.ReadAllText(path, Utf8) != text)
            File.WriteAllText(path, text, Utf8);
    }
    private static bool ValidArtifact(string artifact, string hash)
    {
        try
        {
            if (!File.Exists(artifact) || !File.Exists(artifact + ".map") || !File.Exists(artifact + ".editor-map.json"))
                return false;
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(artifact)!, "build.json")));
            return manifest.RootElement.GetProperty("hash").GetString() == hash &&
                manifest.RootElement.GetProperty("artifactSha256").GetString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifact)));
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
        catch (KeyNotFoundException) { return false; }
    }
    public static List<Diagnostic> ParseDiagnostics(IReadOnlyList<string> log, string[] sourceFiles)
    {
        var result = new List<Diagnostic>();
        // Dotty emits '-- [E007] Type Mismatch Error: C:\...\f0.scala:3:9'.
        var location = new Regex(@"(?<file>(?:[A-Za-z]:)?[^\r\n]*?\.scala):(?<line>\d+):(?<column>\d+)");
        for (int i = 0; i < log.Count; i++)
        {
            string text = Strip(log[i]);
            int marker = text.IndexOf(": ", StringComparison.Ordinal);
            string tail = text.StartsWith("--", StringComparison.Ordinal) && marker >= 0 ? text[(marker + 2)..] : text;
            var match = location.Match(tail);
            if (!match.Success)
                continue;
            string rawPath = match.Groups["file"].Value.Trim();
            string file = Path.GetFileName(rawPath);
            if (!sourceFiles.Any(f => Path.GetFileName(f) == file))
                continue;
            int line = Math.Max(1, int.Parse(match.Groups["line"].Value) - 2);
            int column = Math.Max(1, int.Parse(match.Groups["column"].Value));
            int width = 1;
            var message = new List<string>();
            for (int j = i + 1; j < log.Count && j < i + 15; j++)
            {
                string next = Strip(log[j]);
                if (location.IsMatch(next) || next.StartsWith("FARMER_", StringComparison.Ordinal) || next.Contains("error found", StringComparison.Ordinal))
                    break;
                int bar = next.IndexOf('|');
                if (bar >= 0)
                {
                    string body = next[(bar + 1)..].Trim();
                    if (body.Length > 0 && body.All(c => c == '^'))
                        width = Math.Max(width, body.Length);
                    if (body.Length != 0 && !body.All(c => c == '^') && !char.IsDigit(next.TrimStart().FirstOrDefault()))
                        message.Add(body);
                }
            }
            string heading = text.StartsWith("--", StringComparison.Ordinal) && marker >= 0 ? text[2..marker].Trim() : "Scala compilation error";
            result.Add(new(file, line, column, line, column + width, message.Count > 0 ? string.Join("\n", message) : heading));
        }
        return result;
    }
    private static string Strip(string s) => Regex.Replace(s, "\\x1B\\[[0-9;]*[A-Za-z]", "").Replace("[error] ", "").TrimEnd();
    public void Dispose()
    {
        session?.Dispose();
        gate.Dispose();
    }

    private const string BuildDefinition = """
import org.scalajs.linker.interface.{ESVersion, ModuleKind}
ThisBuild / scalaVersion := "3.9.0"
ThisBuild / offline := sys.props.get("farmer.offline").contains("true")
lazy val root = project.in(file(".")).enablePlugins(ScalaJSPlugin).settings(
  Compile / unmanagedSourceDirectories += baseDirectory.value / "sdk",
  scalaJSUseMainModuleInitializer := false,
  scalaJSLinkerConfig ~= { _.withModuleKind(ModuleKind.NoModule).withESFeatures(_.withESVersion(ESVersion.ES2015).withAllowBigIntsForLongs(false)).withOptimizer(false).withSourceMap(true) },
  Compile / fastLinkJS / scalaJSLinkerOutputDirectory := baseDirectory.value / "linked"
)
Global / commands += Command.command("farmerBuild") { state =>
  try {
    val (linked, _) = Project.extract(state).runTask(Compile / fastLinkJS, state)
    val (next, cp) = Project.extract(linked).runTask(Compile / fullClasspath, linked)
    def quote(s: String) = "\"" + s.replace("\\", "\\\\").replace("\"", "\\\"") + "\""
    val output = Project.extract(next).get(baseDirectory) / "compiler-classpath.json"
    IO.write(output, cp.map(entry => quote(entry.data.getAbsolutePath)).mkString("[", ",", "]"))
    println("FARMER_BUILD_OK")
    next
  } catch { case e: Throwable => println("FARMER_BUILD_FAIL"); state }
}
Global / commands += Command.command("farmerReady") { state => println("FARMER_READY"); state }
""";
}

internal sealed class SbtSession : IDisposable
{
    private readonly Process process;
    private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
    private bool disposed;
    public bool HasExited => disposed || process.HasExited;
    public SbtSession(string output, string toolchain)
    {
        string java = Path.Combine(toolchain, "java", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (!File.Exists(java))
            throw new FileNotFoundException("Bundled Java runtime is missing. Reinstall FarmerScala.", java);
        string launcher = Path.Combine(toolchain, "sbt-launch-1.11.7.jar");
        if (!File.Exists(launcher))
            throw new FileNotFoundException("Bundled Scala compiler launcher is missing.", launcher);
        // Ivy treats parentheses in filesystem repositories as optional-pattern
        // syntax. The default "local" resolver therefore breaks when Steam is
        // installed in Program Files (x86). This compiler uses only the pinned
        // Maven artifacts in its Coursier cache; it needs no Ivy-local resolver.
        string repositories = Path.Combine(output, "farmer-repositories");
        File.WriteAllText(repositories, "[repositories]\nmaven-central: https://repo.maven.apache.org/maven2/\n", new UTF8Encoding(false));
        var start = new ProcessStartInfo(java) { WorkingDirectory = output, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) };
        foreach (string arg in new[] { "-Xms128m", "-Xmx2g", "-Dfarmer.offline=" + (Environment.GetEnvironmentVariable("FARMER_SCALA_OFFLINE") == "true" ? "true" : "false"), "-Dsbt.server.autostart=false", "-Dsbt.boot.server=false", "-Dsbt.supershell=false", "-Dsbt.color=false", "-Dsbt.log.noformat=true", "-Dsbt.ci=true", "-Dsbt.io.virtual=false", "-Dsbt.boot.directory=" + Path.Combine(toolchain, "sbt-boot"), "-Dsbt.global.base=" + Path.Combine(toolchain, "sbt-global"), "-Dsbt.ivy.home=" + Path.Combine(toolchain, "ivy"), "-Dsbt.repository.config=" + repositories, "-Dsbt.override.build.repos=true", "-jar", launcher })
            start.ArgumentList.Add(arg);
        start.Environment["COURSIER_CACHE"] = Path.Combine(toolchain, "coursier");
        start.Environment["JAVA_HOME"] = Path.Combine(toolchain, "java");
        process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.Exited += (_, _) => lines.Writer.TryComplete();
        process.Start();
        _ = Read(process.StandardOutput);
        _ = Read(process.StandardError);
    }
    private async Task Read(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
            await lines.Writer.WriteAsync(line);
    }
    public async Task ReadyAsync(CancellationToken cancellation)
    {
        await process.StandardInput.WriteLineAsync("farmerReady");
        await process.StandardInput.FlushAsync(cancellation);
        await WaitFor("FARMER_READY", cancellation);
    }
    public async Task<(bool Success, List<string> Lines)> BuildAsync(CancellationToken cancellation)
    {
        await process.StandardInput.WriteLineAsync("farmerBuild");
        await process.StandardInput.FlushAsync(cancellation);
        var log = await WaitFor("FARMER_BUILD_", cancellation);
        return (log.Any(l => l.Contains("FARMER_BUILD_OK", StringComparison.Ordinal)), log);
    }
    private async Task<List<string>> WaitFor(string marker, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        var log = new List<string>();
        try
        {
            await foreach (string line in lines.Reader.ReadAllAsync(timeout.Token))
            {
                if (log.Count < 12000)
                    log.Add(line);
                if (line.Contains("Project loading failed:", StringComparison.Ordinal))
                {
                    Dispose();
                    throw new IOException("Scala compiler project loading failed.\n" + string.Join("\n", log.TakeLast(65)));
                }
                if (line.Contains(marker, StringComparison.Ordinal))
                    return log;
            }
            throw new IOException("Scala compiler exited unexpectedly.\n" + string.Join("\n", log.TakeLast(12)));
        }
        catch (OperationCanceledException) { Dispose(); throw new TimeoutException("Scala compiler did not respond within four minutes."); }
    }
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
        process.Dispose();
    }
}
