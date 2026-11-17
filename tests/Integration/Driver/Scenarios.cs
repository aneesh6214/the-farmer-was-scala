using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Core.Platform;
using Platforms;
using FarmerScala.Editor;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class IntegrationPlugin
{
    private sealed class Scenario
    {
        internal string Name = "";
        internal Func<IEnumerator> Run = null!;
    }
    private IEnumerable<Scenario> Scenarios()
    {
        if (Environment.GetEnvironmentVariable("FARMER_SCALA_TEST_PHASE") == "restart")
        {
            yield return new Scenario { Name = "cold-restart-persistence", Run = Restart };
            yield break;
        }
        if ((Environment.GetEnvironmentVariable("FARMER_SCALA_TEST_SUITE") ?? "smoke") == "full")
            yield return new Scenario { Name = "cold-start-editor-with-errors", Run = ColdStartEditor };
        yield return new Scenario { Name = "startup-and-real-movement", Run = Smoke };
        if ((Environment.GetEnvironmentVariable("FARMER_SCALA_TEST_SUITE") ?? "smoke") == "smoke") yield break;
        yield return new Scenario { Name = "native-api-contract", Run = ApiContract };
        yield return new Scenario { Name = "scala-language-and-entry-points", Run = Language };
        yield return new Scenario { Name = "farming-api-and-wraparound", Run = Farming };
        yield return new Scenario { Name = "typed-child-drone-replay", Run = Drones };
        yield return new Scenario { Name = "native-simulation", Run = Simulations };
        yield return new Scenario { Name = "mining-native-api", Run = Mining };
        yield return new Scenario { Name = "research-progression-and-info-refresh", Run = Research };
        yield return new Scenario { Name = "steam-and-ranked-isolation", Run = Ranked };
        yield return new Scenario { Name = "step-resume-stop", Run = Stepping };
        yield return new Scenario { Name = "edits-and-immutable-execution", Run = Edits };
        yield return new Scenario { Name = "compile-and-runtime-errors", Run = Diagnostics };
        yield return new Scenario { Name = "completion-docs-navigation-colors", Run = Editor };
        yield return new Scenario { Name = "native-editor-formatting", Run = Formatting };
        yield return new Scenario { Name = "actual-window-and-toolbar-state", Run = Windows };
        yield return new Scenario { Name = "output-startup-status", Run = OutputStartup };
        yield return new Scenario { Name = "completion-relevance-in-native-popup", Run = CompletionRelevance };
        yield return new Scenario { Name = "background-backup-and-editor-writes", Run = ConcurrentBackup };
        yield return new Scenario { Name = "save-reload-and-default-language", Run = Persistence };
    }
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type Mod(string name) => typeof(FarmerScala.Plugin).Assembly.GetType("FarmerScala." + name, true)!;
    private static object? Field(object value, string name) => value.GetType().GetField(name, All)!.GetValue(value);
    private static object? Call(Type type, string name, object? instance, params object?[] args)
        => type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(instance, args);
    private object Host => typeof(FarmerScala.Plugin).GetProperty("Host", All)!.GetValue(FarmerScala.Plugin.Instance)!;
    private bool Running => (bool)Host.GetType().GetProperty("IsRunning")!.GetValue(Host);
    private bool Compiling => (bool)Host.GetType().GetProperty("IsCompiling", All)!.GetValue(Host);
    private Simulation Sim => (Simulation)typeof(MainSim).GetField("sim", All)!.GetValue(MainSim.Inst)!;
    private CodeWindow Main => MainSim.Inst.workspace.codeWindows["main"];
    private string Output => File.Exists(Path.Combine(root, "data/output.txt")) ? File.ReadAllText(Path.Combine(root, "data/output.txt")) : "";
    private void StopHost() { if (FarmerScala.Plugin.Instance != null) Call(Host.GetType(), "Stop", Host, "Integration scenario cleanup"); }
    private IEnumerator Frames(int count = 5) { for (int i = 0; i < count; i++) { Timeout("Unity frames"); yield return null; } }
    private IEnumerator Wait(Func<bool> predicate, string phase)
    {
        while (!predicate()) { Timeout(phase); yield return null; }
    }
    private IEnumerator Play(string expected, CodeWindow? window = null)
    {
        (window ?? Main).PressExecuteOrStop();
        yield return Wait(() => !Running, "compiler/worker/native execution for " + expected);
        yield return Frames();
        Check(Output.Contains(expected), expected + " reached real program output; output=" + Output.Substring(0, Math.Min(Output.Length, 600)));
    }
    private readonly string[] initialUnlocks = { "grass_1", "soil", "harvest", "pass", "grassland", "hay", "straw_hat", "true", "false", "loops", "speed_5", "expand_5", "north", "south", "east", "west", "entities", "plant", "operators", "items", "carrots_1", "grounds", "none", "senses", "variables", "functions", "import", "watering_1" };
    private readonly string[] miningUnlocks = { "mining_3", "underground_senses", "coal_1", "iron_1", "prospecting", "jump", "quartz_1", "bamboo_1", "rice_1", "dynamite_1", "special_soils", "mushrooms_1", "ore_boost_1", "treasure_map", "pyramid" };
    private string currentSave = "Integration";
    private Dictionary<string, string> Fixture(string name)
    {
        string path = Path.Combine(Environment.GetEnvironmentVariable("FARMER_SCALA_TEST_FIXTURES")!, name);
        return Directory.GetFiles(path, "*.scala").ToDictionary(Path.GetFileNameWithoutExtension, File.ReadAllText);
    }
    private IEnumerator Load(string name, Dictionary<string, string> sources, bool scala = true, params string[] extraUnlocks)
    {
        StopHost();
        currentSave = name;
        string save = Path.Combine(root, "data", "Saves", name);
        if (!Directory.Exists(save))
        {
            Directory.CreateDirectory(save);
            string id = Guid.NewGuid().ToString("N");
            void Json(string file, object value) => File.WriteAllText(file, JsonConvert.SerializeObject(value, Newtonsoft.Json.Formatting.Indented));
            if (scala)
            {
                string project = Path.Combine(save, ".farmer-scala"); Directory.CreateDirectory(Path.Combine(project, "src"));
                Json(Path.Combine(project, "language.json"), new { FormatVersion = 1, SaveId = id, Language = "native-scala" });
                Json(Path.Combine(project, "project.json"), new { FormatVersion = 1, SaveId = id, Language = "native-scala", Enabled = true, SaveName = name, Modules = sources.Keys.ToArray() });
            }
            foreach (var source in sources) File.WriteAllText(Path.Combine(save, scala ? ".farmer-scala/src/" + source.Key + ".scala" : source.Key + ".py"), source.Value);
            if (scala) File.WriteAllText(Path.Combine(save, "main.py"), "pass\n");
            var stock = new[] { "hay", "wood", "carrot", "water", "block", "coal", "iron", "quartz", "bamboo", "rice", "mushroom", "dynamite", "pumpkin", "gold", "fertilizer" };
            Json(Path.Combine(save, "save.json"), new {
                version = 3, items = new { serializeList = stock.Select(n => new { name = n, nr = 1000000.0 }).ToArray() }, unlocks = initialUnlocks.Concat(extraUnlocks).ToArray(),
                dockedFiles = new object[0], minimizedFiles = new object[0], openDocPages = new object[0],
                openFilePositions = new[] { new { key = "main", value = new { x = 100, y = 60 } } },
                openFileSizes = new[] { new { key = "main", value = new { x = 1000, y = 700 } } }, openFileScrollPositions = new[] { new { key = "main", value = 0 } }
            });
        }
        yield return Reload(name);
        Check(EditorIntegration.Enabled == scala, name + " resolved the correct save language");
        if (!scala) Check(Main.CodeInput.text == sources["main"], "native loader restored the Default source buffer");
    }
    private IEnumerator Reload(string name)
    {
        // Follow the native save chooser's scene-reload path. Saver.Load alone
        // assumes a fresh workspace and intentionally does not replace open windows.
        var previous = MainSim.Inst;
        var pending = Saver.WaitForPendingSaves();
        yield return Wait(() => pending.IsCompleted, "pending native save writes");
        pending.GetAwaiter().GetResult();
        Saver.StopFileWatcher();
        previous.menu.LoadSave(name);
        UnityEngine.SceneManagement.SceneManager.LoadScene(0);
        yield return Wait(() => MainSim.Inst != null && !ReferenceEquals(previous, MainSim.Inst) && MainSim.Inst.workspace != null && MainSim.Inst.workspace.codeWindows.ContainsKey("main"), "native save scene reload");
        MainSim.Inst.menu.Play();
        yield return Frames(8);
    }
    private void Edit(CodeWindow window, string text)
    {
        window.CodeInput.text = text;
        window.CodeInput.stringPosition = text.Length;
        window.UpdateTokens();
    }
    private IEnumerator ApiContract()
    {
        var sdk = (string)Mod("Runtime.SdkProfile").GetProperty("ActiveDirectory", All)!.GetValue(null)!;
        var methods = new HashSet<string>(Directory.GetFiles(sdk, "*.scala", SearchOption.AllDirectories).SelectMany(f =>
            System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"(?m)^\s*def\s+(\w+)").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value)));
        var pythonOnly = new HashSet<string> { "range", "len", "min", "max", "abs", "str", "list", "set", "dict", "append", "add", "insert", "pop", "remove", "leaderboard_run" };
        var missing = BuiltinFunctions.Functions.Keys.Where(n => !pythonOnly.Contains(n) && !methods.Contains(n)).ToArray();
        Check(missing.Length == 0, "every native game function has a typed SDK wrapper; missing=" + string.Join(",", missing));
        Check(Application.isBatchMode && SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null, "actual Unity player is running without a window or graphics device");
        yield return null;
    }
    private IEnumerator Language()
    {
        yield return Load("Language", Fixture("language"));
        yield return Play("LANGUAGE_OK");
        Check(!Output.Contains("imported entry point ran"), "imports do not invoke another file's @main");
        Edit(Main, "quick_print(\"SCRIPT_OK\")\n");
        yield return Play("SCRIPT_OK");
        Edit(Main, "def main(): Unit = quick_print(\"MUST_NOT_RUN\")\nquick_print(\"DEFINITIONS_OK\")\n");
        yield return Play("DEFINITIONS_OK");
        Check(!Output.Contains("MUST_NOT_RUN"), "an unannotated method named main is not implicitly invoked");
    }
    private IEnumerator Farming()
    {
        yield return Load("Farming", Fixture("farming"), true, "mazes_1", "hats");
        yield return Play("FARMING_OK");
        Check(Sim.farm.drones[0].pos.x == 7, "native wraparound returns to the expected tile");
        Check(MainSim.Inst.GetNumItem(ResourceManager.GetAllItems().Single(i => i.itemName == "wood").itemId) < 1000000, "native planting deducted real wood inventory");
    }
    private IEnumerator Drones()
    {
        yield return Load("Drones", Fixture("drones"), true, "megafarm_2");
        yield return Play("DRONES_OK");
        Check(Sim.farm.drones.Count(d => d != null) == 1, "native child drone is removed after completion");
    }
    private IEnumerator Simulations()
    {
        yield return Load("Simulations", Fixture("simulation"));
        yield return Play("SIMULATION_OK");
        Check(!MainSim.Inst.MightBeSimulating(), "native simulation restored the main farm");
    }
    private IEnumerator Mining()
    {
        bool owned = PlatformManager.IsDlcInstalled(EDlcType.Mining);
        Check((bool)Mod("Runtime.SdkProfile").GetProperty("MiningEnabled", All)!.GetValue(null) == owned, "SDK profile matches real Steam DLC installation state");
        if (!owned) { skipReason = "Mining DLC not installed"; File.AppendAllText(Path.Combine(root, "progress.log"), "SKIP mining gameplay: DLC not installed\n"); yield break; }
        yield return Load("Mining", Fixture("mining"), true, miningUnlocks);
        yield return Play("MINING_OK");
        Check(Sim.farm.drones[0].pos.x >= 0, "native mining left a valid drone on the actual farm");
    }
    private Dictionary<string, UnlockBox> Tree()
    {
        var research = MainSim.Inst.researchMenu;
        research.Setup();
        var boxes = (Dictionary<string, UnlockBox>)Field(research, "allBoxes")!;
        foreach (var node in boxes.Values.Where(b => string.IsNullOrEmpty(b.unlockSO.parentUnlock)))
            node.SetupRec(true, research.openedUnlockDocs, MainSim.Inst.GetInventory(), MainSim.Inst.GetUnlocks(), out _);
        return boxes;
    }
    private IEnumerator Research()
    {
        yield return Load("Research", new Dictionary<string, string> { ["main"] = "quick_print(\"RESEARCH\")" });
        Check(OptionHolder.GetString("allow locked features") == "disabled", "free language upgrades do not depend on Allow Locked Features");
        string[] code = { "loops", "functions", "import", "lists", "dictionaries", "debug", "debug_2", "simulation", "costs" };
        Check(code.All(id => MainSim.Inst.GetUnlocks().GetValueOrDefault(id) >= 1), "actual game research counts contain pre-unlocked code features");
        Check(MainSim.Inst.GetUnlocks().GetValueOrDefault("mining") == 0 && MainSim.Inst.GetUnlocks().GetValueOrDefault("trees") == 0, "physical base/mining research is not pre-unlocked");
        foreach (string id in new[] { "trees", "mining" })
        {
            var upgrade = ResourceManager.GetUnlock(id);
            if (upgrade == null || !PlatformManager.IsDlcInstalled(upgrade.dlc)) continue;
            var cost = MainSim.Inst.GetUnlockCost(upgrade).items.ToArray();
            var before = MainSim.Inst.GetInventory().items.ToArray();
            Check(MainSim.Inst.UnlockOrUpgrade(upgrade), id + " can be purchased through the real native API");
            var after = MainSim.Inst.GetInventory().items;
            Check(before.Select((n, i) => Math.Abs(after[i] - (n - cost[i])) < .001).All(x => x), id + " native purchase deducts exactly its displayed item costs");
            Check(MainSim.Inst.GetUnlocks().GetValueOrDefault(id) == 1, id + " native research level advances once");
        }
        for (int refresh = 0; refresh < 3; refresh++)
        {
            var boxes = Tree();
            Check(MainSim.Inst.researchMenu.GetComponentsInChildren<CodeExample>(true).All(e => e.GetComponentInChildren<CodeWindow>(true)?.GetComponent<Window>() == null), "documentation snippets remain embedded examples, refresh " + refresh);
            foreach (string id in new[] { "leaderboard", "underground_leaderboard" })
            {
                if (!boxes.TryGetValue(id, out var box)) continue;
                var button = (ColoredButton)Field(box, "button")!;
                Check(!button.Interactable && button.Text == "Unavailable", id + " disabled after actual tree/Info rebuild " + refresh);
                double[] inventory = MainSim.Inst.GetInventory().items.ToArray();
                box.ButtonClicked();
                Check(inventory.SequenceEqual(MainSim.Inst.GetInventory().items) && !MainSim.Inst.IsUnlocked(id), id + " click does not buy research or spend inventory");
            }
        }
        var task = Saver.Save(MainSim.Inst);
        yield return Wait(() => task.IsCompleted, "native save after research purchases");
        Check(task.GetAwaiter().GetResult(), "native asynchronous progress save completed");
        yield return Reload(currentSave);
        Check(MainSim.Inst.GetUnlocks().GetValueOrDefault("trees") == 1 && MainSim.Inst.GetUnlocks().GetValueOrDefault("functions") == 1, "purchased research and code grants survive real save/reload");
    }
    private IEnumerator Ranked()
    {
        yield return Load("Ranked", new Dictionary<string, string> { ["main"] = "quick_print(\"GUARDS\")" });
        string path = Path.Combine(root, "external-write-attempts.log");
        string before = File.Exists(path) ? File.ReadAllText(path) : "";
        Type stats = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Steamworks.SteamUserStats")).First(t => t != null)!;
        var achievement = stats.GetMethod("SetAchievement", new[] { typeof(string) })!;
        var stat = stats.GetMethod("SetStat", new[] { typeof(string), typeof(int) })!;
        Check(!(bool)achievement.Invoke(null, new object[] { "INTEGRATION_SENTINEL_NEVER_SEND" }), "production guard rejects achievement writes inside real Unity");
        Check(!(bool)stat.Invoke(null, new object[] { "INTEGRATION_SENTINEL_NEVER_SEND", 1 }), "production guard rejects mining/base stat writes inside real Unity");
        Check((File.Exists(path) ? File.ReadAllText(path) : "") == before, "production guards prevented calls from reaching the independent Steam-write tripwire");
        foreach (EDlcType dlc in new[] { EDlcType.None, EDlcType.Mining })
        {
            var args = new MainSim.LeaderboardStartArgs("main", Array.Empty<string>(), ItemBlock.CreateEmpty(), new List<KeyValuePair<string, IPyObject>>(), "integration", "integration", LeaderboardType.farm_resources, true, dlc);
            MainSim.Inst.ScheduleLeaderboardStart(args);
            Check(Field(MainSim.Inst, "startLeaderboardNow") == null, dlc + " ranked start was rejected by the actual Harmony hook");
        }
    }
    private IEnumerator Stepping()
    {
        yield return Load("Stepping", Fixture("stepping"));
        Main.PressStepByStepButton();
        yield return Wait(() => !Compiling, "step-mode compilation");
        yield return Wait(() => {
            var active = Field(Host, "active");
            var workers = active == null ? null : Field(active, "Workers") as IDictionary;
            return workers != null && workers.Count > 0 && workers.Values.Cast<object>().All(w => Field(w, "Connection") != null);
        }, "step worker authentication");
        yield return Wait(() => Mod("Editor.EditorIntegration").GetField("pausedMarker", All)!.GetValue(null) != null, "initial mapped source pause");
        int start = Sim.farm.drones[0].pos.x;
        // A step advances a mapped Scala source point, which can precede an API call.
        // Drive actual controls until that statement has executed; do not equate JS points with actions.
        int steps = 0;
        while (Sim.farm.drones[0].pos.x == start && Running && steps++ < 64)
        {
            object? marker = Mod("Editor.EditorIntegration").GetField("pausedMarker", All)!.GetValue(null);
            Main.PressStepByStepButton();
            yield return Wait(() => !Running || !ReferenceEquals(marker, Mod("Editor.EditorIntegration").GetField("pausedMarker", All)!.GetValue(null)), "next mapped source pause");
        }
        Check(Sim.farm.drones[0].pos.x != start, "source stepping executes a native move");
        yield return Frames(20);
        Check(Running && (bool)Field(Field(Host, "active")!, "StepMode")!, "step control keeps the real run paused between actions");
        Check(Sim.farm.drones[0].pos.x == 1, "one native step performed exactly one move");
        Main.PressExecuteOrStop(); yield return Wait(() => !Running, "resumed step run"); yield return Frames();
        Check(Output.Contains("STEPPING_OK") && Sim.farm.drones[0].pos.x == 2, "Play resumes and finishes the native stepped run");
    }
    private IEnumerator Edits()
    {
        yield return Load("Edits", Fixture("loop"));
        Main.PressExecuteOrStop(); yield return Wait(() => !Compiling, "loop compilation"); yield return Frames(20);
        Check(Running, "loop is executing before an unrelated edit");
        Edit(MainSim.Inst.workspace.codeWindows["Scratch"], "val unrelated: Int = \"invalid\"\n");
        yield return Frames(30);
        Check(Running && MainSim.Inst.IsExecuting(), "invalid edits to an unrelated file leave the actual native program running");
        Edit(MainSim.Inst.workspace.codeWindows["Used"], "def tick(): Unit = { move(East); do_a_flip(); () }\n");
        yield return Wait(() => !Running, "stop after an involved source edit");
        Check(!MainSim.Inst.IsExecuting(), "editing an actual dependency stops the native execution");
        Main.PressExecuteOrStop(); yield return Wait(() => !Running, "compile failure from unrelated invalid source");
        Check(!MainSim.Inst.IsExecuting() && Output.Length > 0, "a new run is blocked by project compile errors");
        Edit(MainSim.Inst.workspace.codeWindows["Scratch"], "val unrelated: Int = 1\n");
        Main.PressExecuteOrStop(); yield return Wait(() => !Compiling, "recovered compilation"); yield return Frames(20);
        Check(Running, "compilation recovers after fixing the unrelated file");
        Main.PressExecuteOrStop(); yield return Wait(() => !Running, "explicit stop");
        Check(!MainSim.Inst.IsExecuting(), "Stop retires the actual execution and workers");
    }
    private IEnumerator Diagnostics()
    {
        yield return Load("Errors", new Dictionary<string, string> { ["main"] = "val wrong: Int = \"text\"\n" });
        Main.PressExecuteOrStop(); yield return Wait(() => !Running, "type-error compilation"); yield return Frames();
        var popup = (ErrorMessage)Field(Main, "errorMessage")!;
        Check(popup.gameObject.activeSelf, "compiler errors open the real native error widget");
        Check(!MainSim.Inst.IsExecuting(), "invalid Scala never starts the native farm");
        Edit(Main, "@main def fail(): Unit = throw new IllegalStateException(\"integration-runtime-error\")\n");
        Main.PressExecuteOrStop(); yield return Wait(() => !Running, "Scala runtime exception"); yield return Frames();
        Check(Output.Contains("integration-runtime-error"), "worker runtime errors are delivered to the actual game output");
        Edit(Main, "quick_print(\"RECOVERED_OK\")\n"); yield return Play("RECOVERED_OK");
    }
    private IEnumerator Editor()
    {
        yield return Load("Editor", Fixture("editor")); yield return Play("EDITOR_WARM_OK");
        Type analysis = Mod("Editor.ScalaAnalysis");
        Edit(Main, Main.CodeInput.text + "\nimport Pla");
        List<string>? complete = null;
        Call(analysis, "Complete", null, Main, Main.CodeInput.text.Length, new Action<List<string>>(v => complete = v));
        yield return Wait(() => complete != null && complete.Contains("PlantingUtils"), "compiler-backed project import completion");
        Check(complete!.Contains("PlantingUtils") && !complete.Contains("private"), "actual editor import completion keeps modules and excludes unrelated keywords");
        Edit(Main, Fixture("editor")["main"]);
        object? result = null;
        int at = Main.CodeInput.text.IndexOf("yieldItems", StringComparison.Ordinal) + 4;
        yield return Wait(() => {
            object?[] args = { Main, at, null };
            if (!(bool)Call(analysis, "TryResolve", null, args)!) return false;
            result = args[2]; return ((string?)Field(result!, "HoverText"))?.Contains("Inventory items produced") == true;
        }, "inherited Scaladoc through the actual editor provider");
        Check(((string)Field(result!, "HoverText")!).Contains("Inventory items produced"), "actual instance hover inherits base field documentation");
        int library = Main.CodeInput.text.IndexOf("Int]", StringComparison.Ordinal) + 2;
        yield return Wait(() => {
            object?[] args = { Main, library, null };
            if (!(bool)Call(analysis, "TryResolve", null, args)!) return false;
            result = args[2]; var def = Field(result!, "Definition"); return def != null && (bool)Field(def, "IsLibrary")!;
        }, "Scala library definition resolution");
        var target = Field(result!, "Definition")!;
        var view = (CodeWindow)Call(Mod("Editor.ReadOnlyLibraryWindows"), "Open", null, MainSim.Inst.workspace, target)!;
        Check(view.CodeInput.readOnly && !MainSim.Inst.workspace.codeWindows.Values.Contains(view), "real Scala library window is read-only and excluded from player modules");
        Check(view.CodeInput.text.Contains("class Int"), "library source viewer contains actual Scala source");
        yield return Wait(() => Call(analysis, "Spans", null, Main) is IEnumerable spans && spans.Cast<object>().Any(), "semantic highlighting in the actual window");
        Check(((IEnumerable)Call(analysis, "Spans", null, Main)!).Cast<object>().Any(s => (string?)Field(s, "Kind") == "class"), "compiler semantic type colors reach the game editor");
    }
    private IEnumerator Formatting()
    {
        yield return Load("Formatting", Fixture("editor")); yield return Play("EDITOR_WARM_OK");
        string source = "import Models.Carrot\nimport Models.{Carrot, Method}\n/** A description. */\ndef f(x:Int):Int={\n x+1\n}\n";
        Edit(Main, source);
        Call(Mod("Editor.ScalaEditorTools"), "RequestFormat", null, Main);
        yield return Wait(() => Main.CodeInput.text != source, "native editor format request");
        Check(Main.CodeInput.text.Contains("Models.{Carrot, Method}") && Main.CodeInput.text.Contains("x: Int") && Main.CodeInput.text.Contains("\n  x + 1"), "format request merges imports and uses two-space indentation in the real code window");
        Check(Main.CodeInput.text.Contains("/**\n") && Main.CodeInput.text.Contains(" * A description."), "formatter preserves expanded Scaladoc in the actual editor");
        string formatted = Main.CodeInput.text;
        Call(Mod("Editor.ScalaEditorTools"), "RequestFormat", null, Main); yield return Frames(30);
        Check(Main.CodeInput.text == formatted, "formatting the same actual file twice is stable");
    }
    private IEnumerator Windows()
    {
        yield return Load("Windows", Fixture("editor")); yield return Play("EDITOR_WARM_OK");
        MainSim.Inst.menu.Open(); yield return Frames(10);
        var buttons = UnityEngine.Object.FindObjectsByType<ColoredButton>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var scala = buttons.Single(b => b.tooltipName == "Scala Changelog");
        var news = MainSim.Inst.menu.titleWorkspace.openNewsButton.GetComponent<RectTransform>();
        var scalaRect = (RectTransform)scala.transform;
        var cornersA = new Vector3[4]; var cornersB = new Vector3[4]; scalaRect.GetWorldCorners(cornersA); news.GetWorldCorners(cornersB);
        Check(cornersA[2].x < cornersB[0].x, "actual Scala news button has a gap left of native News");
        foreach (var dlc in UnityEngine.Object.FindObjectsByType<DLCStatusButton>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            ((RectTransform)dlc.transform).GetWorldCorners(cornersB);
            bool overlap = cornersA[0].x < cornersB[2].x && cornersA[2].x > cornersB[0].x && cornersA[0].y < cornersB[2].y && cornersA[2].y > cornersB[0].y;
            Check(!overlap, "actual Scala news button does not overlap " + dlc.dlc);
            var dlcLeft = cornersB[0].x; var dlcRight = cornersB[2].x;
            news.GetWorldCorners(cornersB);
            if (dlcRight <= cornersB[0].x && cornersA[2].x <= dlcLeft)
                Check(Math.Abs((dlcLeft - cornersA[2].x) - (cornersB[0].x - dlcRight)) < .5f,
                    "Scala/mining/news buttons have matching measured horizontal gaps");
        }
        MainSim.Inst.menu.Play(); yield return Frames();
        var output = MainSim.Inst.workspace.GetComponentsInChildren<ColoredButton>(true).FirstOrDefault(b => b.tooltipName == "Scala Output");
        Check(output != null || UnityEngine.Object.FindObjectsByType<ColoredButton>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(b => b.tooltipName.Contains("Output")), "output toolbar button exists in the actual game UI");
    }
    private IEnumerator Restart()
    {
        yield return Wait(() => MainSim.Inst != null && MainSim.Inst.workspace != null && MainSim.Inst.workspace.codeWindows.ContainsKey("main"), "cold scene initialization");
        Check(OptionHolder.GetString("activeSave") == "Persistence", "a new game process restores the most recently used save");
        Check(EditorIntegration.Enabled && Main.CodeInput.text.Contains("PERSISTED_OK"), "cold startup restores Scala source and language");
        Check(MainSim.Inst.workspace.codeWindows["Helper"].CodeInput.text.Contains("42"), "cold startup restores the other saved module");
        Check(MainSim.Inst.IsUnlocked("functions"), "language upgrades survive a cold process restart");
        Check(Helper.persistentDataPath == Path.Combine(root, "data"), "the second process remains isolated from real saves");
        MainSim.Inst.menu.Play(); yield return Frames();
        yield return Play("PERSISTED_OK");
    }
    private IEnumerator Persistence()
    {
        yield return Load("Persistence", new Dictionary<string, string> { ["main"] = "quick_print(\"PERSISTED_OK\")\n", ["Helper"] = "val marker: Int = 42\n" });
        var save = Saver.Save(MainSim.Inst); yield return Wait(() => save.IsCompleted, "asynchronous save"); Check(save.GetAwaiter().GetResult(), "real save pipeline reports success");
        string directory = Path.Combine(root, "data/Saves/Persistence");
        Check(File.ReadAllText(Path.Combine(directory, "main.py")).Trim() == "pass", "Scala saving never overwrites vanilla Python source");
        Check(File.ReadAllText(Path.Combine(directory, ".farmer-scala/src/main.scala")).Contains("PERSISTED_OK"), "Scala source was written into its actual sidecar");
        yield return Reload(currentSave); yield return Play("PERSISTED_OK");
        Check(MainSim.Inst.workspace.codeWindows.ContainsKey("Helper"), "real reload restores all project modules");
        yield return Load("Default", new Dictionary<string, string> { ["main"] = "quick_print(\"DEFAULT_OK\")\n" }, false, "debug");
        Main.PressExecuteOrStop(); yield return Wait(() => Output.Contains("DEFAULT_OK"), "native default-language output");
        yield return Wait(() => !MainSim.Inst.IsExecuting(), "native default-language execution"); yield return Frames();
        Check(Output.Contains("DEFAULT_OK"), "Default save still runs the original game interpreter");
        yield return Reload("Persistence");
        Check(EditorIntegration.Enabled && Main.CodeInput.text.Contains("PERSISTED_OK"), "returning from Default restores Scala language and source identity");
    }
}
