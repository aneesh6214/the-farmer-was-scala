using System.Reflection;
using FarmerScala.Editor;
using FarmerScala.Game;

Fixtures.Initialize();
int checks = 0;
void Check(bool okay, string name) { if (!okay) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
MethodInfo Hook(string nested, string method) => typeof(ScalaCodeUnlocks).GetNestedType(nested, BindingFlags.NonPublic)!.GetMethod(method, Hidden)!;
var main = new MainSim();
MainSim.Inst = main;
EditorIntegration.Active = main;
IEnumerable<string> LoadInput(IEnumerable<string> original, MainSim? instance = null) { object?[] arguments = { instance ?? main, original }; Hook("LoadGrants", "Prefix").Invoke(null, arguments); return (IEnumerable<string>)arguments[1]!; }
bool Purchase(Farm farm, UnlockSO unlock, out bool nativeCalled) { object?[] arguments = { unlock, false }; nativeCalled = (bool)Hook("LeaderboardPurchase", "Prefix").Invoke(null, arguments)!; return nativeCalled ? farm.UnlockOrUpgrade(unlock) : (bool)arguments[1]!; }
void Tree(UnlockBox box, Dictionary<string, int> levels) => Hook("TreePresentation", "Postfix").Invoke(null, new object[] { box, levels });

var empty = ScalaCodeUnlocks.Augment(Array.Empty<string>());
Check(ScalaCodeUnlocks.UpgradeIds.Length == 15 && ScalaCodeUnlocks.UpgradeIds.Distinct().Count() == 15, "exact fifteen code-upgrade policy IDs");
Check(ScalaCodeUnlocks.UpgradeIds.All(id => Fixtures.Nodes.ContainsKey(id)), "approved IDs match the factual native capability fixtures");
Check(!empty.Any(ScalaCodeUnlocks.IsLeaderboard), "leaderboard is never a free code upgrade");
Check(ScalaCodeUnlocks.UpgradeIds.Where(id => !id.StartsWith("debug")).All(id => empty.Contains(id + "_1")), "normal grants encode levels before native dependent-token expansion");
Check(empty.Contains("debug") && empty.Contains("debug_2") && !empty.Contains("debug_1") && !empty.Contains("debug_2_1"), "native debug identifier exception remains literal");
Check(empty.SequenceEqual(ScalaCodeUnlocks.Augment(empty)), "augmentation is idempotent");
var original = new List<string> { "speed_2", "expand_3", "megafarm_2", "carrots", "loops", "costs_3", "leaderboard" };
var untouched = original.ToArray();
var augmented = ScalaCodeUnlocks.Augment(original);
Check(original.SequenceEqual(untouched), "source unlock collection is never mutated");
Check(augmented.Take(7).SequenceEqual(new[] { "speed_2", "expand_3", "megafarm_2", "carrots", "loops_1", "costs_3", "leaderboard" }), "input order and existing positive/legacy progress are preserved");
Check(!augmented.Contains("costs_1"), "higher existing code level is not downgraded");
var recovered = new Farm(ScalaCodeUnlocks.Augment(new[] { "loops_1", "loops_0" }));
Check(recovered.Level("loops") == 1, "final zero level is repaired even after an earlier positive entry");
var fresh = new Farm(empty);
Check(ScalaCodeUnlocks.UpgradeIds.All(id => fresh.Level(id) == 1), "all code nodes load as actually unlocked");
Check(Fixtures.Nodes.Values.SelectMany(n => n.unlocks).All(token => fresh.Level(token) > 0), "all actual declared API and language dependency flags are unlocked");
Check(new[] { "simulate", "get_cost", "print", "quick_print", "set_execution_speed", "set_world_size" }.All(token => fresh.Level(token) > 0), "simulation costs and output documentation/API flags exist at start");
Check(ScalaCodeUnlocks.SyntaxIds.All(token => fresh.Level(token) > 0), "if else elif for and range are standalone language grants");
Check(new[] { "plant", "speed", "expand", "megafarm", "carrots", "watering", "fertilizer", "leaderboard", "can_harvest", "get_world_size", "the_farmers_remains" }.All(id => fresh.Level(id) == 0), "physical progression and leaderboard gates remain untouched");
Check(fresh.Items == 100 && fresh.PurchaseCalls == 0, "free language grants charge no inventory and invoke no purchases");
Fixtures.Nodes["loops"].unlocks.Add("1tier_one_test");
Check(new Farm(ScalaCodeUnlocks.Augment(Array.Empty<string>())).Level("tier_one_test") == 1, "encoded first grant supports native tier-prefixed dependencies");
Fixtures.Nodes["loops"].unlocks.Remove("1tier_one_test");
var reload = new Farm(ScalaCodeUnlocks.Augment(fresh.Serialize()));
Check(fresh.Levels.OrderBy(p => p.Key).SequenceEqual(reload.Levels.OrderBy(p => p.Key)), "serialized code grants and dependency state survive a world reload");
var legacy = new Farm(augmented);
Check(legacy.Level("speed") == 2 && legacy.Level("expand") == 3 && legacy.Level("megafarm") == 2 && legacy.Level("leaderboard") == 1, "existing farm and earned leaderboard levels are retained");
IEnumerable<string> none = Array.Empty<string>();
Check(LoadInput(none).Any(), "resolved Native Scala load enters the SetupSim grant path");
EditorIntegration.Native = false;
Check(ReferenceEquals(LoadInput(none), none), "Default load leaves the original native enumerable unchanged");
EditorIntegration.Native = true;
EditorIntegration.Failed = true;
Check(ReferenceEquals(LoadInput(none), none), "unresolved or failed metadata never authorizes code grants");
EditorIntegration.Failed = false;
Check(ReferenceEquals(LoadInput(none, new MainSim()), none), "a different MainSim cannot receive this save's grants");
EditorIntegration.Loading = false;
Check(ReferenceEquals(LoadInput(none), none), "SetupSim outside the load phase does not acquire free grants");
var simulated = new Farm(Array.Empty<string>());
Check(simulated.Level("simulation") == 0 && simulated.Level("get_cost") == 0, "explicit simulation constructor input remains independent");
EditorIntegration.Loading = true;
main.SetupSim(LoadInput(Array.Empty<string>()));
EditorIntegration.Loading = false;
Check(Fixtures.Nodes.Values.Where(n => n.docs.Length > 0).All(n => main.OpenedDocs.Contains(n.docs)), "native SetupSim marks granted docs as seen before research UI refresh");
Check(main.Farm.Level("simulation") > 0 && Fixtures.Nodes["simulation"].docs.Length > 0, "native unlock tooltip has an accessible simulation documentation target");
var box = new UnlockBox { unlockSO = Fixtures.Nodes["costs"] };
box.Label.color = new() { r = .2f, g = .3f, b = .4f, a = .1f };
Tree(box, main.Farm.Levels);
Check(box.Label.color.a == 1f && box.Label.color.r == .2f && !main.Farm.Levels.ContainsKey("plant"), "granted code label is visible despite locked physical ancestors");
Check(box.Button.Interactable && box.Button.Text == "Unlock", "code visibility does not replace native purchase/button state or recurse into child gating");
var physical = new UnlockBox { unlockSO = new() { unlockName = "carrots" } };
physical.Label.color = new() { a = .1f };
Tree(physical, main.Farm.Levels);
Check(physical.Label.color.a == .1f, "physical upgrade presentation remains native");
var ungranted = new UnlockBox { unlockSO = Fixtures.Nodes["costs"] };
ungranted.Label.color = new() { a = .1f };
Tree(ungranted, new());
Check(ungranted.Label.color.a == .1f, "code node is not made visible by a fabricated unlock count");
var leaderboard = new UnlockSO { unlockName = "leaderboard" };
var purchaseFarm = new Farm(Array.Empty<string>());
Check(!Purchase(purchaseFarm, leaderboard, out bool nativeCall) && !nativeCall && purchaseFarm.Items == 100 && purchaseFarm.PurchaseCalls == 0, "Scala leaderboard purchase is rejected before cost removal");
Check(Purchase(purchaseFarm, new() { unlockName = "carrots" }, out nativeCall) && nativeCall && purchaseFarm.Items == 90, "ordinary physical upgrade purchases still use the native path");
EditorIntegration.Native = false;
var defaultFarm = new Farm(Array.Empty<string>());
Check(Purchase(defaultFarm, leaderboard, out nativeCall) && nativeCall && defaultFarm.Items == 90, "Default language leaderboard purchase remains native");
EditorIntegration.Failed = true;
var failedFarm = new Farm(Array.Empty<string>());
Check(!Purchase(failedFarm, leaderboard, out nativeCall) && !nativeCall && failedFarm.Items == 100, "failed save metadata also blocks leaderboard purchases without charge");
EditorIntegration.Native = true;
EditorIntegration.Failed = false;
foreach (int count in new[] { 0, 1 })
{
    var boardBox = new UnlockBox { unlockSO = leaderboard };
    Tree(boardBox, new() { { "leaderboard", count } });
    Check(!boardBox.Button.Interactable && boardBox.Button.Text == "Unavailable" && boardBox.Button.OverrideColors.disabledColor.r == .1f, "leaderboard UI stays disabled at actual level " + count);
}
EditorIntegration.Native = false;
var reloadedDefaultBox = new UnlockBox { unlockSO = leaderboard };
Tree(reloadedDefaultBox, new());
Check(reloadedDefaultBox.Button.Interactable && reloadedDefaultBox.Button.Text == "Unlock", "fresh native tree after a Default load has no Scala-disabled label or purchase state");
EditorIntegration.Native = true;
Tree(box, main.Farm.Levels);
Tree(box, main.Farm.Levels);
Check(box.Label.color.a == 1f && main.Farm.Level("get_cost") == 1, "repeated Info/tree refreshes retain actual counts and code brightness");

// Apply the production hooks to native-result adapters. These assertions do
// not emulate Unity's tree renderer or claim to execute native purchases.
foreach (string id in new[] { "leaderboard", "underground_leaderboard" })
{
    var node = new UnlockSO { unlockName = id };
    Check(ScalaCodeUnlocks.IsLeaderboard(id) && !ScalaCodeUnlocks.IsCodeUpgrade(id), id + " is blocked rather than pre-granted");
    Check(!new Farm(ScalaCodeUnlocks.Augment(Array.Empty<string>())).Levels.ContainsKey(id), id + " receives no free unlock");
    var farm = new Farm(Array.Empty<string>());
    Check(!Purchase(farm, node, out bool called) && !called && farm.Items == 100 && farm.PurchaseCalls == 0, id + " cannot charge for a Scala purchase");
    foreach (int level in new[] { 0, 1, 3 })
    {
        var entry = new UnlockBox { unlockSO = node };
        var counts = new Dictionary<string,int> { [id] = level };
        for (int refresh = 0; refresh < 3; refresh++) Tree(entry, counts);
        Check(!entry.Button.Interactable && entry.Button.Text == "Unavailable" && counts[id] == level, id + " remains disabled through Info refreshes at level " + level);
    }
    EditorIntegration.Native = false;
    var defaultEntry = new UnlockBox { unlockSO = node };
    Tree(defaultEntry, new());
    Check(defaultEntry.Button.Interactable && defaultEntry.Button.Text == "Unlock", id + " stays native in Default saves");
    var defaultPurchase = new Farm(Array.Empty<string>());
    Purchase(defaultPurchase, node, out bool defaultCalled);
    Check(defaultCalled && defaultPurchase.PurchaseCalls == 1, id + " keeps native purchase handling in Default saves");
    EditorIntegration.Failed = true;
    var blockedPurchase = new Farm(Array.Empty<string>());
    Check(!Purchase(blockedPurchase, node, out called) && !called && blockedPurchase.Items == 100, id + " also fails closed for unresolved save metadata");
    EditorIntegration.Failed = false;
    EditorIntegration.Native = true;
}
string[] physicalIds = { "plant", "carrots", "trees", "watering", "speed", "expand", "megafarm", "mining", "underground_senses", "coal", "iron", "prospecting", "jump", "quartz", "mushrooms", "bamboo", "rice", "dynamite", "special_soils", "ore_boost", "petrified_pumpkins", "treasure_map", "pyramid" };
var newScalaFarm = new Farm(ScalaCodeUnlocks.Augment(Array.Empty<string>()));
Check(physicalIds.All(id => newScalaFarm.Level(id) == 0), "base and mining progression is never granted as language support");
foreach (string id in physicalIds)
{
    var node = new UnlockSO { unlockName = id };
    var farm = new Farm(Array.Empty<string>());
    Purchase(farm, node, out bool called);
    Check(called && farm.PurchaseCalls == 1, id + " retains native parent, cost, and purchase handling");
    foreach (int level in new[] { 0, 1, 3 })
    {
        var entry = new UnlockBox { unlockSO = node };
        entry.Button.Text = "native-state";
        entry.Button.Interactable = level == 0;
        entry.Button.OverrideColors = new() { disabledColor = new() { r = .75f } };
        entry.Label.color = new() { a = .1f };
        var counts = new Dictionary<string,int> { [id] = level };
        for (int refresh = 0; refresh < 3; refresh++) Tree(entry, counts);
        Check(entry.Button.Text == "native-state" && entry.Button.Interactable == (level == 0) && entry.Button.OverrideColors.disabledColor.r == .75f && entry.Label.color.a == .1f && counts[id] == level,
            id + " native presentation and level survive Info refreshes at " + level);
    }
}
var miningProgress = new[] { "expand_5", "mining_3", "coal_2", "iron_1", "quartz_2", "ore_boost_4", "underground_leaderboard" };
var originalProgress = new Farm(miningProgress);
var scalaProgress = new Farm(ScalaCodeUnlocks.Augment(miningProgress));
Check(originalProgress.Levels.All(p => scalaProgress.Level(p.Key) == p.Value), "existing mining research levels survive Scala load without spending inventory");
var reloadedMining = new Farm(ScalaCodeUnlocks.Augment(scalaProgress.Serialize()));
Check(scalaProgress.Levels.OrderBy(p => p.Key).SequenceEqual(reloadedMining.Levels.OrderBy(p => p.Key)), "mining progression and language grants survive serialization/reload together");
ScalaCompletionUnlocks.IsUnlocked = _ => true;
Check(!ScalaCompletionUnlocks.Allows("leaderboard") && !ScalaCompletionUnlocks.Allows("underground_leaderboard") && !ScalaCompletionUnlocks.Allows("leaderboard_run"), "both leaderboard gates are excluded even when already unlocked");
Check(ScalaCompletionUnlocks.Allows("mining") && ScalaCompletionUnlocks.Allows("coal"), "normal unlocked mining completion remains available");
ScalaCompletionUnlocks.IsUnlocked = null;

// Metadata-only validation of actual supported native hook signatures.
string gameRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : @"C:\Program Files (x86)\Steam\steamapps\common\The Farmer Was Replaced";
string managed = Path.Combine(gameRoot, "TheFarmerWasReplaced_Data", "Managed");
AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { string path = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll"); return File.Exists(path) ? Assembly.LoadFrom(path) : null; };
var core = Assembly.LoadFrom(Path.Combine(managed, "Core.dll"));
const BindingFlags nativeFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
var setup = core.GetType("MainSim")!.GetMethod("SetupSim", nativeFlags)!;
Check(setup.GetParameters().Length == 5 && setup.GetParameters()[0].ParameterType == typeof(IEnumerable<string>), "installed SetupSim accepts the intercepted unlock enumerable");
var purchase = core.GetType("Farm")!.GetMethod("UnlockOrUpgrade", nativeFlags)!;
Check(purchase.ReturnType == typeof(bool) && purchase.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(new[] { "UnlockSO", "Boolean" }), "installed Farm purchase prefix matches actual cost-bearing API");
var tree = core.GetType("UnlockBox")!;
Check(tree.GetMethod("SetupRec", nativeFlags)!.GetParameters()[3].ParameterType == typeof(Dictionary<string, int>) && tree.GetField("codeText", nativeFlags)!.FieldType.Name == "TextMeshProUGUI" && tree.GetField("button", nativeFlags)!.FieldType.Name == "ColoredButton", "installed research-tree arguments and visual fields match supported native metadata");
AchievementChecks.Run(Check, core, managed);
Console.WriteLine($"PASS {checks} code-upgrade policy, native-hook adapter and installed-metadata checks; no live game or save mutation.");
