using System.Diagnostics;
using FarmerScala;
using FarmerScala.Editor;
using FarmerScala.Runtime;

internal static class StagedSmoke
{
    internal static async Task Run(string stage)
    {
        stage = Path.GetFullPath(stage);
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string scratch = Path.Combine(root, ".local", "staged-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string model = "case class Crop(value: Int)\n";
        string source = "import models.modeling.Crop\nclass Farmer { def plan = (1 to 5).map(Crop.apply).collect { case c if c.value % 2 == 1 => c.value }.sum }\nassert(new Farmer().plan == 9)\nassert(get_world_size() == 4)\nquick_print(\"Staged Scala worker passed\")\n";
        File.WriteAllText(Path.Combine(scratch, "main.scala"), source);
        File.WriteAllText(Path.Combine(scratch, "models.modeling.scala"), model);
        BepInEx.Paths.CachePath = Path.Combine(scratch, "cache");
        EditorIntegration.Project = new ProjectSnapshot { Directory = scratch, Sources = new Dictionary<string, string> { ["main"] = source, ["models.modeling"] = model } };
        var plugin = new Plugin();
        using var host = new RuntimeHost(plugin, stage);
        var timer = Stopwatch.StartNew();
        host.Run(new CodeWindow());
        bool started = false;
        while (host.IsRunning)
        {
            if (timer.Elapsed > TimeSpan.FromMinutes(3))
                throw new TimeoutException("Staged pipeline timed out.\n" + string.Join("\n", plugin.Lines));
            host.Tick();
            started |= MainSim.Inst.Starts > 0;
            await Task.Delay(10);
        }
        if (!started || EditorIntegration.Error != null)
            throw new Exception("Staged pipeline failed: " + EditorIntegration.Error + "\n" + string.Join("\n", plugin.Lines));
        Console.WriteLine("PASS relocated offline stage: actual bundled compiler → Scala.js → actual bundled x64 worker → authenticated production host → adapted game call → completion");
        Console.WriteLine("Elapsed " + timer.Elapsed.TotalSeconds.ToString("F2") + "s; Unity/native farm execution is not part of this smoke test.");
    }
}
