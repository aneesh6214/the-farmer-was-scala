using FarmerScala.Editor;

int checks = 0;
void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
var sources = new Dictionary<string, string>
{
    ["main"] = "/** Base farmer. */\nclass Bob(val age: Int):\n  /** Harvest this field. */\n  def farm(): Unit = harvest()\n  val name: String = \"bob\"\n\ncase class Point(x: Int, y: Int)\nclass Derek extends Bob(4):\n  def run(): Unit = farm()\n",
    ["f0"] = "import main.{Derek as D, Point}\nval derek = D()\nval other: D = derek\nval p = Point(1, 2)\nderek.farm\nother.\np.\n"
};
var model = new ScalaCompletionModel(sources);
string source = sources["f0"];
int farmEnd = source.IndexOf("derek.farm", StringComparison.Ordinal) + "derek.farm".Length;
var farm = model.Resolve("f0", farmEnd);
Check(farm.Definition?.File == "main", "cross-file inherited function navigation");
Check(farm.HoverText?.Contains("def farm()", StringComparison.Ordinal) == true, "function signature hover");
Check(farm.HoverText?.Contains("Harvest this field.", StringComparison.Ordinal) == true, "Scaladoc hover");
Check(model.TryMembers("f0", source.IndexOf("other.", StringComparison.Ordinal) + 6, out var inherited), "typed instance members resolve");
Check(new[] { "farm", "name", "age", "run" }.All(inherited.Contains), "inherited and declared members");
Check(inherited.IndexOf("run") < inherited.IndexOf("farm"), "local fallback prioritizes declared members over inherited members");
Check(model.TryMembers("f0", source.IndexOf("p.\n", StringComparison.Ordinal) + 2, out var point) && point.Contains("x") && point.Contains("y"), "case class constructor fields");
var constructor = model.Resolve("f0", source.IndexOf("D()", StringComparison.Ordinal) + 1);
Check(constructor.Definition?.File == "main", "import alias class navigation");
var spans = model.Spans("f0");
Check(spans.Any(s => s.Start == source.IndexOf("D()", StringComparison.Ordinal) && s.Kind == "class"), "alias class reference semantic color");
Check(spans.Any(s => s.Start == farmEnd - 4 && s.Kind == "function"), "uncalled method reference color");
var braces = new ScalaCompletionModel(new Dictionary<string, string> { ["a"] = "class Box { val item: Int = 1; def get(): Int = item }\nval b = new Box()\nb.\n" });
Check(braces.TryMembers("a", 76, out _) || braces.TryMembers("a", "class Box { val item: Int = 1; def get(): Int = item }\nval b = new Box()\nb.".Length, out _), "brace-defined class constructor");
string shadow = "val harvest = 4\nharvest\n";
var shadowModel = new ScalaCompletionModel(new Dictionary<string, string> { ["a"] = shadow });
Check(!shadowModel.Resolve("a", shadow.LastIndexOf("harvest", StringComparison.Ordinal) + 7).IsGameBuiltin, "local shadows builtin");
var palette = new ScalaSyntaxPalette { Keyword = "K", String = "S", Comment = "C", Number = "N", FunctionDeclaration = "F", FunctionCall = "F", TypeParameter = "T", Class = "T", Builtin = "B", Search = "M", SelectedSearch = "X" };
string painted = ScalaSyntaxHighlighter.Colorize(shadow, palette, semanticSpans: shadowModel.Spans("a"));
Check(!painted.Contains("<color=B>", StringComparison.Ordinal), "shadow does not inherit builtin green");
Check(ScalaSyntaxHighlighter.Colorize("harvest()", palette).Contains("<color=B>harvest</color>", StringComparison.Ordinal), "game API keeps builtin green");
Check(ScalaSyntaxHighlighter.Colorize("class Box", palette).Contains("<color=T>Box</color>", StringComparison.Ordinal), "class color is distinct");
Check(ScalaLexer.Lex("/* outside /* nested */ still */ val x = 1").Count(t => t.Kind == ScalaTokenKind.Comment) == 1, "nested Scala block comment");
Check(ScalaLexer.Lex("// def fake()\nval text = s\"value $x\"").Any(t => t.Kind == ScalaTokenKind.String && t.Text.StartsWith("s\"", StringComparison.Ordinal)), "Scala string interpolator token");
Check(ScalaLexer.Keywords.Contains("given") && ScalaLexer.Keywords.Contains("then") && !ScalaLexer.Keywords.Contains("elif"), "Scala 3 keyword set");
Check(ScalaSymbolLocation.TryIdentifier("`some name`", 4, out int start, out int end) && start == 0 && end == 11, "backtick identifier boundary");
Check(ScalaSymbolLocation.TryIdentifier("x ++ y", 2, out start, out end) && start == 2 && end == 4, "operator identifier boundary");
Check(ScalaSyntaxHighlighter.Colorize("Bob", palette, navigationStart: 0, navigationLength: 3).Contains("<u>", StringComparison.Ordinal), "Ctrl-hover underline rendering");
Check(ScalaSyntaxHighlighter.Colorize("<b>text", palette).Contains("<noparse><</noparse>", StringComparison.Ordinal), "player markup stays inert");
Check(ScalaHoverFormatter.Format("class Point(x: Int, y: Int)\n\nA point.", palette).Contains("<color=T>Point</color>", StringComparison.Ordinal), "native class hover signature colors");
Check(ScalaHoverFormatter.Format("def applyTwice[A](value: A)(f: A => A): A", palette).Contains("<color=T>A</color>", StringComparison.Ordinal), "generic parameter colors in hover signature");

string testRoot = Path.Combine(Path.GetTempPath(), "FarmerScalaEditor-" + Guid.NewGuid().ToString("N"));
try
{
    string save = Path.Combine(testRoot, "save"), defaultSave = Path.Combine(testRoot, "default");
    var store = new SourceStore(Path.Combine(testRoot, "state"));
    store.InitializeLanguage(save, "test", SaveLanguage.NativeScala);
    Check(store.ReadLanguage(save) == SaveLanguage.NativeScala, "per-save Scala marker");
    store.SaveSources(save, "test", true, new Dictionary<string, string> { ["f0"] = "harvest()" });
    Check(File.Exists(Path.Combine(SourceStore.SourceDirectory(save), "f0.scala")), "owned source extension is scala");
    Check(!File.Exists(Path.Combine(save, "f0.py")), "native vanilla files not overwritten");
    var snapshot = store.Snapshot(save, "f0", store.ReadSources(save));
    Check(snapshot.EntryFile == "f0.scala" && File.ReadAllText(Path.Combine(snapshot.Directory, snapshot.EntryFile)) == "harvest()", "immutable Scala build snapshot");
    File.WriteAllText(Path.Combine(SourceStore.SourceDirectory(save), "helper.scala"), "def helper(): Int = 42");
    File.WriteAllText(Path.Combine(SourceStore.SourceDirectory(save), "f0.scala"), "external edit");
    var raced = store.Snapshot(save, "f0", new Dictionary<string, string> { ["f0"] = "clicked revision" });
    Check(raced.Sources["helper"] == "def helper(): Int = 42" && File.Exists(Path.Combine(raced.Directory, "helper.scala")), "untracked external Scala module participates in snapshot fingerprint");
    Check(raced.Sources["f0"] == "clicked revision" && File.ReadAllText(Path.Combine(raced.Directory, "f0.scala")) == "clicked revision", "editor snapshot remains exact across external disk edit");
    // Real Windows file sharing: a short-lived reader must not lose an edit,
    // while a persistent lock must fail without damaging the previous revision.
    string metadata = Path.Combine(SourceStore.ProjectRoot(save), "project.json");
    using (var readerLock = File.Open(metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var release = Task.Run(() => { Thread.Sleep(40); readerLock.Dispose(); });
        store.SaveSources(save, "save", true, new Dictionary<string, string> { ["f0"] = "after sharing lock" });
        release.GetAwaiter().GetResult();
    }
    Check(File.ReadAllText(metadata).Contains("f0"), "temporary Windows sharing lock does not prevent atomic metadata replacement");
    string previousMetadata = File.ReadAllText(metadata);
    bool sharingFailure = false;
    using (var readerLock = File.Open(metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
        try { store.SaveSources(save, "save", true, new Dictionary<string, string> { ["f0"] = "persistent lock" }); }
        catch (IOException) { sharingFailure = true; }
    Check(sharingFailure && File.ReadAllText(metadata) == previousMetadata, "persistent sharing lock preserves the previous metadata and reports failure");
    Check(!Directory.GetFiles(SourceStore.ProjectRoot(save), "*.tmp", SearchOption.AllDirectories).Any(), "failed atomic writes clean up their test-owned temporary files");
    string caseSave = Path.Combine(testRoot, "case-rename");
    store.InitializeLanguage(caseSave, "case-rename", SaveLanguage.NativeScala);
    store.SaveSources(caseSave, "case-rename", true, new Dictionary<string, string> { ["main"] = "harvest()" });
    store.SaveSources(caseSave, "case-rename", true, new Dictionary<string, string> { ["Main"] = "harvest()" });
    Check(store.ReadSources(caseSave).Keys.Single() == "Main", "case-only module rename survives save reload");
    var renamed = store.Snapshot(caseSave, "Main", new Dictionary<string, string> { ["Main"] = "harvest()" });
    Check(renamed.Sources.Keys.Single() == "Main" && Path.GetFileName(Directory.GetFiles(renamed.Directory, "*.scala").Single()) == "Main.scala", "case-only Windows rename preserves exact module and entry spelling");
    store.InitializeLanguage(defaultSave, "default", SaveLanguage.Default);
    bool locked = false;
    try
    {
        store.InitializeLanguage(defaultSave, "default", SaveLanguage.NativeScala);
    }
    catch (IOException) { locked = true; }
    Check(locked && store.ReadLanguage(defaultSave) == SaveLanguage.Default, "save language cannot change");
    File.WriteAllText(Path.Combine(SourceStore.SourceDirectory(save), "old.py"), "pass");
    Check(!store.ReadSources(save).ContainsKey("old"), "Python files are not Scala modules");
}
finally
{
    // Test-owned unique directory, never a game save.
    if (Directory.Exists(testRoot))
        Directory.Delete(testRoot, true);
}
Console.WriteLine($"Scala editor checks passed: {checks}");
