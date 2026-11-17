using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FarmerScala.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FarmerScala.Runtime;

/// <summary>Serializes compiler requests off Unity's thread and owns the compiler tree.</summary>
internal sealed class CompilerClient : IDisposable
{
    private readonly string root, cache;
    private readonly Action<string> log;
    private readonly BlockingCollection<Work> queue = new BlockingCollection<Work>(32);
    private readonly ConcurrentDictionary<string, Task<JObject>> requests = new ConcurrentDictionary<string, Task<JObject>>();
    private readonly ConcurrentDictionary<string, IDisposable> requestPins = new ConcurrentDictionary<string, IDisposable>();
    private readonly ConcurrentDictionary<string, string> artifactOrigins = new ConcurrentDictionary<string, string>();
    private readonly ConcurrentDictionary<string, long> requestUse = new ConcurrentDictionary<string, long>();
    private readonly OwnedCacheRetention retention;
    private readonly object cacheOperations = new object();
    private readonly Timer retentionTimer;
    private ProcessJob? job;
    private readonly object processLock = new object();
    private Process? process;
    private long nextId;
    private long nextUse;
    private bool disposed;
    private ProjectSnapshot? latestWarm;
    private IDisposable? warmPin;
    private bool warming;

    public CompilerClient(string root, string cache, Action<string> log)
    {
        this.root = root;
        this.cache = Path.GetFullPath(cache);
        this.log = log;
        retention = new OwnedCacheRetention(this.cache, log);
        retentionTimer = new Timer(_ =>
        {
            if (disposed)
                return;
            bool compilerIdle = Monitor.TryEnter(cacheOperations);
            try
            {
                retention.Prune(includeCompilerArtifacts: compilerIdle);
            }
            finally { if (compilerIdle) Monitor.Exit(cacheOperations); }
        }, null, 30000, 30000);
        new Thread(Loop) { IsBackground = true, Name = "FarmerScala compiler client" }.Start();
    }

    public IDisposable KeepRun(string project, string? artifact = null)
    {
        string? origin = null;
        string? hash = artifact == null ? null : Path.GetFileName(Path.GetDirectoryName(artifact));
        if (!string.IsNullOrEmpty(hash))
            artifactOrigins.TryGetValue(hash!, out origin);
        return retention.Pin(project, artifact, origin);
    }

    internal void PruneCache()
    {
        lock (cacheOperations)
            retention.Prune();
    }

    internal static string Fingerprint(ProjectSnapshot project)
    {
        var text = new StringBuilder(project.EntryFile).Append('\0');
        foreach (var source in project.Sources.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append(source.Key).Append('\0').Append(source.Value).Append('\0');
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
    }

    public Task<JObject> Compile(ProjectSnapshot project)
    {
        string hash = Fingerprint(project);
        lock (processLock)
        {
            if (disposed)
                return Task.FromException<JObject>(new ObjectDisposedException(nameof(CompilerClient)));
            if (requests.TryGetValue(hash, out var existing))
            {
                requestUse[hash] = Interlocked.Increment(ref nextUse);
                return existing;
            }
            var work = new Work(project, hash, retention.Pin(project.Directory));
            if (!queue.TryAdd(work))
            {
                work.Pin.Dispose();
                return Task.FromException<JObject>(new IOException("Compiler queue is full. Wait for the current build, then press Play again."));
            }
            requests[hash] = work.Completion.Task;
            requestUse[hash] = Interlocked.Increment(ref nextUse);
            return work.Completion.Task;
        }
    }

    public void Warm(ProjectSnapshot project)
    {
        lock (processLock)
        {
            if (disposed)
                return;
            warmPin?.Dispose();
            latestWarm = project;
            warmPin = retention.Pin(project.Directory);
            if (warming)
                return;
            warming = true;
        }
        Task.Run(async () =>
        {
            while (true)
            {
                ProjectSnapshot next;
                IDisposable? pin;
                lock (processLock)
                {
                    if (disposed || latestWarm == null)
                    {
                        warming = false;
                        return;
                    }
                    next = latestWarm;
                    latestWarm = null;
                    pin = warmPin;
                    warmPin = null;
                }
                try
                {
                    var task = Compile(next); // Queue/cache ownership is registered before releasing the warm pin.
                    pin?.Dispose();
                    pin = null;
                    await task.ConfigureAwait(false);
                }
                catch (Exception error) { log("Background Scala build: " + error.Message); }
                finally { pin?.Dispose(); }
            }
        });
    }

    private void Loop()
    {
        foreach (var work in queue.GetConsumingEnumerable())
        {
            lock (cacheOperations)
            {
                IDisposable? artifactPin = null;
                try
                {
                    var compiler = EnsureProcess();
                    long id = Interlocked.Increment(ref nextId);
                    // One stable project preserves sbt/Zinc state across immutable editor snapshots.
                    string output = Path.Combine(cache, "compiler-project");
                    var request = new JObject
                    {
                        ["id"] = id,
                        ["op"] = "compile",
                        ["project"] = work.Project.Directory,
                        ["entry"] = work.Project.EntryFile,
                        ["output"] = output
                    };
                    compiler.StandardInput.WriteLine(request.ToString(Formatting.None));
                    compiler.StandardInput.Flush();
                    string? line;
                    JObject? reply = null;
                    while ((line = compiler.StandardOutput.ReadLine()) != null)
                    {
                        if (line.Length > 4 * 1024 * 1024)
                            throw new IOException("Compiler response exceeds the protocol limit.");
                        try
                        {
                            reply = JObject.Parse(line);
                        }
                        catch (JsonException) { log("Compiler: " + line); continue; }
                        if ((long?)reply["id"] == id)
                            break;
                        reply = null;
                    }
                    if (reply == null)
                        throw new IOException("Scala compiler disconnected before returning a build result.");
                    if ((bool?)reply["ok"] == true)
                    {
                        string artifact = (string?)reply["artifact"] ?? throw new IOException("Compiler omitted its artifact.");
                        string immutable = Path.Combine(cache, "artifacts", work.Hash);
                        string copy = Path.Combine(immutable, "main.js");
                        artifactPin = retention.Pin(copy, artifact);
                        Directory.CreateDirectory(immutable);
                        File.Copy(artifact, copy, true);
                        reply["artifact"] = copy;
                        string? map = (string?)reply["sourceMap"];
                        if (!string.IsNullOrEmpty(map) && File.Exists(map))
                        {
                            File.Copy(map!, copy + ".map", true);
                            reply["sourceMap"] = copy + ".map";
                        }
                        if (File.Exists(artifact + ".editor-map.json"))
                            File.Copy(artifact + ".editor-map.json", copy + ".editor-map.json", true);
                        string sourceSnapshot = Path.Combine(Path.GetDirectoryName(artifact)!, "sources.json");
                        if (File.Exists(sourceSnapshot))
                            File.Copy(sourceSnapshot, Path.Combine(immutable, "sources.json"), true);
                        string classpath = Path.Combine(Path.GetDirectoryName(artifact)!, "compiler-classpath.json");
                        if (File.Exists(classpath))
                            File.Copy(classpath, Path.Combine(immutable, "compiler-classpath.json"), true);
                        reply["sourceHash"] = work.Hash;
                        lock (processLock)
                        {
                            if (disposed)
                                throw new ObjectDisposedException(nameof(CompilerClient));
                            artifactOrigins[work.Hash] = artifact;
                            requestPins[work.Hash] = artifactPin;
                            artifactPin = null;
                        }
                    }
                    else
                        RemoveRequest(work.Hash); // An unchanged project may recover after tool/cache repair.
                    work.Completion.TrySetResult(reply);
                    // Keep only a bounded number of completed requests; artifacts are immutable.
                    if (requests.Count > 64)
                        foreach (var old in requests.Where(p => p.Value.IsCompleted && p.Key != work.Hash)
                            .OrderBy(p => requestUse.TryGetValue(p.Key, out long use) ? use : 0).Take(32))
                            RemoveRequest(old.Key);
                }
                catch (Exception error)
                {
                    RemoveRequest(work.Hash);
                    work.Completion.TrySetException(error);
                    lock (processLock)
                    {
                        job?.Dispose();
                        job = null; // Retire sbt/Java too before a replacement can take its locks.
                        try
                        {
                            if (process != null && !process.HasExited)
                                process.Kill();
                        }
                        catch { }
                        process?.Dispose();
                        process = null;
                    }
                }
                finally
                {
                    artifactPin?.Dispose();
                    work.Pin.Dispose();
                    retention.Prune();
                }
            }
        }
    }

    private void RemoveRequest(string hash)
    {
        requests.TryRemove(hash, out _);
        requestUse.TryRemove(hash, out _);
        artifactOrigins.TryRemove(hash, out _);
        if (requestPins.TryRemove(hash, out var pin))
            pin.Dispose();
    }

    private Process EnsureProcess()
    {
        lock (processLock)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(CompilerClient));
            if (process != null && !process.HasExited)
                return process;
            string dotnet = Path.Combine(root, "runtime", "dotnet.exe");
            string service = Path.Combine(root, "tools", "compiler", "FarmerScala.Compiler.dll");
            if (!File.Exists(dotnet) || !File.Exists(service))
                throw new FileNotFoundException("The bundled Scala compiler or .NET runtime is missing. Reinstall Farmer Scala.");
            var info = new ProcessStartInfo(dotnet, Quote(service) + " --sdk " + Quote(SdkProfile.Resolve(root)) + " --toolchain " + Quote(Path.Combine(root, "toolchain")))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = root
            };
            info.EnvironmentVariables["FARMER_SCALA_OFFLINE"] = "true";
            process = new Process { StartInfo = info };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) log("Compiler: " + e.Data); };
            if (!process.Start())
                throw new IOException("Could not start the Scala compiler service.");
            job = new ProcessJob();
            job.Add(process);
            process.BeginErrorReadLine();
            return process;
        }
    }

    public void Dispose()
    {
        lock (processLock)
        {
            if (disposed)
                return;
            disposed = true;
            retentionTimer.Dispose();
            warmPin?.Dispose();
            warmPin = null;
            latestWarm = null;
            queue.CompleteAdding();
            job?.Dispose();
            job = null;
            while (queue.TryTake(out var work))
            {
                work.Pin.Dispose();
                work.Completion.TrySetException(new ObjectDisposedException(nameof(CompilerClient)));
            }
            foreach (string hash in requests.Keys)
                RemoveRequest(hash);
            process?.Dispose();
            process = null;
        }
    }

    private static string Quote(string text) => "\"" + text.Replace("\"", "\\\"") + "\"";
    private sealed class Work
    {
        public readonly ProjectSnapshot Project;
        public readonly string Hash;
        public readonly IDisposable Pin;
        public readonly TaskCompletionSource<JObject> Completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        public Work(ProjectSnapshot project, string hash, IDisposable pin)
        {
            Project = project;
            Hash = hash;
            Pin = pin;
        }
    }
}
