using System.Text.Json;
using FarmerScala.Compiler;
using FarmerScala.Editor;
using FarmerScala.Runtime;
using Jint;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string scratch = Path.Combine(root, ".local/release110-checks");
Directory.CreateDirectory(scratch);
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
var modules = new[] { "PlantingUtils", "PositionUtils", "models.Position", "main" };
Check(ScalaModuleCompletion.Merge(new[] { "private", "Thread$Builder$OfPlatform" }, modules, "import Pla", 10).SequenceEqual(new[] { "PlantingUtils" }), "import prefix retains project modules and excludes unrelated suggestions");
Check(ScalaModuleCompletion.Merge(Array.Empty<string>(), modules, "import models.", 14).Contains("Position"), "namespace completion retains child module");
foreach (string modifiers in new[] { "override", "private", "protected", "private[this] final", "@deprecated(\"old\", \"1\")\nprivate", "@deprecated(\n  \"old\",\n  \"1\"\n)\nprivate" })
{
    string text = "class Example {\n/** Attached description. */\n" + modifiers + " def work(): Int = 1\n}\nval e = new Example\ne.work()\n";
    var model = new ScalaCompletionModel(new Dictionary<string,string> { ["main"] = text });
    Check(model.Resolve("main", text.LastIndexOf("work", StringComparison.Ordinal) + 4).HoverText?.Contains("Attached description.") == true, "Scaladoc attaches across " + modifiers.Replace('\n', ' '));
}
string nested = "f(Seq(1, 2))";
Check(ScalaParenthesisMatch.Find(nested, 1) == (1, nested.Length - 1), "outer parentheses match across nested expressions");
Check(ScalaParenthesisMatch.Find("f(\n 3\n)", 7) == (6, 1), "closing parenthesis matches opening across lines");
Check(ScalaParenthesisMatch.Find("val s = \"(text)\" // (comment)", 9) == (-1, -1), "comments and string text do not create pairs");
string interpolation = "s\"value ${f(1)}\"";
int paren = interpolation.IndexOf('(');
Check(ScalaParenthesisMatch.Find(interpolation, paren).Second == interpolation.IndexOf(')'), "code within string interpolation has matching parentheses");
var constantData = new[] { ("Grounds", "Soil", false), ("Grounds", "Red_Block", true), ("Items", "Hay", false), ("Items", "Coal", true) };
Check(!SdkProfile.SelectCatalog(constantData, false)["Grounds"].Contains("Red_Block") &&
      !SdkProfile.SelectCatalog(constantData, false)["Items"].Contains("Coal"), "native DLC flags exclude mining constants even when public and internal names differ");
Check(SdkProfile.SelectCatalog(constantData, true)["Grounds"].Contains("Red_Block"), "owned DLC retains its ground constants");
var catalog = new Dictionary<string,string[]>();
string bundled = Path.Combine(root, "sdk");
string baseSdk = SdkProfile.Build(bundled, scratch, false, catalog);
string miningSdk = SdkProfile.Build(bundled, scratch, true, catalog);
Check(!Directory.GetFiles(baseSdk, "*Mining*", SearchOption.AllDirectories).Any(), "base SDK contains no mining source");
Check(!File.ReadAllText(Path.Combine(baseSdk, "game/api.scala")).Contains("export MiningApi"), "base API does not export mining functions");
Check(File.Exists(Path.Combine(miningSdk, "game/MiningApi.scala")), "mining SDK includes its separate implementation");
Check(File.ReadAllText(Path.Combine(miningSdk, "game/api.scala")).Contains("Option[Double | (Int, Int) | String]"), "mining measure includes native string results");

if (args.Length == 2 && args[0] == "--verify-save")
{
    var store = new SourceStore(Path.Combine(scratch, "save-check"));
    var manifest = store.ReadManifest(args[1]);
    var savedSources = store.ReadSources(args[1]);
    Check(store.ReadLanguage(args[1]) == SaveLanguage.NativeScala, "fixture has consistent Scala language metadata");
    Check(SourceStore.IsUsableLayout(manifest.ScalaLayout) && SourceStore.IsUsableLayout(manifest.VanillaLayout), "fixture layouts match production source-store requirements");
    Check(savedSources.Keys.Order().SequenceEqual(manifest.Modules.Order()) && savedSources.ContainsKey("main"), "fixture module metadata matches its source files");
    Console.WriteLine("Verified test save without changing files: " + args[1]);
    return;
}

if (args.Length == 3 && args[0] == "--prepare-sdk")
{
    var native = JsonSerializer.Deserialize<Dictionary<string,string[]>>(File.ReadAllText(args[1]))!;
    Console.WriteLine(SdkProfile.Build(bundled, args[2], true, native));
    return;
}
string project = Path.Combine(scratch, "project");
Directory.CreateDirectory(project);
File.WriteAllText(Path.Combine(project, "Helper.scala"), "@main def helperEntry(): Unit = throw new Exception(\"Imported entry was invoked\")\ndef twice(n: Int): Int = models.Transitive.twice(n)\n");
File.WriteAllText(Path.Combine(project, "Unrelated.scala"), "val value = 17\n");
File.WriteAllText(Path.Combine(project, "models.Transitive.scala"), "def twice(n: Int): Int = n * 2\n");
File.WriteAllText(Path.Combine(project, "main.scala"), "import Helper.twice\n@main def farm(): Unit = { assert(twice(3) == 6); quick_print(\"entry-ok\") }\n");
using (var compiler = new CompilerService(baseSdk, Path.Combine(root, ".local/toolchain")))
{
    var result = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "base-build"));
    Check(result.ok, "annotated entry and imported annotated helper compile: " + JsonSerializer.Serialize(result.diagnostics));
    var engine = new Engine();
    var printed = new List<string>();
    engine.SetValue("__farmerAction", new Func<string,string,string>((op, json) => { printed.Add(json); return "null"; }));
    engine.Execute(File.ReadAllText(result.artifact!));
    engine.Invoke(engine.Evaluate("runFarm"), "main.scala");
    Check(printed.Count == 1 && printed[0].Contains("entry-ok"), "Play invokes only the selected @main exactly once");
    Check(result.dependencies!.Contains("main") && result.dependencies.Contains("Helper") && result.dependencies.Contains("models.Transitive") && !result.dependencies.Contains("Unrelated"), "resolved execution dependencies exclude unrelated files");
    File.WriteAllText(Path.Combine(project, "main.scala"), "@main def farm(n: Int): Unit = ()\n");
    var invalid = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "base-build"));
    Check(!invalid.ok && invalid.diagnostics.Any(d => d.message.Contains("empty parameter list")), "unsupported main signature has a source diagnostic");
    File.WriteAllText(Path.Combine(project, "main.scala"), "@main def farm() = 123\n");
    var nonUnit = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "base-build"));
    Check(!nonUnit.ok && nonUnit.diagnostics.Any(d => d.line == 1 && d.message.Contains("returning Unit")), "inferred non-Unit entry points report at the annotation");
    File.WriteAllText(Path.Combine(project, "main.scala"), "@main def requireUnit(): scala.Unit = quick_print(\"qualified-unit\")\n");
    var qualifiedUnit = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "base-build"));
    Check(qualifiedUnit.ok, "qualified Unit return types and entry names cannot collide with generated validation: " + JsonSerializer.Serialize(qualifiedUnit.diagnostics));
    File.WriteAllText(Path.Combine(project, "main.scala"), "dig()\n");
    var noDlc = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "base-build"));
    Check(!noDlc.ok && noDlc.diagnostics.Any(d => d.message.Contains("dig")), "mining-only function does not compile against base SDK");
}
File.WriteAllText(Path.Combine(project, "main.scala"), "@main def mining(): Unit = {\n val dug: Boolean = dig()\n val placed: Boolean = place(Grounds.Soil)\n val z: Int = get_pos_z(Some(North))\n val h: Option[Double] = get_hardness()\n val s: Option[Double] = get_stability()\n val iron: Option[Directions] = prospect_iron()\n val quartz: Option[Double] = prospect_quartz()\n val pushed: Boolean = push(East)\n val canPush: Boolean = can_push(West)\n val changed: Boolean = hover(2)\n val height: Int = get_hover()\n val measured: Option[Double | (Int, Int) | String] = measure()\n assert(measured.contains(\"NESW\")); jump(Unlocks.Grass)\n}\n");
using (var compiler = new CompilerService(miningSdk, Path.Combine(root, ".local/toolchain")))
{
    var result = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "mining-build"));
    Check(result.ok, "all typed mining APIs compile: " + JsonSerializer.Serialize(result.diagnostics));
    var actions = new List<string>();
    var engine = new Engine();
    engine.SetValue("__farmerAction", new Func<string,string,string>((op,json) => { actions.Add(op); return op switch {
        "dig" or "place" or "push" or "can_push" or "hover" => "true", "get_pos_z" => "-2", "get_hover" => "2",
        "get_hardness" or "get_stability" or "prospect_quartz" => "3.5",
        "prospect_iron" => "{\"$\":\"enum\",\"family\":\"Direction\",\"name\":\"North\"}",
        "measure" => "\"NESW\"", "jump" => "null", _ => throw new Exception(op) }; }));
    engine.Execute(File.ReadAllText(result.artifact!)); engine.Invoke(engine.Evaluate("runFarm"), "main.scala");
    Check(SdkProfile.MiningActions.All(actions.Contains), "mining arguments and typed results survive actual Scala.js execution");
}
Console.WriteLine($"Release 1.1.0 checks passed: {checks}");
