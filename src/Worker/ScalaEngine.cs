using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Debugger;

namespace FarmerScala.Worker;

public sealed class ScalaEngine
{
    private readonly WorkerTransport transport;
    private readonly string entry;
    private readonly string digest;
    private readonly string script;
    private readonly ReplayJournal journal;
    private readonly JsonObject globals;
    private readonly SourceMapping mappings;
    private readonly Engine engine;
    private readonly AutoResetEvent resume = new(false);
    private readonly object controls = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private JsValue? captured;
    private bool pauseRequested;
    private bool started;
    private string? lastPaused;
    private EditorLocation? lastUserLocation;

    public ScalaEngine(WorkerTransport transport, string artifact, string entry, JsonObject? task = null, bool startPaused = false, JsonNode? simulationGlobals = null)
    {
        this.transport = transport;
        this.entry = entry;
        byte[] artifactBytes = File.ReadAllBytes(artifact);
        digest = Convert.ToHexString(SHA256.HashData(artifactBytes));
        script = new UTF8Encoding(false, true).GetString(artifactBytes);
        if (task is not null && (string?)task["artifactDigest"] != digest)
            throw new IOException("The drone task does not match this exact compiled Scala artifact.");
        journal = new(ReplayJournal.DecodeTask(task));
        globals = DecodeGlobals(task?["globals"] ?? simulationGlobals);
        if (task is not null && journal.StageCount == 0)
            throw new IOException("Drone task has no closure checkpoint.");
        mappings = new(artifact);
        // Jint's allocation quota is cumulative per entry, so it would eventually stop a healthy
        // long-running farming loop. Journal memory is bounded separately; Stop cancels the engine.
        engine = new Engine(options => options.DebugMode().CancellationToken(transport.Cancellation));
        engine.SetValue("__farmerAction", new Func<string, string, string>(Action));
        engine.SetValue("__farmerSpawn", new Func<JsValue, string>(Spawn));
        engine.SetValue("__farmerRandom", new Func<double>(() => Nondeterministic("random", Random.Shared.NextDouble)));
        engine.SetValue("__farmerNow", new Func<double>(() => Nondeterministic("now", () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
        engine.SetValue("__farmerPerformanceNow", new Func<double>(() => Nondeterministic("performance", () => clock.Elapsed.TotalMilliseconds)));
        engine.SetValue("__farmerGlobalsJson", globals.ToJsonString());
        engine.Execute("""
            globalThis.__farmerGlobals = JSON.parse(__farmerGlobalsJson);
            delete globalThis.__farmerGlobalsJson;
            Math.random = function() { return __farmerRandom(); };
            (function() {
              const NativeDate = Date;
              function ReplayDate(...args) {
                if (new.target) return Reflect.construct(NativeDate, args.length ? args : [__farmerNow()]);
                return new NativeDate(__farmerNow()).toString();
              }
              ReplayDate.prototype = NativeDate.prototype;
              ReplayDate.prototype.constructor = ReplayDate;
              ReplayDate.now = function() { return __farmerNow(); };
              ReplayDate.parse = NativeDate.parse;
              ReplayDate.UTC = NativeDate.UTC;
              Date = ReplayDate;
              globalThis.performance = {now: function() { return __farmerPerformanceNow(); }};
            })();
            """, "farmer-bootstrap.js");
        engine.Debugger.Skip += OnPoint;
        engine.Debugger.Step += OnPoint;
        transport.Control += Control;
        pauseRequested = startPaused;
    }

    public JsonNode? Run()
    {
        if (journal.StageCount == 0)
        {
            engine.Execute(script, "main.js");
            NotifyStarted();
            return ParseResult(InvokeEntry());
        }
        journal.BeginStage(0);
        try
        {
            engine.Execute(script, "main.js");
            InvokeEntry();
            throw new ReplayMismatchException("The original entry ended before the captured spawn point.");
        }
        catch (ReplayCheckpointException) { }
        for (int stage = 1; stage < journal.StageCount; stage++)
        {
            JsValue callback = captured ?? throw new ReplayMismatchException("Missing reconstructed closure.");
            captured = null;
            journal.BeginStage(stage);
            try
            {
                engine.Invoke(callback);
                throw new ReplayMismatchException("The parent drone ended before its captured spawn point.");
            }
            catch (ReplayCheckpointException) { }
        }
        JsValue target = captured ?? throw new ReplayMismatchException("No closure was captured.");
        captured = null;
        journal.EndReplay();
        JsonNode? payload = ParseResult(engine.Invoke(target));
        return new JsonObject { ["$"] = "scala", ["value"] = payload };
    }

    private JsValue InvokeEntry()
    {
        // Scala.js emits a lexical `let runFarm`; it is not necessarily a property of globalThis.
        return engine.Invoke(engine.Evaluate("runFarm"), entry);
    }

    private static JsonNode? ParseResult(JsValue result)
    {
        if (result.IsUndefined() || result.IsNull())
            return null;
        if (!result.IsString())
            throw new IOException("The compiled Scala entry must return an encoded JSON string.");
        return JsonNode.Parse(result.AsString());
    }

    private string Action(string op, string json)
    {
        if (op == "leaderboard_run")
            throw new JavaScriptException(engine.Intrinsics.Error, "Leaderboards are disabled in Scala mode.");
        JsonArray args = JsonNode.Parse(json) as JsonArray ?? throw new IOException("Game arguments must be a JSON array.");
        JsonObject response;
        if (journal.Replay("action", op, args) is { } replay)
            response = replay;
        else
        {
            response = transport.Request(op, args, lastUserLocation is null ? null : LocationFields(lastUserLocation));
            journal.Record(new JsonObject
            {
                ["kind"] = "action",
                ["op"] = op,
                ["args"] = args.DeepClone(),
                ["ok"] = response["ok"]?.DeepClone(),
                ["value"] = response["value"]?.DeepClone(),
                ["error"] = response["error"]?.DeepClone()
            });
        }
        return Value(response);
    }

    private string Spawn(JsValue callback)
    {
        if (journal.AtCheckpoint)
        {
            captured = callback;
            // Deliberately a CLR exception: user Scala/JS catch clauses cannot swallow the replay checkpoint.
            throw new ReplayCheckpointException();
        }
        if (journal.Replay("spawn") is { } replay)
            return Value(replay);
        JsonObject task = journal.Capture(digest, entry);
        task["globals"] = globals.DeepClone();
        if (Encoding.UTF8.GetByteCount(task.ToJsonString()) > ReplayJournal.MaximumTaskBytes)
            throw new ReplayCapacityException("This drone's replay journal and simulation globals exceed the worker task budget.");
        JsonObject response = transport.Request("spawn_drone", new JsonArray(task), lastUserLocation is null ? null : LocationFields(lastUserLocation));
        journal.Record(new JsonObject
        {
            ["kind"] = "spawn",
            ["ok"] = response["ok"]?.DeepClone(),
            ["value"] = response["value"]?.DeepClone(),
            ["error"] = response["error"]?.DeepClone()
        });
        return Value(response);
    }

    private string Value(JsonObject response)
    {
        if ((bool?)response["ok"] != true)
        {
            string error = (string?)response["error"]?["message"] ?? "The game rejected this action.";
            throw new JavaScriptException(engine.Intrinsics.Error, error);
        }
        JsonNode? value = response["value"];
        if (value is JsonObject tagged && (string?)tagged["$"] == "scala")
            value = tagged["value"];
        return value?.ToJsonString() ?? "null";
    }

    private double Nondeterministic(string name, Func<double> generate)
    {
        if (journal.Replay("nondeterministic", name) is { } replay)
            return (double?)replay["value"] ?? throw new IOException("Invalid recorded nondeterministic value.");
        double value = generate();
        journal.Record(new JsonObject { ["kind"] = "nondeterministic", ["op"] = name, ["value"] = value });
        return value;
    }

    private void Control(string action)
    {
        lock (controls)
        {
            switch (action)
            {
                case "pause":
                    pauseRequested = true;
                    break;
                case "step":
                    pauseRequested = true;
                    resume.Set();
                    break;
                case "resume":
                    pauseRequested = false;
                    lastPaused = null;
                    resume.Set();
                    break;
            }
        }
    }

    private void NotifyStarted()
    {
        if (started) return;
        started = true;
        transport.Event("started");
    }

    private StepMode OnPoint(object sender, DebugInformation info)
    {
        if (journal.IsReplaying)
            return StepMode.None;
        if (info.Location.SourceFile != "main.js")
            return StepMode.None;
        EditorLocation? location = mappings.Map(info.Location.Start.Line, info.Location.Start.Column);
        if (location is null)
            return StepMode.None;
        NotifyStarted();
        lastUserLocation = location;
        string key = location.File + ":" + location.Line + ":" + location.Column;
        bool shouldPause;
        lock (controls)
            shouldPause = pauseRequested && key != lastPaused;
        if (!shouldPause)
            return StepMode.None;
        lastPaused = key;
        transport.Event("paused", LocationFields(location));
        WaitHandle.WaitAny([resume, transport.Cancellation.WaitHandle]);
        transport.Cancellation.ThrowIfCancellationRequested();
        return StepMode.None;
    }

    public JsonObject Fault(Exception exception)
    {
        EditorLocation? location = null;
        if (exception is JavaScriptException javascript)
            location = mappings.Map(javascript.Location.Start.Line, javascript.Location.Start.Column)
                ?? mappings.MapStack(javascript.JavaScriptStackTrace);
        location ??= mappings.MapStack(exception.StackTrace);
        location ??= lastUserLocation;
        var fields = location is null ? new JsonObject { ["file"] = entry, ["line"] = 1, ["column"] = 1 } : LocationFields(location);
        fields["message"] = exception.Message;
        return fields;
    }

    private static JsonObject LocationFields(EditorLocation location) => new()
    {
        ["file"] = location.File,
        ["line"] = location.Line,
        ["column"] = location.Column,
        ["endLine"] = location.EndLine,
        ["endColumn"] = location.EndColumn
    };

    private static JsonObject DecodeGlobals(JsonNode? value)
    {
        if (value is null)
            return new();
        if (value is not JsonObject obj)
            throw new IOException("Simulation globals must be a dictionary.");
        if ((string?)obj["$"] != "dict")
            return (JsonObject)obj.DeepClone();
        var result = new JsonObject();
        foreach (var item in obj["items"] as JsonArray ?? new())
        {
            if (item is not JsonArray pair || pair.Count != 2 || pair[0] is not JsonValue key || !key.TryGetValue<string>(out var name))
                throw new IOException("Simulation global names must be strings.");
            JsonNode? payload = pair[1];
            if (payload is JsonObject tagged && (string?)tagged["$"] == "scala")
                payload = tagged["value"];
            result[name] = payload?.DeepClone();
        }
        return result;
    }
}
