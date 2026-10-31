using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FarmerScala.Worker;

int checks = 0;
string temporary = Path.Combine(Path.GetTempPath(), "FarmerScala-worker-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
string Artifact(string name, string script)
{
    string directory = Path.Combine(temporary, name);
    Directory.CreateDirectory(directory);
    string file = Path.Combine(directory, "main.js");
    File.WriteAllText(file, script);
    return file;
}
async Task<(JsonNode? Result, MockGame Game)> Run(string artifact, JsonObject? task = null, Func<JsonObject, JsonNode?>? action = null, JsonNode? globals = null, string entry = "main.scala", int timeoutSeconds = 15)
{
    var game = new MockGame(action);
    using var transport = new WorkerTransport("test-run", "0");
    var accept = game.Accept();
    transport.Connect(game.Port, "private-token");
    await accept;
    var engine = new ScalaEngine(transport, artifact, entry, task, simulationGlobals: globals);
    var result = await Task.Run(engine.Run).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
    game.Close();
    return (result, game);
}

try
{
    string basic = Artifact("basic", """
        function runFarm(entry) {
          const v = JSON.parse(__farmerAction('get_world_size', '[]'));
          __farmerAction('move', JSON.stringify([{'$':'enum',family:'Direction',name:'North'}]));
          return JSON.stringify([v, entry]);
        }
        """);
    var first = await Run(basic, action: request => (string?)request["op"] == "get_world_size" ? JsonValue.Create(7) : JsonValue.Create(true));
    Check((int?)first.Result?[0] == 7 && (string?)first.Result?[1] == "main.scala", "RPC values or entry dispatch wrong");
    Check(first.Game.Requests.Count == 2 && (string?)first.Game.Requests.Last()["args"]?[0]?["name"] == "North", "Native enum wire changed");

    var capturedTasks = new List<JsonObject>();
    string closure = Artifact("closure", """
        let outside = {x:3};
        function runFarm() {
          let captured = 10;
          const random = Math.random(), time = Date.now(), date = new Date().getTime();
          __farmerAction('move','[1]');
          try {
            __farmerSpawn(function() {
              captured += 7; outside.x += 2;
              __farmerAction('harvest',JSON.stringify([captured, outside.x, random, time, date]));
              return JSON.stringify({'$':'record',type:'Result',items:[captured,outside.x,random,time,date]});
            });
          } catch (e) { __farmerAction('wrong_catch','[]'); }
          captured = 99; outside.x = 100;
          __farmerAction('observe',JSON.stringify([captured,outside.x,random,time,date]));
          return 'null';
        }
        """);
    var parent = await Run(closure, action: request =>
    {
        if ((string?)request["op"] == "spawn_drone")
        {
            capturedTasks.Add((JsonObject)request["args"]![0]!.DeepClone());
            return JsonNode.Parse("{\"$\":\"drone\",\"id\":\"child-1\"}");
        }
        return null;
    });
    Check(capturedTasks.Count == 1 && parent.Game.Requests.Count == 3, "Parent spawn not captured exactly once");
    var child = await Run(closure, capturedTasks[0]);
    Check(child.Game.Requests.Count == 1 && (string?)child.Game.Requests[0]["op"] == "harvest", "Replay issued duplicate native actions or swallowed checkpoint");
    var expected = parent.Game.Requests.Last()["args"]!;
    var actual = child.Game.Requests[0]["args"]!;
    Check((int?)actual[0] == 17 && (int?)actual[1] == 5, "Child closure or object mutation was not isolated");
    Check(JsonNode.DeepEquals(actual[2], expected[2]) && JsonNode.DeepEquals(actual[3], expected[3]) && JsonNode.DeepEquals(actual[4], expected[4]), "Nondeterministic state was not reconstructed");
    Check((string?)child.Result?["$"] == "scala" && (int?)child.Result?["value"]?["items"]?[0] == 17, "Child result opaque wrapper wrong");

    string nested = Artifact("nested", """
        function runFarm() {
          let value = 2;
          __farmerAction('before','[2]');
          __farmerSpawn(function() {
            value += 3;
            __farmerAction('middle',JSON.stringify([value]));
            __farmerSpawn(function() {
              value *= 4;
              __farmerAction('last',JSON.stringify([value]));
              return JSON.stringify(value);
            });
            value = 90;
            return JSON.stringify(value);
          });
          return 'null';
        }
        """);
    JsonObject? nestedTask = null;
    var nestedParent = await Run(nested, action: request =>
    {
        if ((string?)request["op"] == "spawn_drone")
            nestedTask = (JsonObject)request["args"]![0]!.DeepClone();
        return null;
    });
    JsonObject? grandchildTask = null;
    var nestedChild = await Run(nested, nestedTask, request =>
    {
        if ((string?)request["op"] == "spawn_drone")
            grandchildTask = (JsonObject)request["args"]![0]!.DeepClone();
        return null;
    });
    Check(nestedChild.Game.Requests.Count == 2 && (int?)grandchildTask?["stageCount"] == 2, "Nested checkpoint stages missing");
    var grandchild = await Run(nested, grandchildTask);
    Check(grandchild.Game.Requests.Count == 1 && (int?)grandchild.Game.Requests[0]["args"]?[0] == 20 && (int?)grandchild.Result?["value"] == 20, "Nested closure replay wrong");

    string finalizer = Artifact("finalizer", """
        function runFarm() {
          let value = 7;
          try { __farmerSpawn(function() { __farmerAction('childValue',JSON.stringify([value])); return JSON.stringify(value); }); }
          finally { value = 999; __farmerAction('cleanup','[]'); }
          return 'null';
        }
        """);
    JsonObject? finalizerTask = null;
    await Run(finalizer, action: request =>
    {
        if ((string?)request["op"] == "spawn_drone")
            finalizerTask = (JsonObject)request["args"]![0]!.DeepClone();
        return null;
    });
    var finalizerChild = await Run(finalizer, finalizerTask);
    Check(finalizerChild.Game.Requests.Count == 1 && (int?)finalizerChild.Result?["value"] == 7, "Replay checkpoint ran user finally cleanup or mutated captured state while unwinding");

    string repeated = Artifact("repeated", """
        function runFarm() {
          let value = 2;
          __farmerSpawn(function() { return JSON.stringify(value); });
          value += 5;
          __farmerSpawn(function() { __farmerAction('value',JSON.stringify([value])); return JSON.stringify(value); });
          return 'null';
        }
        """);
    capturedTasks.Clear();
    await Run(repeated, action: request =>
    {
        if ((string?)request["op"] == "spawn_drone")
            capturedTasks.Add((JsonObject)request["args"]![0]!.DeepClone());
        return null;
    });
    var secondChild = await Run(repeated, capturedTasks[1]);
    Check(secondChild.Game.Requests.Count == 1 && (int?)secondChild.Result?["value"] == 7, "Earlier spawn was reissued while replaying second child");

    string wait = Artifact("wait", """
        function runFarm() { return __farmerAction('wait_for','[{"$":"drone","id":"1"}]'); }
        """);
    var waited = await Run(wait, action: _ => JsonNode.Parse("{\"$\":\"scala\",\"value\":{\"$\":\"record\",\"items\":[1,2]}}"));
    Check((string?)waited.Result?["$"] == "record" && waited.Result?["items"]?.AsArray().Count == 2, "wait_for did not unwrap opaque Scala result");

    string leaderboard = Artifact("ranked", "function runFarm(){try {__farmerAction('leaderboard_run','[]');} catch(e){return JSON.stringify(e.message);} throw Error('should reject');}");
    var ranked = await Run(leaderboard);
    Check(ranked.Game.Requests.Count == 0 && ((string?)ranked.Result)?.Contains("disabled") == true, "Ranked operation reached native bridge");

    string global = Artifact("globals", "function runFarm(){return JSON.stringify(__farmerGlobals.answer);}");
    var simulation = await Run(global, globals: JsonNode.Parse("{\"$\":\"dict\",\"items\":[[\"answer\",42]]}"));
    Check((int?)simulation.Result == 42, "Native simulation globals were not decoded");

    var tooLarge = new ReplayJournal(null);
    tooLarge.Record(new JsonObject { ["kind"] = "action", ["op"] = "harvest", ["args"] = new JsonArray(new string('x', ReplayJournal.MaximumJournalBytes + 1)) });
    bool capacity = false;
    try
    {
        tooLarge.Capture("digest", "main.scala");
    }
    catch (ReplayCapacityException) { capacity = true; }
    Check(capacity, "Replay journal did not enforce bounded memory/task size");

    string mismatch = Artifact("mismatch", "function runFarm(){__farmerAction('mismatch','[]'); return 'null';}");
    bool mismatched = false;
    try
    {
        await Run(mismatch, capturedTasks[0]);
    }
    catch (IOException) { mismatched = true; }
    Check(mismatched, "Artifact mismatch was accepted");

    string mapArtifact = Artifact("map", "// dummy\n");
    File.WriteAllText(mapArtifact + ".map", "{\"version\":3,\"sources\":[\"file:///build/main.scala\"],\"mappings\":\"AAEA\"}");
    File.WriteAllText(mapArtifact + ".editor-map.json", "{\"sources\":[{\"generated\":\"C:/build/main.scala\",\"file\":\"main.scala\",\"lineOffset\":2,\"columnOffset\":0}]}");
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(mapArtifact)!, "sources.json"), "{\"prefixLines\":2,\"sources\":{\"main.scala\":\"harvest()\"}}");
    var mapped = new SourceMapping(mapArtifact).Map(1, 0);
    Check(mapped?.Line == 1 && mapped.Column == 1 && mapped.EndColumn == 8, "Source map wrapper offset or token range wrong");

    string faultArtifact = Artifact("fault", "function runFarm(){\n__farmerAction('harvest','[]');\nreturn 'null';\n}");
    File.WriteAllText(faultArtifact + ".map", "{\"version\":3,\"sources\":[\"main.scala\"],\"mappings\":\";AAEA;AACA\"}");
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(faultArtifact)!, "sources.json"), "{\"prefixLines\":2,\"sources\":{\"main.scala\":\"harvest()\\n()\"}}");
    var faultGame = new MockGame(_ => new JsonObject { ["$mock-error"] = "Native game failure" });
    using (var transport = new WorkerTransport("test-run", "0"))
    {
        var accept = faultGame.Accept();
        transport.Connect(faultGame.Port, "private-token");
        await accept;
        var engine = new ScalaEngine(transport, faultArtifact, "main.scala");
        JsonObject? fault = null;
        try
        {
            engine.Run();
        }
        catch (Exception e) { fault = engine.Fault(e); }
        Check((string?)fault?["file"] == "main.scala" && (int?)fault["line"] == 1 && ((string?)fault["message"])?.Contains("Native game failure") == true, "Native RPC error did not map back to raw user source");
        faultGame.Close();
    }

    string loop = Artifact("loop", "function runFarm(){while(true){} }");
    var loopGame = new MockGame();
    using (var transport = new WorkerTransport("test-run", "0"))
    {
        var accept = loopGame.Accept();
        transport.Connect(loopGame.Port, "private-token");
        await accept;
        var running = Task.Run(() => new ScalaEngine(transport, loop, "main.scala").Run());
        await Task.Delay(100);
        var cancelTime = Stopwatch.StartNew();
        loopGame.Close();
        bool cancelled = false;
        try
        {
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception e) when (e is OperationCanceledException or Jint.Runtime.ExecutionCanceledException) { cancelled = true; }
        Check(cancelled && cancelTime.Elapsed < TimeSpan.FromSeconds(3), "Pure JS infinite loop did not observe closed-game cancellation");
    }

    string pauseArtifact = Artifact("pause", "function runFarm(){\n__farmerAction('harvest','[]');\nreturn 'null';\n}");
    File.WriteAllText(pauseArtifact + ".map", "{\"version\":3,\"sources\":[\"main.scala\"],\"mappings\":\";AAEA;AACA\"}");
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(pauseArtifact)!, "sources.json"), "{\"prefixLines\":2,\"sources\":{\"main.scala\":\"harvest()\\n()\"}}");
    var pauseGame = new MockGame();
    using (var transport = new WorkerTransport("test-run", "0"))
    {
        var accept = pauseGame.Accept();
        transport.Connect(pauseGame.Port, "private-token");
        await accept;
        var running = Task.Run(() => new ScalaEngine(transport, pauseArtifact, "main.scala", startPaused: true).Run());
        JsonObject paused = await pauseGame.Event.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check((string?)paused["event"] == "paused" && pauseGame.Requests.Count == 0, "Start-step did not pause before a user action");
        pauseGame.SendControl("resume");
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Check(pauseGame.Requests.Count == 1, "Resume did not release paused worker");
        pauseGame.Close();
    }

    int scalaArgument = Array.IndexOf(args, "--scala-artifact");
    if (scalaArgument >= 0)
    {
        string scalaArtifact = args[scalaArgument + 1];
        JsonObject? scalaTask = null;
        JsonNode? scalaChildResult = null;
        var scala = await Run(scalaArtifact, action: request =>
        {
            string? op = (string?)request["op"];
            if (op == "spawn_drone")
            {
                scalaTask = (JsonObject)request["args"]![0]!.DeepClone();
                var result = Run(scalaArtifact, scalaTask).GetAwaiter().GetResult();
                scalaChildResult = result.Result;
                Check(result.Game.Requests.Count == 0, "Actual Scala child replay issued parent native actions");
                return JsonNode.Parse("{\"$\":\"drone\",\"id\":\"scala-child\"}");
            }
            return op switch
            {
                "get_world_size" => JsonValue.Create(4),
                "move" => JsonValue.Create(true),
                "get_entity_type" => JsonNode.Parse("{\"$\":\"enum\",\"family\":\"Entities\",\"name\":\"Grass\"}"),
                "get_cost" => JsonNode.Parse("{\"$\":\"dict\",\"items\":[[{\"$\":\"enum\",\"family\":\"Items\",\"name\":\"Hay\"},12]]}"),
                "get_companion" => JsonNode.Parse("{\"$\":\"tuple\",\"items\":[{\"$\":\"enum\",\"family\":\"Entities\",\"name\":\"Tree\"},{\"$\":\"tuple\",\"items\":[2,3]}]}"),
                "measure" => JsonNode.Parse("{\"$\":\"tuple\",\"items\":[4,5]}"),
                "wait_for" => scalaChildResult?.DeepClone(),
                _ => null
            };
        });
        Check(scalaTask is not null && scalaChildResult is not null, "Actual Scala case-class drone execution missing");
        Check(scala.Game.Requests.Any(r => (string?)r["op"] == "quick_print"), "Actual Scala smoke never finished its assertions");
        var sourceGame = new MockGame(request => (string?)request["op"] == "get_world_size" ? new JsonObject { ["$mock-error"] = "Native game source mapping probe" } : null);
        using (var transport = new WorkerTransport("test-run", "0"))
        {
            var accept = sourceGame.Accept();
            transport.Connect(sourceGame.Port, "private-token");
            await accept;
            var engine = new ScalaEngine(transport, scalaArtifact, "main.scala");
            JsonObject? fault = null;
            try
            {
                engine.Run();
            }
            catch (Exception e) { fault = engine.Fault(e); }
            var snapshot = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(scalaArtifact)!, "sources.json")))!;
            int callLine = Array.FindIndex(((string)snapshot["sources"]!["main.scala"]!).Split('\n'), line => line.Contains("get_world_size()")) + 1;
            var queuedLocation = sourceGame.Requests.Last()["location"];
            Check((string?)queuedLocation?["file"] == "main.scala" && (int?)queuedLocation?["line"] == callLine && (int?)queuedLocation?["column"] > 1,
                "Queued native action lacks its Scala call-site location: " + queuedLocation?.ToJsonString());
            Check((string?)fault?["file"] == "main.scala" && (int?)fault["line"] == callLine && (int?)fault["column"] > 1, "Actual Scala native-error stack did not map to get_world_size call: " + fault?.ToJsonString());
            sourceGame.Close();
        }
        var helperGame = new MockGame(_ => new JsonObject { ["$mock-error"] = "nested native failure" });
        using (var transport = new WorkerTransport("test-run", "0"))
        {
            var accept = helperGame.Accept();
            transport.Connect(helperGame.Port, "private-token");
            await accept;
            var helperEngine = new ScalaEngine(transport, scalaArtifact, "nativeFault.scala");
            try { helperEngine.Run(); } catch (Exception) { }
            var queued = helperGame.Requests.Single()["location"];
            Check((string?)queued?["file"] == "nativeFault.scala" && (int?)queued?["line"] == 2 && (int?)queued?["column"] > 1,
                "nested helper native action lost its source file/line before host application: " + queued?.ToJsonString());
            helperGame.Close();
        }

        Console.WriteLine("Actual Scala.js SDK + isolated child replay passed.");
    }

    int replayArgument = Array.IndexOf(args, "--scala-replay-artifact");
    if (replayArgument >= 0)
    {
        string scalaArtifact = args[replayArgument + 1];
        JsonNode? result = null;
        bool replayed = false;
        var scalaReplay = await Run(scalaArtifact, action: request =>
        {
            if ((string?)request["op"] == "spawn_drone")
            {
                var task = (JsonObject)request["args"]![0]!.DeepClone();
                var child = Run(scalaArtifact, task, entry: "replay.scala").GetAwaiter().GetResult();
                Check(child.Game.Requests.Count == 0, "Actual Scala replay duplicated parent actions");
                result = child.Result;
                replayed = true;
                return JsonNode.Parse("{\"$\":\"drone\",\"id\":\"typed-replay-child\"}");
            }
            if ((string?)request["op"] == "wait_for")
                return result?.DeepClone();
            return null;
        }, entry: "replay.scala");
        Check(replayed && scalaReplay.Game.Requests.Any(r => (string?)r["op"] == "quick_print"), "Actual Scala mutable-class/random/time replay assertions did not complete");
        Console.WriteLine("Actual Scala.js mutable class + Random/currentTimeMillis/nanoTime replay passed.");
    }

    Process StartWorker(string artifact, MockGame game)
    {
        string runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        string dotnet = Path.Combine(Directory.GetParent(runtime.TrimEnd(Path.DirectorySeparatorChar))!.Parent!.Parent!.FullName, "dotnet.exe");
        string testAssembly = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var launch = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, "runtimeconfig.json"), "--depsfile", Path.ChangeExtension(testAssembly, "deps.json"), typeof(ScalaEngine).Assembly.Location, "--artifact", artifact, "--entry", "main.scala" })
            launch.ArgumentList.Add(arg);
        launch.Environment["TFWR_PORT"] = game.Port.ToString();
        launch.Environment["TFWR_TOKEN"] = "private-token";
        launch.Environment["TFWR_RUN_ID"] = "test-run";
        launch.Environment["TFWR_DRONE_ID"] = "0";
        return Process.Start(launch) ?? throw new Exception("Could not start worker process");
    }
    var cliGame = new MockGame(request => (string?)request["op"] == "get_world_size" ? JsonValue.Create(7) : JsonValue.Create(true));
    var cliAccept = cliGame.Accept();
    using (var process = StartWorker(basic, cliGame))
    {
        await cliAccept.WaitAsync(TimeSpan.FromSeconds(10));
        JsonObject done = await cliGame.Event.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(process.ExitCode == 0 && (string?)done["event"] == "completed" && (int?)done["result"]?[0] == 7, "Packaged worker CLI failed authenticated execution: " + await process.StandardError.ReadToEndAsync());
        cliGame.Close();
    }
    var cliLoopGame = new MockGame();
    var cliLoopAccept = cliLoopGame.Accept();
    using (var process = StartWorker(loop, cliLoopGame))
    {
        await cliLoopAccept.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        cliLoopGame.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(process.ExitCode == 0, "Worker process did not cleanly cancel an infinite JS loop after host disconnect");
    }

    if (args.Contains("--late-spawn-benchmark") || args.Contains("--late-spawn-varied"))
    {
        var measurements = new JsonArray();
        foreach (int count in new[] { 10000, 100000 })
        {
            bool varied = args.Contains("--late-spawn-varied");
            string operation = varied ? "(i%2===0?'harvest':'move')" : "'move'";
            string arguments = varied ? "(i%2===0?'[]':'[{\"$\":\"enum\",\"family\":\"Direction\",\"name\":\"North\"}]')" : "'[{\"$\":\"enum\",\"family\":\"Direction\",\"name\":\"North\"}]'";
            string artifact = Artifact("late-spawn-" + count, "function runFarm(){for(let i=0;i<" + count + ";i++)__farmerAction(" + operation + "," + arguments + ");__farmerSpawn(function(){__farmerAction('harvest','[]');return '17';});return 'null';}");
            JsonObject? task = null;
            var timer = Stopwatch.StartNew();
            var parentRun = await Run(artifact, action: request =>
            {
                if ((string?)request["op"] == "spawn_drone")
                {
                    task = (JsonObject)request["args"]![0]!.DeepClone();
                    return JsonNode.Parse("{\"$\":\"drone\",\"id\":\"late-child\"}");
                }
                return JsonValue.Create(true);
            }, timeoutSeconds: 180);
            double parentMs = timer.Elapsed.TotalMilliseconds;
            int taskBytes = Encoding.UTF8.GetByteCount(task!.ToJsonString());
            JsonArray journal = ReplayJournal.DecodeTask(task)!;
            int rawBytes = Encoding.UTF8.GetByteCount(journal.ToJsonString());
            timer.Restart();
            var childRun = await Run(artifact, task, timeoutSeconds: 180);
            double childMs = timer.Elapsed.TotalMilliseconds;
            Check(parentRun.Game.Requests.Count == count + 1 && childRun.Game.Requests.Count == 1 && (int?)childRun.Result?["value"] == 17, "Late spawn replay duplicated native actions or reconstructed wrong closure");
            var result = new JsonObject
            {
                ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                ["runtime"] = Environment.Version.ToString(),
                ["pattern"] = varied ? "harvest-move" : "move",
                ["nativeActionCount"] = count,
                ["parentTcpMs"] = parentMs,
                ["childReplayMs"] = childMs,
                ["taskBytes"] = taskBytes,
                ["journalBytes"] = rawBytes,
                ["journalGroups"] = journal[0]!.AsArray().Count
            };
            measurements.Add(result);
            Console.WriteLine("LATE_SPAWN " + result.ToJsonString());
        }
        if (Array.IndexOf(args, "--benchmark-output") is var outputIndex && outputIndex >= 0)
            File.WriteAllText(args[outputIndex + 1], measurements.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    if (args.Contains("--journal-memory-benchmark"))
    {
        var moveEvent = JsonNode.Parse("{\"kind\":\"action\",\"op\":\"move\",\"args\":[{\"$\":\"enum\",\"family\":\"Direction\",\"name\":\"North\"}],\"ok\":true,\"value\":true,\"error\":null}")!.AsObject();
        var harvestEvent = JsonNode.Parse("{\"kind\":\"action\",\"op\":\"harvest\",\"args\":[],\"ok\":true,\"value\":true,\"error\":null}")!.AsObject();
        long before = GC.GetTotalMemory(true);
        var journal = new ReplayJournal(null);
        for (int i = 0; i < 100000; i++)
            journal.Record(i % 2 == 0 ? harvestEvent : moveEvent);
        long retained = GC.GetTotalMemory(true) - before;
        var timer = Stopwatch.StartNew();
        var task = journal.Capture("digest", "main.scala");
        double captureMs = timer.Elapsed.TotalMilliseconds;
        Console.WriteLine("JOURNAL_MEMORY " + new JsonObject { ["events"] = 100000, ["estimatedRetainedManagedBytes"] = retained, ["captureMs"] = captureMs, ["taskBytes"] = Encoding.UTF8.GetByteCount(task.ToJsonString()) }.ToJsonString());
        GC.KeepAlive(journal);
    }

    Console.WriteLine($"Worker checks passed: {checks}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    // This unique test-owned path was created above and is never based on user input.
    if (Path.GetDirectoryName(temporary) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
        Directory.Delete(temporary, recursive: true);
}

sealed class MockGame
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Func<JsonObject, JsonNode?> action;
    private readonly object sendLock = new();
    private TcpClient? client;
    private StreamWriter? writer;
    public readonly List<JsonObject> Requests = new();
    public readonly TaskCompletionSource<JsonObject> Event = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public MockGame(Func<JsonObject, JsonNode?>? action = null)
    {
        this.action = action ?? (_ => null);
        listener.Start();
    }
    public async Task Accept()
    {
        client = await listener.AcceptTcpClientAsync();
        client.NoDelay = true;
        writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);
        var hello = JsonNode.Parse((await reader.ReadLineAsync())!)!;
        if ((string?)hello["token"] != "private-token")
            throw new Exception("Handshake token wrong");
        Send(new JsonObject { ["kind"] = "hello", ["v"] = 1, ["run"] = "test-run", ["drone"] = "0", ["ok"] = true, ["api"] = new JsonArray(), ["enums"] = new JsonObject() });
        _ = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    var request = JsonNode.Parse(line)!.AsObject();
                    if ((string?)request["kind"] == "event")
                    {
                        Event.TrySetResult(request);
                        continue;
                    }
                    Requests.Add(request);
                    JsonNode? result = action(request);
                    Send(new JsonObject { ["kind"] = "pending", ["v"] = 1, ["run"] = "test-run", ["drone"] = "0", ["id"] = request["id"]!.DeepClone() });
                    if (result is JsonObject failed && (string?)failed["$mock-error"] is { } error)
                        Send(new JsonObject { ["kind"] = "response", ["v"] = 1, ["run"] = "test-run", ["drone"] = "0", ["id"] = request["id"]!.DeepClone(), ["ok"] = false, ["error"] = new JsonObject { ["code"] = "GameError", ["message"] = error } });
                    else
                        Send(new JsonObject { ["kind"] = "response", ["v"] = 1, ["run"] = "test-run", ["drone"] = "0", ["id"] = request["id"]!.DeepClone(), ["ok"] = true, ["value"] = result });
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        });
    }
    private void Send(JsonObject value)
    {
        lock (sendLock)
            writer!.WriteLine(value.ToJsonString());
    }
    public void SendControl(string action) => Send(new JsonObject { ["kind"] = "control", ["v"] = 1, ["run"] = "test-run", ["drone"] = "0", ["action"] = action });
    public void Close()
    {
        client?.Dispose();
        listener.Stop();
    }
}
