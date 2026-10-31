using FarmerScala.Compiler;
using FarmerScala.Editor;
using Jint;
using System.Text.Json;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string scratch = Path.Combine(root, ".local", "module-checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
int checks = 0;
void Check(bool passed, string name) { checks++; if (!passed) throw new Exception(name); Console.WriteLine("PASS " + name); }
bool Invalid(Action action) { try { action(); return false; } catch (IOException) { return true; } }
Check(SourceStore.NormalizeFileNameInput("models.modeling.scala") == "models.modeling", "one terminal extension is optional user input");
Check(SourceStore.NormalizeFileNameInput("models.scala.modeling") == "models.scala.modeling", "internal scala segment remains a namespace");
Check(SourceStore.NormalizeFileNameInput(" models.modeling.SCALA ") == "models.modeling", "user input trims outer space and optional extension case-insensitively");
Check(new[] { "models..modeling", ".models", "models.", "../models", "models/path", "CON", "my file", "__farmerEntry" }.All(n => Invalid(() => SourceStore.ValidateFileName(n))), "unsafe and empty module segments are rejected");
Check(Invalid(() => SourceStore.ValidateModuleSet(new[] { "models", "models.modeling" })), "module versus namespace ambiguity is rejected");
Check(Invalid(() => SourceStore.ValidateModuleSet(new[] { "Models.one", "models.ONE" })), "case-insensitive Windows duplicates are rejected");
var store = new SourceStore(Path.Combine(scratch, "cache"));
string save = Path.Combine(scratch, "save");
store.InitializeLanguage(save, "test", SaveLanguage.NativeScala);
var sources = new Dictionary<string, string>
{
    ["helper"] = "val seed = 40\nquick_print(\"helper-init\")\n",
    ["models.modeling"] = "import helper.seed\ncase class Crop(value: Int)\nval value = seed + 2\n",
    ["models.scala.modeling"] = "val value = 7\nquick_print(\"deep-init\")\n",
    ["main"] = "import models.modeling\nimport models.scala.{modeling as deep}\nassert(modeling.value == 42)\nassert(modeling.Crop(1).value == 1)\nassert(deep.value == 7)\nquick_print(\"main-ok\")\n"
};
store.SaveSources(save, "test", true, sources);
Check(store.ReadSources(save)["models.scala.modeling"] == sources["models.scala.modeling"], "dotted module identities survive storage round trip");
var snapshot = store.Snapshot(save, "models.modeling", sources);
Check(snapshot.EntryFile == "models.modeling.scala" && File.Exists(Path.Combine(snapshot.Directory, snapshot.EntryFile)), "dotted entry snapshot has one real file extension");
Check(snapshot.Sources.Count == 4 && snapshot.Sources.ContainsKey("models.scala.modeling"), "snapshot includes exact dotted identities without extension normalization");
Check(Invalid(() => store.SaveSources(save, "test", true, new Dictionary<string, string> { ["models"] = "val x=1", ["models.child"] = "val x=2" })) && store.ReadSources(save).Count == 4, "ambiguous save rejected before source mutation");
var local = new ScalaCompletionModel(new Dictionary<string, string>
{
    ["models.modeling"] = "class Bob { def farm(): Unit = harvest() }\nval count = 1\n",
    ["models.other"] = "val count = 2\n",
    ["models.scala.modeling"] = "val value = 3\n",
    ["main"] = "import models.modeling\nimport models.modeling.{Bob as Farmer, count}\nval bob = Farmer()\nmodels.\nmodels.modeling.\nmodeling.\nbob.\n"
});
string raw = "import models.modeling\nimport models.modeling.{Bob as Farmer, count}\nval bob = Farmer()\nmodels.\nmodels.modeling.\nmodeling.\nbob.\n";
Check(local.TryMembers("main", raw.IndexOf("models.\n", StringComparison.Ordinal) + 7, out var children) && new[] { "modeling", "other", "scala" }.All(children.Contains), "namespace completion lists direct child modules");
Check(local.TryMembers("main", raw.IndexOf("models.modeling.\n", StringComparison.Ordinal) + 16, out var members) && members.Contains("Bob") && members.Contains("count"), "qualified module completion includes file declarations");
Check(local.TryMembers("main", raw.LastIndexOf("modeling.\n", StringComparison.Ordinal) + 9, out members) && members.Contains("Bob"), "imported module alias completion resolves");
Check(local.TryMembers("main", raw.LastIndexOf("bob.\n", StringComparison.Ordinal) + 4, out members) && members.Contains("farm"), "class imported from dotted module retains methods");
Check(local.Resolve("main", raw.IndexOf("models.modeling", StringComparison.Ordinal) + 15).Definition?.File == "models.modeling", "leaf module qualifier navigates to matching file");
Check(local.Resolve("main", raw.IndexOf("models.modeling", StringComparison.Ordinal) + 6).Definition?.File == "models.modeling", "namespace qualifier navigates to first module beneath it");

string project = Path.Combine(scratch, "project");
Directory.CreateDirectory(project);
foreach (var item in sources) File.WriteAllText(Path.Combine(project, item.Key + ".scala"), item.Value);
using var compiler = new CompilerService(Path.Combine(root, "sdk"), Path.Combine(root, ".local/toolchain"));
var result = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "build"));
Check(result.ok, "real Scala.js compiles dotted packages and imported module aliases: " + JsonSerializer.Serialize(result.diagnostics));
List<string> Execute(string entry)
{
    var output = new List<string>();
    var engine = new Engine();
    engine.SetValue("__farmerGlobals", new object());
    engine.SetValue("__farmerAction", new Func<string, string, string>((op, json) => { if (op != "quick_print") throw new Exception(op); output.Add(JsonDocument.Parse(json).RootElement[0].GetString()!); return "null"; }));
    engine.Execute(File.ReadAllText(result.artifact!));
    engine.Invoke(engine.Evaluate("runFarm"), entry);
    return output;
}
var output = Execute("main.scala");
Check(output.SequenceEqual(new[] { "helper-init", "deep-init", "main-ok" }), "dotted imports initialize default-package helpers once and execute selected main");
Check(Execute("models.modeling.scala").SequenceEqual(new[] { "helper-init" }), "dotted file can be selected as the entry point");
Check(Execute("models.scala.modeling.scala").SequenceEqual(new[] { "deep-init" }), "internal scala namespace and terminal file extension dispatch correctly");
File.WriteAllText(Path.Combine(project, "models.modeling.scala"), "val broken: Int = \"wrong\"\n");
var broken = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "build"));
Check(!broken.ok && broken.diagnostics.Any(d => d.file == "models.modeling.scala" && d.line == 1 && d.column > 1), "compiler diagnostic maps dotted source to raw first line");
File.WriteAllText(Path.Combine(project, "models.scala"), "val x = 1\n");
var ambiguous = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "build"));
Check(!ambiguous.ok && ambiguous.diagnostics.Any(d => d.message.Contains("namespace", StringComparison.Ordinal)), "compiler also rejects ambiguous externally supplied module paths");
Console.WriteLine($"Dotted module checks passed: {checks}");
