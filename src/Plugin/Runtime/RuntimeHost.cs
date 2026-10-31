using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FarmerScala.Editor;
using FarmerScala.Game;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FarmerScala.Runtime;

internal enum CompilationProgress { None, Compiling, Done, Failed, Cancelled }

internal sealed class RuntimeHost : IDisposable
{
    private readonly Plugin plugin;
    private readonly string installRoot;
    private readonly ConcurrentQueue<Action> mainQueue = new ConcurrentQueue<Action>();
    private readonly CompilerClient compiler;
    private RunState? active;
    private PendingRun? pending;
    public bool IsRunning => active != null || pending != null;
    internal bool IsCompiling => pending != null;
    internal CompilationProgress CompilationState { get; private set; }
    public RuntimeHost(Plugin plugin, string installRoot)
    {
        this.plugin = plugin;
        this.installRoot = installRoot;
        compiler = new CompilerClient(installRoot, Path.Combine(BepInEx.Paths.CachePath, "FarmerScala"), plugin.LogInfo);
        compiler.ArtifactReady += artifact => mainQueue.Enqueue(() => ScalaAnalysis.CompilerReady(artifact));
    }
    public void ProjectChanged(ProjectSnapshot project) => compiler.Warm(project);
    internal void PruneCache() => compiler.PruneCache();

    public void Run(CodeWindow window, bool stepping = false)
    {
        if (IsRunning)
        {
            Stop("Stopped");
            return;
        }
        if (MainSim.Inst == null || MainSim.Inst.IsExecuting())
            throw new InvalidOperationException("Stop the current game program first.");
        var project = EditorIntegration.CaptureProject(window);
        var request = new PendingRun(project, window, stepping, compiler.KeepRun(project.Directory));
        pending = request;
        CompilationState = CompilationProgress.Compiling;
        compiler.Compile(project).ContinueWith(task => mainQueue.Enqueue(() =>
        {
            try
            {
                if (pending != request)
                    return; // Stop/save reload revokes this compilation's authority.
                pending = null;
                if (task.IsFaulted || task.IsCanceled)
                {
                    CompilationState = task.IsCanceled ? CompilationProgress.Cancelled : CompilationProgress.Failed;
                    string message = task.Exception?.GetBaseException().Message ?? "Scala compilation was cancelled.";
                    plugin.QueueOutput(message);
                    EditorIntegration.SetError(project.EntryFile, 1, 1, message);
                    return;
                }
                JObject result = task.Result;
                if ((bool?)result["ok"] != true)
                {
                    CompilationState = CompilationProgress.Failed;
                    var diagnostic = (result["diagnostics"] as JArray)?.OfType<JObject>().FirstOrDefault();
                    string message = (string?)diagnostic?["message"] ?? (string?)result["error"] ?? "Scala compilation failed.";
                    plugin.QueueOutput(message);
                    EditorIntegration.SetError((string?)diagnostic?["file"] ?? project.EntryFile,
                        (int?)diagnostic?["line"] ?? 1, (int?)diagnostic?["column"] ?? 1, message, project.EntryFile,
                        true, (int?)diagnostic?["endLine"], (int?)diagnostic?["endColumn"]);
                    return;
                }
                CompilationState = CompilationProgress.Done;
                try
                {
                    StartCompiled(request, (string?)result["artifact"] ?? throw new IOException("Missing Scala.js artifact."));
                }
                catch (Exception error) { Stop("Start failed"); plugin.QueueOutput(error.Message); EditorIntegration.SetError(project.EntryFile, 1, 1, error.Message); }
            }
            finally { request.ReleaseCache(); }
        }), TaskScheduler.Default);
    }

    private void StartCompiled(PendingRun request, string artifact)
    {
        if (!EditorIntegration.Enabled || request.Window == null)
            return;
        var project = request.Project;
        var window = request.Window;
        bool stepping = request.Stepping;
        IDisposable pin = compiler.KeepRun(project.Directory, artifact);
        RunState state;
        try
        {
            state = new RunState(project.Directory, project.EntryFile, artifact, window, plugin.ReportFailure, pin) { StepMode = stepping };
        }
        catch { pin.Dispose(); throw; }
        active = state;
        try
        {
            state.Session.TaskStarting += (drone, task) => mainQueue.Enqueue(() =>
            {
                if (active != state || state.NativeStopping || state.RetiredDrones.ContainsKey(drone))
                    return;
                try
                {
                    SpawnWorker(state, drone, task);
                }
                catch (Exception e) { Fail(state, e.Message); }
            });
            state.Session.DroneRemoved += drone =>
            {
                // Retirement can precede the main-thread queued launch.
                state.RetiredDrones.TryAdd(drone, 0);
                if (!state.Workers.TryGetValue(drone, out var worker))
                    return;
                worker.Retired = true;
                worker.Completed = true;
                try
                {
                    worker.Connection?.Dispose();
                }
                catch { }
                try
                {
                    if (!worker.Process.HasExited)
                        worker.Process.Kill();
                }
                catch { }
            };
            state.Listener.Start(36);
            new Thread(() => AcceptLoop(state)) { IsBackground = true, Name = "FarmerScala accept" }.Start();
            global::Logger.Close(); // Flush output left after an earlier run before native Clear drops its writer.
            MainSim.Inst.StartMainExecution(window, state.Session.RootNode);
            EditorIntegration.BeginRun(window);
            SpawnWorker(state, 0, null);
            if (stepping)
                EditorIntegration.BeginStep(window);
            plugin.LogInfo("Scala run " + state.Id + " started: " + project.EntryFile);
        }
        catch { Stop("Start failed"); throw; }
    }

    private void SpawnWorker(RunState run, int drone, JObject? task)
    {
        if (run.Workers.ContainsKey(drone))
            throw new InvalidOperationException("Duplicate Scala drone " + drone);
        string executable = Path.Combine(installRoot, "runtime", "dotnet.exe");
        string workerDll = Path.Combine(installRoot, "tools", "worker", "FarmerScala.Worker.dll");
        if (!File.Exists(executable) || !File.Exists(workerDll))
            throw new FileNotFoundException("Bundled Scala execution worker is missing. Reinstall Farmer Scala.", executable);
        var args = new StringBuilder(Quote(workerDll)).Append(" --artifact ").Append(Quote(run.Artifact));
        if (task == null)
            args.Append(" --entry ").Append(Quote(run.Entry));
        else if ((string?)task["$"] == "program")
        {
            string globalsFile = Path.Combine(run.Project, "globals-" + drone + ".json");
            File.WriteAllText(globalsFile, (task["globals"] ?? new JObject()).ToString(Formatting.None), new UTF8Encoding(false));
            string entry = (string?)task["entry"] ?? throw new IOException("Simulation entry missing.");
            if (!entry.EndsWith(".scala", StringComparison.OrdinalIgnoreCase))
                entry += ".scala";
            args.Append(" --entry ").Append(Quote(entry));
            args.Append(" --globals-file ").Append(Quote(globalsFile));
        }
        else
        {
            string taskFile = Path.Combine(run.Project, "task-" + drone + ".json");
            File.WriteAllText(taskFile, task.ToString(Formatting.None), new UTF8Encoding(false));
            args.Append(" --task-file ").Append(Quote(taskFile));
            args.Append(" --entry ").Append(Quote((string?)task["entry"] ?? run.Entry));
        }
        args.Append(" --project ").Append(Quote(run.Project));
        var info = new ProcessStartInfo(executable, args.ToString())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = run.Project,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.EnvironmentVariables["TFWR_PORT"] = ((IPEndPoint)run.Listener.LocalEndpoint).Port.ToString();
        info.EnvironmentVariables["TFWR_TOKEN"] = run.Token;
        info.EnvironmentVariables["TFWR_RUN_ID"] = run.Id;
        info.EnvironmentVariables["TFWR_DRONE_ID"] = drone.ToString();
        if (run.StepMode)
            info.EnvironmentVariables["TFWR_START_PAUSED"] = "step";
        var worker = new WorkerState(new Process { StartInfo = info });
        run.Workers[drone] = worker;
        try
        {
            worker.Process.OutputDataReceived += (_, e) => { if (e.Data != null) plugin.QueueOutput(e.Data); };
            worker.Process.ErrorDataReceived += (_, e) => { if (e.Data != null) plugin.QueueOutput(e.Data); };
            if (!worker.Process.Start())
                throw new IOException("Could not launch Scala.");
            run.Job.Add(worker.Process);
            worker.Pid = worker.Process.Id;
            worker.Process.BeginOutputReadLine();
            worker.Process.BeginErrorReadLine();
        }
        catch { try { worker.Process.Kill(); } catch { } throw; }
    }

    private void AcceptLoop(RunState run)
    {
        try
        {
            while (active == run)
            {
                var client = run.Listener.AcceptTcpClient();
                client.ReceiveTimeout = 5000;
                new Thread(() => Serve(run, client)) { IsBackground = true, Name = "FarmerScala worker" }.Start();
            }
        }
        catch (Exception e) { if (active == run) mainQueue.Enqueue(() => Fail(run, "Worker listener: " + e.Message)); }
    }

    private void Serve(RunState run, TcpClient client)
    {
        WorkerState? worker = null;
        using var connection = new WorkerConnection(client);
        try
        {
            var hello = connection.Read() ?? throw new IOException("No worker handshake.");
            if ((string?)hello["kind"] != "hello" || (int?)hello["v"] != 1 || (string?)hello["token"] != run.Token ||
                (string?)hello["run"] != run.Id || !int.TryParse((string?)hello["drone"], out int drone) ||
                !run.Workers.TryGetValue(drone, out worker) || worker.Connection != null || worker.Pid != (int?)hello["pid"])
                throw new IOException("Invalid worker handshake.");
            worker.Connection = connection;
            if (worker.Retired || run.RetiredDrones.ContainsKey(drone))
                return;
            client.ReceiveTimeout = 0;
            var response = (JObject)run.Manifest.DeepClone();
            response["kind"] = "hello";
            response["v"] = 1;
            response["run"] = run.Id;
            response["drone"] = drone.ToString();
            response["ok"] = true;
            connection.Send(response);
            while (active == run && !run.NativeStopping && !worker.Retired)
            {
                var message = connection.Read();
                if (message == null || worker.Retired)
                    break;
                if ((int?)message["v"] != 1 || (string?)message["run"] != run.Id || (string?)message["drone"] != drone.ToString())
                    throw new IOException("Worker message identity mismatch.");
                string? kind = (string?)message["kind"];
                if (kind == "request")
                {
                    var captured = message;
                    long id = (long?)captured["id"] ?? throw new IOException("Request ID missing.");
                    if ((string?)captured["op"] == "leaderboard_run")
                        throw new IOException("Leaderboards are disabled in Scala mode.");
                    Timer? heartbeat = null;
                    var completionLock = new object();
                    bool completed = false;
                    heartbeat = new Timer(_ =>
                    {
                        lock (completionLock)
                        {
                            if (completed)
                                return;
                            try
                            {
                                connection.Send(Envelope(run, drone, id, "pending"));
                            }
                            catch { }
                        }
                    }, null, 1000, 1000);
                    run.Session.Enqueue(drone, captured, value =>
                    {
                        lock (completionLock)
                        {
                            completed = true;
                            heartbeat.Dispose();
                            if (active != run)
                                return;
                            var reply = Envelope(run, drone, id, "response");
                            reply["ok"] = true;
                            reply["value"] = value;
                            try
                            {
                                connection.Send(reply);
                            }
                            catch { }
                        }
                    }, (code, error) =>
                    {
                        lock (completionLock)
                        {
                            completed = true;
                            heartbeat.Dispose();
                            if (active != run)
                                return;
                            var reply = Envelope(run, drone, id, "response");
                            reply["ok"] = false;
                            reply["error"] = new JObject { ["code"] = code, ["message"] = error };
                            try
                            {
                                connection.Send(reply);
                            }
                            catch { }
                        }
                    });
                }
                else if (kind == "event")
                {
                    string? name = (string?)message["event"];
                    if (name == "completed")
                    {
                        worker.Completed = true;
                        run.Session.CompleteDrone(drone, message["result"]);
                    }
                    else if (name == "fault")
                    {
                        worker.Completed = true;
                        string error = (string?)message["message"] ?? (string?)message["error"]?["message"] ?? message.ToString(Formatting.None);
                        mainQueue.Enqueue(() =>
                        {
                            if (!run.NativeStopping && !worker.Retired)
                                Fail(run, error, (string?)message["file"],
                                (int?)message["line"] ?? 1, (int?)message["column"] ?? 1,
                                (int?)message["endLine"], (int?)message["endColumn"]);
                        });
                    }
                    else if (name == "paused")
                    {
                        string file = (string?)message["file"] ?? run.Entry;
                        int line = (int?)message["line"] ?? 1;
                        mainQueue.Enqueue(() => { if (active == run && !run.NativeStopping && !worker.Retired) EditorIntegration.Highlight(file, line); });
                    }
                }
            }
            if (active == run && !run.NativeStopping && worker != null && !worker.Completed)
                mainQueue.Enqueue(() =>
                {
                    if (active == run && !run.NativeStopping && !worker.Completed)
                        Fail(run, "Scala worker disconnected unexpectedly.");
                });
        }
        catch (Exception e)
        {
            if (active == run && !run.NativeStopping && (worker == null || !worker.Completed))
                mainQueue.Enqueue(() =>
                {
                    // clear() may intentionally retire another drone while this
                    // thread is blocked in Read. Recheck at dispatch as well:
                    // retirement can occur after a disconnect was queued.
                    if (active == run && !run.NativeStopping && (worker == null || !worker.Completed))
                        Fail(run, "Scala connection: " + e.Message);
                });
        }
    }

    private static JObject Envelope(RunState run, int drone, long id, string kind) => new JObject
    {
        ["kind"] = kind,
        ["v"] = 1,
        ["run"] = run.Id,
        ["drone"] = drone.ToString(),
        ["id"] = id
    };

    public void Tick()
    {
        for (int i = 0; i < 100 && mainQueue.TryDequeue(out var action); i++)
            action();
        var run = active;
        if (run == null)
            return;
        if (run.NativeStopping)
        {
            Stop("Stopped by game");
            return;
        }
        string? fault = run.Session.PollFault();
        if (!string.IsNullOrEmpty(fault))
        {
            var location = run.Session.FaultLocation;
            Fail(run, fault, (string?)location?["file"], (int?)location?["line"] ?? 1,
                (int?)location?["column"] ?? 1, (int?)location?["endLine"], (int?)location?["endColumn"]);
            return;
        }
        foreach (var worker in run.Workers.Values)
            if (!worker.Completed && worker.Connection == null && DateTime.UtcNow - worker.Started > TimeSpan.FromSeconds(15))
            {
                Fail(run, "Scala worker did not connect within 15 seconds.");
                return;
            }
        if (!MainSim.Inst.IsExecuting() && !run.Session.HasLiveExecution)
            Stop(run.Workers.Values.All(w => w.Completed) ? "Scala run complete" : "Game execution ended");
    }

    private void Fail(RunState run, string message, string? file = null, int line = 1,
        int column = 1, int? endLine = null, int? endColumn = null)
    {
        if (active != run)
            return;
        plugin.QueueOutput("Scala error: " + message);
        Stop("Scala error");
        bool nativeSource = file == null || !Path.IsPathRooted(file) ||
            StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(file)?.TrimEnd(Path.DirectorySeparatorChar),
                run.Project.TrimEnd(Path.DirectorySeparatorChar));
        EditorIntegration.SetError(file ?? run.Entry, line, column, message, run.Entry, nativeSource, endLine, endColumn);
    }
    public void Stop(string reason)
    {
        var preparing = pending;
        if (preparing != null) CompilationState = CompilationProgress.Cancelled;
        pending = null;
        preparing?.ReleaseCache();
        var run = active;
        if (run == null)
            return;
        active = null; // Close generation first; queued responses lose authority immediately.
        CloseWorkerConnections(run); // Interrupt blocked writes before cancellation callbacks take their locks.
        run.Session.Cancel(); // Unblock simulation thread before native Stop takes its locks.
        run.Listener.Stop();
        run.Job.Dispose();
        try
        {
            MainSim.Inst?.StopMainExecution();
        }
        catch (Exception e) { plugin.LogError(e.ToString()); }
        foreach (var worker in run.Workers.Values)
        {
            try
            {
                worker.Process.Dispose();
            }
            catch { }
        }
        run.Session.Dispose();
        run.CachePin.Dispose(); // Workers and native callbacks are retired before their files become eligible.
        EditorIntegration.EndRun();
        plugin.LogInfo(reason);
    }
    public void Dispose()
    {
        Stop("Plugin shutdown");
        compiler.Dispose();
    }
    public void PrepareNativeStop()
    {
        var preparing = pending;
        if (preparing != null) CompilationState = CompilationProgress.Cancelled;
        pending = null;
        preparing?.ReleaseCache();
        var run = active;
        if (run == null)
            return;
        run.NativeStopping = true;
        CloseWorkerConnections(run);
        run.Session.Cancel();
    }
    private static void CloseWorkerConnections(RunState run)
    {
        foreach (var worker in run.Workers.Values)
        {
            try
            {
                worker.Connection?.Dispose();
            }
            catch { }
        }
    }
    public Node? ParseSimulationWindow(CodeWindow window) => active != null && SimulationParseScope.Active ? active.Session.CreateSimulationNode(window) : null;
    public void Step(CodeWindow window)
    {
        if (active == null)
        {
            Run(window, true);
            return;
        }
        string action = active.StepMode ? "step" : "pause";
        active.StepMode = true;
        EditorIntegration.BeginStep(window);
        foreach (var pair in active.Workers)
        {
            try
            {
                pair.Value.Connection?.Send(new JObject { ["kind"] = "control", ["v"] = 1, ["run"] = active.Id, ["drone"] = pair.Key.ToString(), ["action"] = action });
            }
            catch (Exception e) { plugin.LogError(e.Message); }
        }
    }
    public bool ResumeIfStepping(CodeWindow window)
    {
        var run = active;
        if (run == null || !run.StepMode)
            return false;
        run.StepMode = false;
        EditorIntegration.EndStep();
        foreach (var pair in run.Workers)
        {
            try
            {
                pair.Value.Connection?.Send(new JObject { ["kind"] = "control", ["v"] = 1, ["run"] = run.Id, ["drone"] = pair.Key.ToString(), ["action"] = "resume" });
            }
            catch (Exception e) { plugin.LogError(e.Message); }
        }
        return true;
    }
    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

    private sealed class WorkerState
    {
        public readonly Process Process;
        public readonly DateTime Started = DateTime.UtcNow;
        public int Pid;
        public volatile bool Completed;
        public volatile bool Retired;
        public WorkerConnection? Connection;
        public WorkerState(Process process)
        {
            Process = process;
        }
    }
    private sealed class RunState
    {
        public readonly string Id = Guid.NewGuid().ToString("N"), Token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        public readonly string Project, Entry, Artifact;
        public bool StepMode;
        public volatile bool NativeStopping;
        public readonly NativeGameSession Session;
        public readonly JObject Manifest;
        public readonly TcpListener Listener = new TcpListener(IPAddress.Loopback, 0);
        public readonly ProcessJob Job = new ProcessJob();
        public readonly IDisposable CachePin;
        public readonly ConcurrentDictionary<int, WorkerState> Workers = new ConcurrentDictionary<int, WorkerState>();
        public readonly ConcurrentDictionary<int, byte> RetiredDrones = new ConcurrentDictionary<int, byte>();
        public RunState(string project, string entry, string artifact, CodeWindow window, Action<string> log, IDisposable cachePin)
        {
            Project = project;
            Entry = entry;
            Artifact = artifact;
            CachePin = cachePin;
            Session = new NativeGameSession(window, log);
            Manifest = Session.BuildManifest();
        }
    }
    private sealed class PendingRun
    {
        public readonly ProjectSnapshot Project;
        public readonly CodeWindow Window;
        public readonly bool Stepping;
        private IDisposable? cachePin;
        public PendingRun(ProjectSnapshot project, CodeWindow window, bool stepping, IDisposable cachePin)
        {
            Project = project;
            Window = window;
            Stepping = stepping;
            this.cachePin = cachePin;
        }
        public void ReleaseCache() => Interlocked.Exchange(ref cachePin, null)?.Dispose();
    }
}
