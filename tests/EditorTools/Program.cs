using FarmerScala.Editor;

int checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
var delay = new DiagnosticDelay();
Check(!delay.Observe(1, 0), "new project waits");
Check(!delay.Observe(1, 1.99), "errors hidden before two seconds");
Check(!delay.Observe(2, 1.99), "typing resets quiet period");
Check(!delay.Observe(2, 3), "continued wait after edit");
Check(delay.Observe(2, 4), "unchanged project eligible after quiet period");
Check(!delay.Observe(3, 4), "another file edit invalidates project diagnostics");
delay.Reset();
Check(!delay.Observe(3, 100), "reload of same revision waits again");
Check(delay.Observe(3, 102), "reload becomes eligible");
Check(DiagnosticRanges.Normalize("val x = \"😀\"", 10, 1, out int start, out int end) && start == 9 && end == 11, "UTF16 range expands whole surrogate pair");
Check(DiagnosticRanges.Normalize("abc", 3, 0, out start, out end) && start == 2 && end == 3, "EOF diagnostics have a visible marker");
Check(!DiagnosticRanges.Normalize("abc", -1, 2, out _, out _) && !DiagnosticRanges.Normalize("abc", 9, 1, out _, out _), "out-of-buffer errors rejected");
Check(!DiagnosticRanges.Normalize("", 0, 0, out _, out _), "empty buffer has no fake glyph");
Check(!DiagnosticRanges.Normalize("x\r\n", 1, 2, out _, out _), "newline-only span has no misleading squiggle");
Check(DiagnosticRanges.Normalize("abc", 1, int.MaxValue, out start, out end) && start == 1 && end == 3, "oversized ranges bounded without overflow");
Check(DiagnosticRanges.MapCaret("val x=1", "val x = 1", 7) == 9, "formatting keeps EOF caret");
Check(DiagnosticRanges.MapCaret("val x=1\nx", "val x = 1\nx", 2) == 2, "unchanged prefix caret stays in place");
string current = "import main.x\nx";
var model = new ScalaCompletionModel(new Dictionary<string, string> { ["main"] = "val x = 1", ["other"] = current });
var module = model.Resolve("other", current.IndexOf("main", StringComparison.Ordinal) + 4);
Check(module.Definition?.File == "main" && module.Definition.Offset == 0, "module qualifier opens file start");
Check(model.Resolve("other", current.Length).Definition?.Offset == 4, "imported symbol still opens declaration");
var shadow = new ScalaCompletionModel(new Dictionary<string, string> { ["main"] = "val x = 1", ["other"] = "val main = 42\nmain" });
Check(shadow.Resolve("other", "val main = 42\nmain".Length).Definition?.File == "other", "local names retain priority over module navigation");
string catalogCall = "val x = get_cost(Entities.Grass)";
int grassAt = catalogCall.IndexOf("Grass", StringComparison.Ordinal);
Check(ScalaCatalogReference.TryFind(catalogCall, grassAt, grassAt + 5, out var catalog, out int anchor) && catalog == "Entities.Grass" && anchor == catalogCall.IndexOf("Entities", StringComparison.Ordinal),
    "catalog member inside a call uses its own exact expression");
string spaced = "get_cost(Entities . Grass)";
int spacedGrass = spaced.IndexOf("Grass", StringComparison.Ordinal);
Check(ScalaCatalogReference.TryFind(spaced, spacedGrass, spacedGrass + 5, out catalog, out _) && catalog == "Entities.Grass", "catalog lookup tolerates spaced dots");
Check(!ScalaCatalogReference.TryFind("thing.Grass", 6, 11, out _, out _), "ordinary user members are not catalog expressions");
Console.WriteLine($"Editor tools: {checks} checks passed.");
