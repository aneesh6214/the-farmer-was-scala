using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using FarmerScala.Editor;
using Newtonsoft.Json;
using UnityEngine;

[BepInPlugin("dev.farmerscala.integration", "Farmer Scala Integration Tests", "0.1.0")]
[BepInDependency("dev.farmerscala")]
public sealed partial class IntegrationPlugin : BaseUnityPlugin
{
    private string root = "";
    private readonly List<string> errors = new List<string>();
    private readonly List<object> checks = new List<object>();
    private readonly List<object> scenarios = new List<object>();
    private bool finished;
    private string? skipReason;
    private DateTime deadline;
    private void Awake()
    {
        root = Environment.GetEnvironmentVariable("FARMER_SCALA_TEST_RUN") ?? "";
        if (root.Length == 0 || !File.Exists(Path.Combine(root, "isolation-ready.txt"))) { Application.Quit(86); return; }
        File.WriteAllText(Path.Combine(root, "driver-ready.txt"), "Unity " + Application.unityVersion + "; batch=" + Application.isBatchMode + "; data=" + Helper.persistentDataPath);
        Application.runInBackground = true;
        Application.logMessageReceived += Observe;
        deadline = DateTime.UtcNow.AddSeconds(180);
    }
    private void Observe(string condition, string stack, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) { string error = condition + "\n" + stack; if (errors.Count < 30 && !errors.Contains(error)) errors.Add(error); }
    }
    private IEnumerator Start()
    {
        yield return null;
        var selected = (Environment.GetEnvironmentVariable("FARMER_SCALA_TEST_CASES") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        var available = Scenarios().ToArray();
        if (selected.Except(available.Select(t => t.Name)).Any()) { Finish(new Exception("Unknown scenario selection: " + string.Join(",", selected))); yield break; }
        foreach (var test in available.Where(t => selected.Length == 0 || t.Name == "startup-and-real-movement" || t.Name == "cold-start-editor-with-errors" || selected.Contains(t.Name)))
        {
            deadline = DateTime.UtcNow.AddSeconds(100);
            string? failure = null;
            skipReason = null;
            int firstCheck = checks.Count, firstError = errors.Count;
            var stack = new Stack<IEnumerator>();
            stack.Push(test.Run());
            while (stack.Count > 0)
            {
                object? current = null;
                try
                {
                    if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                    current = stack.Peek().Current;
                }
                catch (Exception error) { failure = error.ToString();
                    if (MainSim.Inst?.workspace?.codeWindows.TryGetValue("main", out var failedWindow) == true)
                        failure += "\nNative editor error: " + Field(failedWindow, "errorString");
                    while (stack.Count > 0)
                    {
                        try { (stack.Pop() as IDisposable)?.Dispose(); }
                        catch (Exception cleanup) { failure += "\nScenario cleanup: " + cleanup; }
                    } }
                if (current is IEnumerator nested) stack.Push(nested);
                else yield return current;
            }
            if (failure == null && errors.Count > firstError) failure = string.Join("\n", errors.Skip(firstError));
            scenarios.Add(new { name = test.Name, passed = failure == null, checks = checks.Count - firstCheck, error = failure, skipped = skipReason != null, skipReason });
            WriteResults(false);
            if (failure != null)
            {
                File.AppendAllText(Path.Combine(root, "progress.log"), "SCENARIO FAILED " + test.Name + ": " + failure + "\n");
                StopHost();
            }
        }
        Finish(errors.Count > 0 || scenarios.Any(s => !(bool)s.GetType().GetProperty("passed")!.GetValue(s)) ? new Exception("One or more scenarios failed.") : null);
    }
    private IEnumerator Smoke()
    {
        while (MainSim.Inst == null || MainSim.Inst.workspace == null || !MainSim.Inst.workspace.codeWindows.ContainsKey("main"))
        { Timeout("game scene/save initialization"); yield return null; }
        Check(Helper.persistentDataPath == Path.Combine(root, "data"), "native save/settings path is isolated");
        Check(EditorIntegration.Enabled, "production mod resolved the Scala save");
        MainSim.Inst.menu.Play();
        for (int i = 0; i < 5; i++) yield return null;
        var window = MainSim.Inst.workspace.codeWindows["main"];
        var plugin = FarmerScala.Plugin.Instance!;
        var host = typeof(FarmerScala.Plugin).GetProperty("Host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
        var running = host.GetType().GetProperty("IsRunning")!;
        window.PressExecuteOrStop();
        Check((bool)running.GetValue(host), "native Play handler starts the production Scala host");
        while ((bool)running.GetValue(host)) { Timeout("Scala compilation/execution"); yield return null; }
        for (int i = 0; i < 5; i++) yield return null;
        string output = File.ReadAllText(Path.Combine(root, "data", "output.txt"));
        Check(output.Contains("INTEGRATION_SMOKE_OK"), "real Scala compiler/worker executed native movement assertions");
        var sim = (Simulation)typeof(MainSim).GetField("sim", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(MainSim.Inst)!;
        Check(sim.farm.drones[0].pos.x == 1, "actual native drone moved east by one tile");
        Check(errors.Count == 0, "Unity reported no errors during the scenario");
    }
    private void Timeout(string phase) { if (DateTime.UtcNow > deadline) throw new TimeoutException(phase); }
    private void Check(bool condition, string label)
    {
        checks.Add(new { name = label, passed = condition });
        File.AppendAllText(Path.Combine(root, "progress.log"), (condition ? "PASS " : "FAIL ") + label + "\n");
        if (!condition) throw new Exception(label);
    }
    private void Finish(Exception? failure)
    {
        if (finished) return; finished = true;
        finalFailure = failure;
        WriteResults(true);
        Application.Quit(failure == null ? 0 : 1);
    }
    private Exception? finalFailure;
    private void WriteResults(bool complete)
    {
        File.WriteAllText(Path.Combine(root, Environment.GetEnvironmentVariable("FARMER_SCALA_TEST_PHASE") == "restart" ? "results-restart.json" : "results.json"), JsonConvert.SerializeObject(new { passed = complete && finalFailure == null, complete, scenarios, checks, error = finalFailure?.ToString(), errors, unity = Application.unityVersion, batch = Application.isBatchMode }, Newtonsoft.Json.Formatting.Indented));
    }

}
