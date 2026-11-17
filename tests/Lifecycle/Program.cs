using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using FarmerScala;
using FarmerScala.Editor;
using FarmerScala.Runtime;
using Newtonsoft.Json.Linq;

if (args.Length == 2 && args[0] == "--stage") { await StagedSmoke.Run(args[1]); return; }

// Child modes act as independently launched services. The lifecycle under test
// is the actual production host, client, framing and Windows process job.
if (args.Contains("--sdk"))
{
    Console.InputEncoding = Encoding.UTF8;
    Console.OutputEncoding = new UTF8Encoding(false);
    string? line;
    while ((line = Console.ReadLine()) != null)
    {
        var request = JObject.Parse(line);
        string source = File.ReadAllText(Path.Combine((string)request["project"]!, "main.scala"));
        if (source == "slow")
            Thread.Sleep(1200);
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        string artifact = Path.Combine((string)request["output"]!, "artifacts", sourceHash, "main.js");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, source);
        bool ok = source != "error" && (source != "retry" || File.Exists(Path.Combine((string)request["project"]!, "repaired")));
        Console.WriteLine(new JObject
        {
            ["id"] = request["id"],
            ["ok"] = ok,
            ["artifact"] = ok ? artifact : null,
            ["diagnostics"] = new JArray(new JObject { ["file"] = "main.scala", ["line"] = 2, ["column"] = 7, ["message"] = "bad type" })
        }.ToString(Newtonsoft.Json.Formatting.None));
    }
    return;
}
if (args.Contains("--artifact"))
{
    using var client = new TcpClient();
    client.Connect("127.0.0.1", int.Parse(Environment.GetEnvironmentVariable("TFWR_PORT")!));
    using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 4096, true);
    using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    var identity = new JObject { ["v"] = 1, ["run"] = Environment.GetEnvironmentVariable("TFWR_RUN_ID"), ["drone"] = Environment.GetEnvironmentVariable("TFWR_DRONE_ID") };
    var hello = (JObject)identity.DeepClone();
    hello["kind"] = "hello";
    hello["pid"] = Environment.ProcessId;
    hello["token"] = Environment.GetEnvironmentVariable("TFWR_TOKEN");
    writer.WriteLine(hello.ToString(Newtonsoft.Json.Formatting.None));
    if (reader.ReadLine() == null)
        return;
    string source = File.ReadAllText(args[Array.IndexOf(args, "--artifact") + 1]);
    if (source == "retry") Thread.Sleep(250); // Compilation is complete, execution startup is not.
    var started = (JObject)identity.DeepClone();
    started["kind"] = "event"; started["event"] = "started";
    writer.WriteLine(started.ToString(Newtonsoft.Json.Formatting.None));
    if (source == "hold")
    {
        while (reader.ReadLine() != null)
        {
        }
        return;
    }
    identity["kind"] = "event";
    identity["event"] = "completed";
    identity["result"] = null;
    writer.WriteLine(identity.ToString(Newtonsoft.Json.Formatting.None));
    return;
}

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string scratch = Path.Combine(root, ".local", "lifecycle-checks", Guid.NewGuid().ToString("N"));
string install = Path.Combine(scratch, "install");
Directory.CreateDirectory(install);
BepInEx.Paths.CachePath = Path.Combine(scratch, "cache-λ你好");
// Use the project's portable runtime, never a machine-wide runtime in child processes.
string portable = Path.Combine(root, ".local", "toolchain", "dotnet-x64");
foreach (string file in Directory.GetFiles(portable, "*", SearchOption.AllDirectories))
{
    string target = Path.Combine(install, "runtime", Path.GetRelativePath(portable, file));
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(file, target);
}
foreach (string service in new[] { "compiler", "worker" })
{
    string folder = Path.Combine(install, "tools", service);
    Directory.CreateDirectory(folder);
    foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
        File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), true);
    string original = Path.GetFileNameWithoutExtension(typeof(Program).Assembly.Location);
    string target = service == "compiler" ? "FarmerScala.Compiler" : "FarmerScala.Worker";
    foreach (string suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
        File.Copy(Path.Combine(folder, original + suffix), Path.Combine(folder, target + suffix), true);
}
var plugin = new Plugin();
using var host = new RuntimeHost(plugin, install);
var window = new CodeWindow();
int checks = 0;
void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; Console.WriteLine("PASS " + description); }
void Source(string source)
{
    string project = Path.Combine(BepInEx.Paths.CachePath, "FarmerScala", "runs", Guid.NewGuid().ToString("N"), "src");
    Directory.CreateDirectory(project);
    File.WriteAllText(Path.Combine(project, "main.scala"), source);
    EditorIntegration.Project = new ProjectSnapshot { Directory = project, Sources = new Dictionary<string, string> { ["main"] = source } };
    EditorIntegration.Error = null;
}
async Task PumpUntil(Func<bool> done, int timeout = 15000)
{
    var watch = Stopwatch.StartNew();
    while (!done())
    {
        if (watch.ElapsedMilliseconds > timeout)
            throw new TimeoutException(string.Join("\n", plugin.Lines));
        host.Tick();
        await Task.Delay(10);
    }
}
Check(host.CompilationState == CompilationProgress.None, "new host has no compilation status");
Source("slow");
var duration = Stopwatch.StartNew();
host.Run(window);
Check(host.CompilationState == CompilationProgress.Compiling, "accepted Play records compiling status before background work");
Check(duration.ElapsedMilliseconds < 500 && host.IsRunning && MainSim.Inst.Starts == 0, "Play returns while the compiler prepares an immutable snapshot");
host.Stop("cancel preparing");
Check(!host.IsRunning && host.CompilationState == CompilationProgress.Cancelled, "Stop immediately revokes a pending compilation and marks it cancelled");
await Task.Delay(1700);
host.Tick();
Check(MainSim.Inst.Starts == 0, "a late compiler success cannot restart a stopped run");
Source("error");
host.Run(window);
await PumpUntil(() => !host.IsRunning);
Check(host.CompilationState == CompilationProgress.Failed, "compiler error records failure rather than Done");
Check(MainSim.Inst.Starts == 0 && EditorIntegration.Error?.Contains("main.scala:2:7:bad type") == true, "compiler diagnostics keep the real line and column and launch no worker");
Source("retry");
host.Run(window);
await PumpUntil(() => !host.IsRunning);
File.WriteAllText(Path.Combine(EditorIntegration.Project.Directory, "repaired"), "cache repaired");
host.Run(window);
await PumpUntil(() => MainSim.Inst.Starts == 1);
Check(!host.IsCompiling && host.CompilationState == CompilationProgress.Compiling, "finished compilation stays pending while the real worker initializes");
await PumpUntil(() => !host.IsRunning);
Check(host.CompilationState == CompilationProgress.Done, "successful compilation retains Done after execution completes");
Check(MainSim.Inst.Starts == 1, "an unchanged source can retry after a transient compiler failure is repaired");
Source("valid");
host.Run(window);
await PumpUntil(() => MainSim.Inst.Starts == 2 && !host.IsRunning);
Check(MainSim.Inst.Starts == 2, "a corrected source generation compiles, authenticates and completes through the real host");
Source("another");
host.Run(window);
EditorIntegration.Enabled = false;
await PumpUntil(() => !host.IsRunning);
Check(MainSim.Inst.Starts == 2, "switching away from Scala prevents a pending build from starting");
EditorIntegration.Enabled = true;
Check(CompilerClient.Fingerprint(EditorIntegration.Project) != CompilerClient.Fingerprint(new ProjectSnapshot { EntryFile = "other.scala", Sources = EditorIntegration.Project.Sources }), "compilation cache identity includes the chosen entry");

Source("hold");
ProjectSnapshot heldProject = EditorIntegration.Project;
string heldHash = CompilerClient.Fingerprint(heldProject);
string cacheRoot = Path.Combine(BepInEx.Paths.CachePath, "FarmerScala");
string heldRun = Directory.GetParent(heldProject.Directory)!.FullName;
string heldArtifact = Path.Combine(cacheRoot, "artifacts", heldHash);
string heldOrigin = Path.Combine(cacheRoot, "compiler-project", "artifacts", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("hold"))).ToLowerInvariant());
host.Run(window);
await PumpUntil(() => MainSim.Inst.Starts == 3 && host.IsRunning);
for (int i = 0; i < 80; i++)
{
    Source("warm-" + i);
    var snapshot = EditorIntegration.Project;
    string artifact = Path.Combine(cacheRoot, "artifacts", CompilerClient.Fingerprint(snapshot), "main.js");
    host.ProjectChanged(snapshot);
    await PumpUntil(() => File.Exists(artifact));
}
foreach (string category in new[] { "runs", "artifacts", Path.Combine("compiler-project", "artifacts") })
{
    string folder = Path.Combine(cacheRoot, category);
    Directory.CreateDirectory(folder);
    for (int i = 0; i < 140; i++)
    {
        string name = category == "runs" ? Guid.NewGuid().ToString("N") : i.ToString("x64");
        string generation = Path.Combine(folder, name);
        Directory.CreateDirectory(generation);
        File.WriteAllText(Path.Combine(generation, "owned-test.txt"), "unused generation");
    }
    foreach (string generation in Directory.GetDirectories(folder))
        Directory.SetLastWriteTimeUtc(generation, DateTime.UtcNow.AddDays(-2));
}
foreach (string path in new[] { heldRun, heldArtifact, heldOrigin }) Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
string user = Path.Combine(scratch, "user-save", "main.scala");
Directory.CreateDirectory(Path.GetDirectoryName(user)!);
File.WriteAllText(user, "preserve user source");
string unknown = Path.Combine(cacheRoot, "runs", "not-owned");
Directory.CreateDirectory(unknown);
File.WriteAllText(Path.Combine(unknown, "main.scala"), "preserve unknown directory");
host.PruneCache();
Check(File.Exists(Path.Combine(heldProject.Directory, "main.scala")) && Directory.Exists(heldArtifact) && Directory.Exists(heldOrigin), "active run pins keep source, linked artifact and compiler origin after request-cache eviction and forced retention");
Check(Directory.GetDirectories(Path.Combine(cacheRoot, "runs")).Length <= 130 &&
    Directory.GetDirectories(Path.Combine(cacheRoot, "artifacts")).Length <= 129 &&
    Directory.GetDirectories(Path.Combine(cacheRoot, "compiler-project", "artifacts")).Length <= 129,
    "owned cache generations stay bounded with active and cached request pins");
Check(File.ReadAllText(user) == "preserve user source" && Directory.Exists(unknown), "retention never traverses user projects or unrecognized cache names");
host.Stop("release retention fixture");
host.PruneCache();
Check(!Directory.Exists(heldRun) && !Directory.Exists(heldArtifact) && !Directory.Exists(heldOrigin), "Stop releases active cache pins after retiring workers so old generations can be collected");

string safetyRoot = Path.Combine(scratch, "retention-safety");
var safety = new OwnedCacheRetention(safetyRoot, plugin.LogInfo, runLimit: 0, artifactLimit: 0, minimumAge: TimeSpan.Zero);
string leased = Path.Combine(safetyRoot, "runs", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(leased);
File.WriteAllText(Path.Combine(leased, "safe.txt"), "keep while pinned");
using (safety.Pin(Path.Combine(leased, "src")))
{
    safety.Prune();
    Check(Directory.Exists(leased), "cache leases protect a recognized generation before work is enqueued");
}
safety.Prune();
Check(!Directory.Exists(leased), "releasing a cache lease makes an unused owned generation collectable");
string reparse = Path.Combine(safetyRoot, "runs", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(reparse);
try
{
    Directory.CreateSymbolicLink(Path.Combine(reparse, "external-save"), Path.GetDirectoryName(user)!);
    safety.Prune();
    Check(Directory.Exists(reparse) && File.ReadAllText(user) == "preserve user source", "retention skips a recognized tree containing a reparse point to user data");
}
catch (Exception error) when (error is UnauthorizedAccessException || error is IOException || error is PlatformNotSupportedException)
{
    Console.WriteLine("SKIP reparse fixture: " + error.Message);
}
Source("hold");
host.Run(window);
await PumpUntil(() => MainSim.Inst.Starts == 4 && host.IsRunning);
FarmerScala.Game.NativeGameSession.InjectedFault = "late plant failure";
FarmerScala.Game.NativeGameSession.InjectedLocation = new JObject { ["file"] = "helper.scala", ["line"] = 9, ["column"] = 5, ["endLine"] = 9, ["endColumn"] = 10 };
host.Tick();
Check(!host.IsRunning && EditorIntegration.Error == "helper.scala:9:5:late plant failure", "late native side-effect fault reaches the editor with its cross-file Scala call location");
FarmerScala.Game.NativeGameSession.InjectedFault = null;
FarmerScala.Game.NativeGameSession.InjectedLocation = null;
Source("hold");
host.Run(window);
await PumpUntil(() => MainSim.Inst.Starts == 5 && host.IsRunning);
Check(host.IsInvolved(window) && !host.IsInvolved(new CodeWindow { fileName = "Scratch" }), "running dependency set distinguishes the entry from unrelated windows");
host.Tick();
Check(host.IsRunning, "unrelated source activity leaves the current worker running");
EditorIntegration.RevisionMatches = false;
host.Tick();
Check(!host.IsRunning, "changed or removed running sources stop the worker before stale source mapping is used");
EditorIntegration.RevisionMatches = true;
Console.WriteLine(checks + " production lifecycle checks passed (native Unity calls adapted).");
