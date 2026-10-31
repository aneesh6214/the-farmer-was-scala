using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using FarmerScala.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FarmerScala.Editor;

/// <summary>Private presentation-compiler process. Unity only reads caches and queues immutable snapshots.</summary>
internal sealed class CompilerAnalysisProvider : IScalaAnalysisProvider, IScalaEditorToolsProvider, IDisposable
{
    private sealed class Request
    {
        public string Key = "", Op = "", File = "", SourceKey = "";
        public int Offset;
        public long Generation;
        public IReadOnlyDictionary<string, string> Sources = null!;
    }
    private readonly string root;
    private readonly ConcurrentDictionary<string, JObject> cache = new ConcurrentDictionary<string, JObject>();
    private readonly ConcurrentDictionary<string, byte> pending = new ConcurrentDictionary<string, byte>();
    private readonly LinkedList<Request> jobs = new LinkedList<Request>();
    private readonly object gate = new object();
    private readonly object processGate = new object();
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly Thread worker;
    private Process? process;
    private ProcessJob? job;
    private string configuration = "", configured = "";
    private volatile bool stopping;
    private int updates;
    private long sequence;
    private long generation;
    private long retryAfterTicks;
    private IReadOnlyDictionary<string, string>? lastSources;
    private string sourceKey = "";
    private bool warned;
    internal bool TakeUpdates() => Interlocked.Exchange(ref updates, 0) != 0;

    internal CompilerAnalysisProvider(string root)
    {
        this.root = root;
        worker = new Thread(Work) { IsBackground = true, Name = "FarmerScala presentation compiler" };
        worker.Start();
    }
    internal void Configure(string artifact)
    {
        string metadata = Path.Combine(Path.GetDirectoryName(artifact)!, "compiler-classpath.json");
        if (!File.Exists(metadata))
            return;
        string next = File.ReadAllText(metadata);
        lock (gate)
        {
            if (configuration == next)
                return;
            configuration = next;
            generation++;
            Interlocked.Exchange(ref retryAfterTicks, 0);
            cache.Clear();
            pending.Clear();
            jobs.Clear();
        }
        wake.Set();
    }
    private bool RequestResult(IReadOnlyDictionary<string, string> sources, string file, string op, int offset, out JObject value)
    {
        value = null!;
        if (stopping || configuration.Length == 0 && op != "format")
            return false;
        if (!ReferenceEquals(lastSources, sources))
        {
            using var hash = SHA256.Create();
            string text = string.Join("\n", sources.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key.Length + ":" + p.Key + p.Value.Length + ":" + p.Value));
            sourceKey = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
            lastSources = sources;
        }
        long capturedGeneration = Interlocked.Read(ref generation);
        string key = capturedGeneration + ":" + sourceKey + ":" + file + ":" + op + ":" + offset;
        if (cache.TryGetValue(key, out var cached))
        {
            value = cached;
            return true;
        }
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref retryAfterTicks))
            return false;
        if (pending.Count >= 32 || !pending.TryAdd(key, 0))
            return false;
        lock (gate)
        {
            var request = new Request { Key = key, File = file, Op = op, Offset = offset, Sources = sources, Generation = capturedGeneration, SourceKey = sourceKey };
            if (op == "analyze" || op == "diagnostics")
                jobs.AddLast(request);
            else
                jobs.AddFirst(request);
        }
        wake.Set();
        return false;
    }
    public bool TryResolve(IReadOnlyDictionary<string, string> sources, string file, int offset, out AnalysisResolution result)
    {
        result = new AnalysisResolution();
        if (!RequestResult(sources, file, "resolve", offset, out var value))
            return false;
        result.IsGameBuiltin = (bool?)value["isGameBuiltin"] == true;
        result.GameFunctionName = (string?)value["gameFunctionName"];
        result.HoverText = (string?)value["hover"]?["text"];
        if (value["definition"] is JObject target)
            result.Definition = new AnalysisDefinition
            {
                File = (string?)target["file"] ?? "",
                Offset = (int?)target["offset"] ?? 0,
                IsLibrary = (bool?)target["isLibrary"] == true,
                Source = (string?)target["source"],
                LibraryId = (string?)target["libraryId"]
            };
        return true;
    }
    public bool TryComplete(IReadOnlyDictionary<string, string> sources, string file, int offset, out List<string> result)
    {
        result = new List<string>();
        if (!RequestResult(sources, file, "complete", offset, out var value))
            return false;
        result = value["completions"]?.Values<string>().OfType<string>().ToList() ?? result;
        return true;
    }
    public bool TrySpans(IReadOnlyDictionary<string, string> sources, string file, out IReadOnlyList<SemanticSpan> result)
    {
        result = Array.Empty<SemanticSpan>();
        if (!RequestResult(sources, file, "analyze", 0, out var value))
            return false;
        result = value["spans"]?.Select(s => new SemanticSpan { Start = (int?)s["start"] ?? 0, Length = (int?)s["length"] ?? 0, Kind = (string?)s["kind"] ?? "" }).ToArray() ?? Array.Empty<SemanticSpan>();
        return true;
    }
    public bool TryDiagnostics(IReadOnlyDictionary<string, string> sources, out IReadOnlyList<AnalysisDiagnostic> result)
    {
        result = Array.Empty<AnalysisDiagnostic>();
        if (!RequestResult(sources, "", "diagnostics", 0, out var value))
            return false;
        result = value["diagnostics"]?.Select(d => new AnalysisDiagnostic
        {
            File = (string?)d["file"] ?? "",
            Start = (int?)d["start"] ?? 0,
            Length = (int?)d["length"] ?? 0,
            Message = (string?)d["message"] ?? "",
            Severity = (string?)d["severity"] ?? "error"
        }).ToArray() ?? Array.Empty<AnalysisDiagnostic>();
        return true;
    }
    public bool TryFormat(IReadOnlyDictionary<string, string> sources, string file, out AnalysisFormatResult result)
    {
        result = new AnalysisFormatResult();
        if (!RequestResult(sources, file, "format", 0, out var value))
            return false;
        result.Source = (string?)value["source"];
        result.Error = (string?)value["error"];
        // An explicit subsequent shortcut may retry the same source after a
        // transient tool failure. Formatting answers are consumed once.
        cache.TryRemove(Interlocked.Read(ref generation) + ":" + sourceKey + ":" + file + ":format:0", out _);
        return true;
    }
    private void Start()
    {
        lock (processGate)
        {
            if (stopping)
                throw new OperationCanceledException("Scala analysis is stopping.");
            string java = Path.Combine(root, "toolchain", "java", "bin", "java.exe");
            string libraries = Path.Combine(root, "tools", "analysis");
            string[] jars = File.ReadAllLines(Path.Combine(libraries, "classpath.txt")).Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(name => Path.Combine(libraries, Path.GetFileName(name))).ToArray();
            if (!File.Exists(java) || jars.Any(p => !File.Exists(p)))
                throw new IOException("Bundled Scala analysis dependencies are missing.");
            var info = new ProcessStartInfo(java, "-Xms64m -Xmx768m -cp " + Quote(string.Join(Path.PathSeparator.ToString(), jars)) + " farmer.analysis.Main")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            process = new Process { StartInfo = info };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null && e.Data.Length < 2000) Debug.WriteLine("Scala PC: " + e.Data); };
            if (!process.Start())
                throw new IOException("Could not start Scala analysis.");
            job = new ProcessJob();
            job.Add(process);
            process.BeginErrorReadLine();
            configured = "";
        }
    }
    private JObject Send(JObject request)
    {
        if (process == null || process.HasExited)
            Start();
        long id = ++sequence;
        request["id"] = id;
        process!.StandardInput.WriteLine(request.ToString(Formatting.None));
        process.StandardInput.Flush();
        for (int lines = 0; lines < 32; lines++)
        {
            var read = process.StandardOutput.ReadLineAsync();
            if (!read.Wait(TimeSpan.FromSeconds(20)))
                throw new IOException("Scala analysis timed out.");
            string? line = read.Result;
            if (line == null)
                throw new IOException("Scala analysis exited.");
            if (line.Length > 4_000_000)
                throw new IOException("Scala analysis frame exceeds its limit.");
            JObject result;
            try
            {
                result = JObject.Parse(line);
            }
            catch (JsonException) { continue; }
            if ((long?)result["id"] != id)
                continue;
            if ((bool?)result["ok"] != true)
                throw new IOException((string?)result["error"] ?? "Scala analysis failed.");
            return result;
        }
        throw new IOException("Scala analysis returned unexpected output.");
    }
    private void Work()
    {
        while (!stopping)
        {
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref retryAfterTicks))
            {
                wake.WaitOne(250);
                continue;
            }
            Request? request = null;
            string next;
            lock (gate)
            {
                next = configuration;
                if (jobs.Count > 0)
                {
                    request = jobs.First!.Value;
                    jobs.RemoveFirst();
                }
            }
            if (request == null)
            {
                wake.WaitOne(250);
                continue;
            }
            if (request.Generation != Interlocked.Read(ref generation) || request.SourceKey != sourceKey)
            {
                pending.TryRemove(request.Key, out _);
                continue;
            }
            try
            {
                if (request.Op != "format" && configured != next && next.Length != 0)
                {
                    Send(new JObject
                    {
                        ["op"] = "configure",
                        ["classpath"] = JArray.Parse(next),
                        ["sdk"] = Path.Combine(root, "sdk"),
                        ["workspace"] = Path.Combine(BepInEx.Paths.CachePath, "FarmerScala", "analysis"),
                        ["librarySources"] = Path.Combine(root, "tools", "analysis", "sources")
                    });
                    configured = next;
                }
                var response = Send(new JObject
                {
                    ["op"] = request.Op,
                    ["file"] = request.File,
                    ["offset"] = request.Offset,
                    ["sources"] = JObject.FromObject(request.Sources)
                });
                if (!stopping && request.Generation == Interlocked.Read(ref generation))
                {
                    if (cache.Count > 256)
                        cache.Clear();
                    cache[request.Key] = response;
                    Interlocked.Exchange(ref updates, 1);
                }
            }
            catch (Exception error)
            {
                if (request.Op == "format" && request.Generation == Interlocked.Read(ref generation))
                    cache[request.Key] = new JObject { ["error"] = error.Message };
                if (!warned)
                {
                    warned = true;
                    Plugin.Instance?.LogInfo("Scala compiler editor analysis unavailable; local index remains active: " + error.Message);
                }
                Interlocked.Exchange(ref retryAfterTicks, DateTime.UtcNow.AddSeconds(5).Ticks);
                CloseProcess();
            }
            finally { pending.TryRemove(request.Key, out _); }
        }
        CloseProcess();
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private void CloseProcess()
    {
        lock (processGate)
        {
            try
            {
                job?.Dispose();
            }
            catch { }
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill();
            }
            catch { }
            process?.Dispose();
            process = null;
            job = null;
            configured = "";
        }
    }
    public void Dispose()
    {
        stopping = true;
        wake.Set();
        CloseProcess();
        worker.Join(500);
        cache.Clear();
        pending.Clear();
        lock (gate)
            jobs.Clear();
    }
}
