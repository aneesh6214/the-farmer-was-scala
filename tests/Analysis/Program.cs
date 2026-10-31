using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FarmerScala.Editor;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string metadata = Path.Combine(root, ".local/compiler-checks/build/compiler-classpath.json");
bool formatOnly = args.Contains("--format-only", StringComparer.Ordinal);
if (!formatOnly && !File.Exists(metadata)) throw new Exception("Run CompilerTests first to produce a real Scala.js target classpath.");
string libraryRoot = Path.Combine(root, ".local/analysis");
string classpath = string.Join(Path.PathSeparator, File.ReadAllLines(Path.Combine(libraryRoot, "classpath.txt")).Where(l => l.Length != 0).Select(l => Path.Combine(libraryRoot, l)));
var info = new ProcessStartInfo(Path.Combine(root, ".local/toolchain/java/bin/java.exe"))
{
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    StandardInputEncoding = Encoding.UTF8,
    StandardOutputEncoding = Encoding.UTF8
};
foreach (string arg in new[] { "-Xmx768m", "-cp", classpath, "farmer.analysis.Main" }) info.ArgumentList.Add(arg);
using var process = Process.Start(info) ?? throw new Exception("Could not start real Scala presentation compiler.");
var errors = process.StandardError.ReadToEndAsync();
long id = 0;
int checks = 0;
async Task<JsonObject> Send(JsonObject request)
{
    request["id"] = ++id;
    await process.StandardInput.WriteLineAsync(request.ToJsonString());
    await process.StandardInput.FlushAsync();
    for (int line = 0; line < 32; line++)
    {
        string? text = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(25));
        if (text == null)
            throw new Exception("PC exited: " + await errors);
        JsonObject response;
        try
        {
            response = JsonNode.Parse(text)!.AsObject();
        }
        catch { continue; }
        if ((long?)response["id"] != id)
            continue;
        if ((bool?)response["ok"] != true)
            throw new Exception("PC response: " + response.ToJsonString());
        return response;
    }
    throw new Exception("PC returned unexpected stdout.");
}
void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
string workspace = Path.Combine(root, ".local/analysis-checks");
try
{
    var standaloneFormat = await Send(new JsonObject { ["op"] = "format", ["file"] = "draft", ["sources"] = new JsonObject { ["draft"] = "val count=3\n" } });
    Check(((string?)standaloneFormat["source"])?.Contains("val count = 3", StringComparison.Ordinal) == true, "formatting works before compiler configuration or source analysis");
    await FormatterChecks.Run(Send, Check);
    if (formatOnly)
    {
        Console.WriteLine($"Real Scalafmt checks passed: {checks}");
        return;
    }
    await Send(new JsonObject
    {
        ["op"] = "configure",
        ["classpath"] = JsonNode.Parse(File.ReadAllText(metadata)),
        ["sdk"] = Path.Combine(root, "sdk"),
        ["workspace"] = workspace,
        ["librarySources"] = Path.Combine(libraryRoot, "sources")
    });
    string lib = "trait Parent:\n  /** The inherited count. */\n  def inherited(): Int = 7\nclass Bob extends Parent:\n  def own(): String = \"bob\"\ncase class Box[A](value: A)\n";
    string raw = "import lib.*\nval bob = Bob()\nval mapped = List(1, 2).map(_ + 1)\nbob.\n";
    JsonObject Request(string op, string text, int offset) => new()
    {
        ["op"] = op,
        ["file"] = "f0",
        ["offset"] = offset,
        ["sources"] = new JsonObject { ["lib"] = lib, ["f0"] = text }
    };
    var completion = await Send(Request("complete", raw, raw.LastIndexOf("bob.", StringComparison.Ordinal) + 4));
    var names = completion["completions"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
    Check(names.Contains("inherited") && names.Contains("own"), "real compiler inferred inherited completion");
    raw = raw.Replace("bob.\n", "bob.inherited()\n");
    int inherited = raw.LastIndexOf("inherited", StringComparison.Ordinal);
    var resolution = await Send(Request("resolve", raw, inherited + 4));
    Check((string?)resolution["definition"]?["file"] == "lib", "real compiler cross-file definition: " + resolution.ToJsonString());
    Check((string?)resolution["hover"]?["text"] is string hover && hover.Contains("Int", StringComparison.Ordinal), "real compiler typed hover");
    Check((string?)resolution["hover"]?["text"] is string doc && doc.Contains("The inherited count.", StringComparison.Ordinal), "cross-file Scaladoc in native hover payload");
    var module = await Send(Request("resolve", raw, raw.IndexOf("lib.*", StringComparison.Ordinal) + 2));
    Check((string?)module["definition"]?["file"] == "lib" && (int?)module["definition"]?["offset"] == 0, "generated module reference opens the beginning of its raw source file: " + module.ToJsonString());
    JsonObject Dotted(string op, string text, int offset) => new()
    {
        ["op"] = op,
        ["file"] = "f0",
        ["offset"] = offset,
        ["sources"] = new JsonObject { ["models.modeling"] = "class Bob { def count: Int = 7 }\n", ["models.other"] = "val x = 3\n", ["f0"] = text }
    };
    var packageMembers = await Send(Dotted("complete", "models.\n", 7));
    Check(packageMembers["completions"]!.AsArray().Any(n => (string?)n == "modeling") && packageMembers["completions"]!.AsArray().Any(n => (string?)n == "other"), "real compiler completes child module names after a namespace dot");
    string dottedRaw = "import models.modeling\nval bob = modeling.Bob()\nbob.count\n";
    var dottedModule = await Send(Dotted("resolve", dottedRaw, dottedRaw.IndexOf("modeling", StringComparison.Ordinal) + 4));
    Check((string?)dottedModule["definition"]?["file"] == "models.modeling" && (int?)dottedModule["definition"]?["offset"] == 0, "real compiler navigates dotted module qualifier to file start: " + dottedModule.ToJsonString());
    var dottedNamespace = await Send(Dotted("resolve", dottedRaw, dottedRaw.IndexOf("models", StringComparison.Ordinal) + 3));
    Check((string?)dottedNamespace["definition"]?["file"] == "models.modeling", "real compiler namespace navigation opens its first child module: " + dottedNamespace.ToJsonString());
    var dottedClass = await Send(Dotted("resolve", dottedRaw, dottedRaw.IndexOf("Bob()", StringComparison.Ordinal) + 2));
    Check((string?)dottedClass["definition"]?["file"] == "models.modeling" && (int?)dottedClass["definition"]?["offset"] == 6, "dotted class import keeps raw definition offsets");
    var analysis = await Send(Request("analyze", raw, 0));
    Check(analysis["spans"]!.AsArray().Any(n => (int?)n?["start"] == inherited && (string?)n?["kind"] == "function"), "real compiler method semantic token");
    int classReference = raw.IndexOf("Bob()", StringComparison.Ordinal);
    Check(analysis["spans"]!.AsArray().Any(n => (int?)n?["start"] == classReference && (string?)n?["kind"] == "class"), "real compiler class semantic token");
    string std = "val values = List(1, 2, 3)\nvalues.\n";
    var standard = await Send(Request("complete", std, std.LastIndexOf("values.", StringComparison.Ordinal) + 7));
    var standardNames = standard["completions"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
    Check(standardNames.Contains("map") && standardNames.Contains("collect") && standardNames.Contains("flatMap"), "real compiler standard collection completion");
    string library = "val option: Option[Int] = Some(1)\nval mapped = option.map(_ + 1)\n";
    var sourceTimer = Stopwatch.StartNew();
    var option = await Send(Request("resolve", library, library.IndexOf("Option", StringComparison.Ordinal) + 3));
    Console.WriteLine($"First lazy library source/documentation lookup: {sourceTimer.ElapsedMilliseconds} ms");
    Check((bool?)option["definition"]?["isLibrary"] == true && (string?)option["definition"]?["source"] is string optionSource && optionSource.Contains("class Option", StringComparison.Ordinal), "Option navigates to the exact bundled library source: " + option.ToJsonString()[..Math.Min(500, option.ToJsonString().Length)]);
    Check((string?)option["hover"]?["text"] is string optionDoc && optionDoc.Contains("optional values", StringComparison.OrdinalIgnoreCase), "Option displays bundled official Scaladoc");
    var map = await Send(Request("resolve", library, library.IndexOf(".map", StringComparison.Ordinal) + 3));
    Check((bool?)map["definition"]?["isLibrary"] == true && (string?)map["hover"]?["text"] is string mapDoc && mapDoc.Contains("@param", StringComparison.Ordinal), "library member lookup preserves structured Scaladoc parameter tags: " + map.ToJsonString()[..Math.Min(300, map.ToJsonString().Length)]);
    var some = await Send(Request("resolve", library, library.IndexOf("Some", StringComparison.Ordinal) + 3));
    Check((bool?)some["definition"]?["isLibrary"] == true && ((string?)some["definition"]?["source"])?.Contains("class Some", StringComparison.Ordinal) == true, "synthetic case-class companion navigates to shared library file");
    string moveRaw = "move(North)\n";
    var move = await Send(Request("resolve", moveRaw, 3));
    string moveHover = (string?)move["hover"]?["text"] ?? "";
    Check((bool?)move["isGameBuiltin"] == true && move["definition"] == null && moveHover.Contains("Boolean", StringComparison.Ordinal) && moveHover.Contains("@param direction", StringComparison.Ordinal) && moveHover.Contains("@return", StringComparison.Ordinal), "game move hover has typed signature and structured SDK parameters/returns: " + move.ToJsonString());
    string aliasRaw = "import game.api.{move as walk}\nwalk(North)\n";
    var alias = await Send(Request("resolve", aliasRaw, aliasRaw.LastIndexOf("walk", StringComparison.Ordinal) + 3));
    Check((bool?)alias["isGameBuiltin"] == true && (string?)alias["gameFunctionName"] == "move" && ((string?)alias["hover"]?["text"])?.Contains("@param direction", StringComparison.Ordinal) == true, "renamed SDK import preserves resolved native documentation identity: " + alias.ToJsonString());
    string costRaw = "get_cost(Entities.Tree)\nget_cost(Unlocks.Speed, 2)\n";
    var cost = await Send(Request("resolve", costRaw, 4));
    string costHover = (string?)cost["hover"]?["text"] ?? "";
    Check(costHover.Contains("Option[Map[Items, Double]]", StringComparison.Ordinal) && costHover.Contains("@param thing", StringComparison.Ordinal), "game get_cost hover uses Scala enum and optional typed map signature: " + cost.ToJsonString());
    var costLevel = await Send(Request("resolve", costRaw, costRaw.LastIndexOf("get_cost", StringComparison.Ordinal) + 4));
    Check(((string?)costLevel["hover"]?["text"])?.Contains("@param level", StringComparison.Ordinal) == true, "get_cost overloaded hover resolves the corresponding structured level parameter: " + costLevel.ToJsonString());
    string builtin = "harvest()\n";
    var builtinResult = await Send(Request("resolve", builtin, 3));
    Check((bool?)builtinResult["isGameBuiltin"] == true && builtinResult["definition"] == null, "game API preserves native documentation behavior");
    var builtinColors = await Send(Request("analyze", builtin, 0));
    Check(builtinColors["spans"]!.AsArray().Any(n => (string?)n?["kind"] == "builtin"), "game API semantic green category");
    string shadow = "def harvest(): Int = 4\nharvest()\n";
    var shadowColors = await Send(Request("analyze", shadow, 0));
    Check(!shadowColors["spans"]!.AsArray().Any(n => (string?)n?["kind"] == "builtin"), "shadowed game name does not become builtin green");
    string repeated = string.Concat(Enumerable.Repeat("harvest()\n", 256));
    var repeatedColors = await Send(Request("analyze", repeated, 0));
    Check(repeatedColors["spans"]!.AsArray().Count(n => (string?)n?["kind"] == "builtin") == 256, "builtin identity colors remain stable beyond 128 references");
    string memberShadow = "class Farmer:\n  def harvest(): Int = 1\nval farmer = Farmer()\nfarmer.harvest()\n";
    var memberColors = await Send(Request("analyze", memberShadow, 0));
    Check(!memberColors["spans"]!.AsArray().Any(n => (string?)n?["kind"] == "builtin"), "user member harvest does not inherit native green");
    string unicode = "val marker = \"🚜\"\nclass Café:\n  def récolte(): Int = 1\nval café = Café()\ncafé.récolte\n";
    int unicodeReference = unicode.LastIndexOf("récolte", StringComparison.Ordinal);
    var unicodeColors = await Send(Request("analyze", unicode, 0));
    var localUnicode = new ScalaCompletionModel(new Dictionary<string, string> { ["f0"] = unicode }).Spans("f0");
    var compilerUnicode = unicodeColors["spans"]!.AsArray().Select(n => new SemanticSpan { Start = (int)n!["start"]!, Length = (int)n["length"]!, Kind = (string)n["kind"]! }).ToArray();
    Check(SemanticSpanMerger.Merge(localUnicode, compilerUnicode).Any(n => n.Start == unicodeReference && n.Length == 7 && n.Kind == "function"), "hybrid semantic offsets stay UTF-16 after emoji and uncalled Unicode methods");
    string backtick = "class Bob:\n  def `some name`(): Int = 1\nval b = Bob()\nb.`some name`\n";
    var backtickTarget = await Send(Request("resolve", backtick, backtick.LastIndexOf("some name", StringComparison.Ordinal) + 4));
    Check((int?)backtickTarget["definition"]?["offset"] == backtick.IndexOf("`some name`", StringComparison.Ordinal), "backtick definition offset is a source range: " + backtickTarget.ToJsonString());
    var localBacktick = new ScalaCompletionModel(new Dictionary<string, string> { ["f0"] = backtick }).Resolve("f0", backtick.LastIndexOf("some name", StringComparison.Ordinal) + 4);
    var mergedBacktick = AnalysisResolutionMerger.Merge(localBacktick, new AnalysisResolution { HoverText = (string?)backtickTarget["hover"]?["text"] });
    Check(mergedBacktick.HoverText?.Contains("def `some name`", StringComparison.Ordinal) == true, "uncalled method retains declared hover signature when PC reports synthetic result type");
    string removedRaw = "import lib.*\nval bob = Bob()\nbob.\n";
    var removed = await Send(new JsonObject
    {
        ["op"] = "complete",
        ["file"] = "f0",
        ["offset"] = removedRaw.Length - 1,
        ["sources"] = new JsonObject { ["f0"] = removedRaw }
    });
    Check(!removed["completions"]!.AsArray().Any(n => (string?)n == "own" || (string?)n == "inherited"), "deleted source cannot survive through last compiled classes");
    string invalid = "val marker = \"🚜\"\r\nval count: Int = \"five\"\r\n";
    var diagnostics = await Send(new JsonObject { ["op"] = "diagnostics", ["sources"] = new JsonObject { ["f0"] = invalid, ["other"] = "val value: Boolean = 3\n" } });
    var problems = diagnostics["diagnostics"]!.AsArray();
    Check(problems.Any(d => (string?)d?["file"] == "f0" && (int?)d?["start"] == invalid.IndexOf("\"five\"", StringComparison.Ordinal) && (string?)d?["severity"] == "error"), "type diagnostics use exact raw UTF-16 positions after emoji/CRLF: " + diagnostics.ToJsonString());
    Check(problems.Any(d => (string?)d?["file"] == "other"), "diagnostics cover every current project module");
    Check(problems.All(d => !((string?)d?["message"] ?? "").StartsWith("Either", StringComparison.Ordinal)) && problems.Any(d => ((string?)d?["message"])?.Contains("Found", StringComparison.OrdinalIgnoreCase) == true), "diagnostics unwrap LSP Either into the actual compiler explanation");
    var corrected = await Send(new JsonObject { ["op"] = "diagnostics", ["sources"] = new JsonObject { ["f0"] = invalid.Replace("\"five\"", "5", StringComparison.Ordinal) } });
    Check(corrected["diagnostics"]!.AsArray().Count == 0, "fixed project source clears obsolete errors");
    string messy = "val count=3\nfor(i<-0 until count){quick_print(i)}\n";
    var formatTimer = Stopwatch.StartNew();
    var formatted = await Send(Request("format", messy, 0));
    Console.WriteLine($"First explicit Scalafmt request: {formatTimer.ElapsedMilliseconds} ms");
    Check((string?)formatted["source"] is string clean && clean.Contains("val count = 3", StringComparison.Ordinal) && clean.Contains("quick_print(i)", StringComparison.Ordinal) && !clean.Contains("__FarmerFormat", StringComparison.Ordinal), "formatter handles script statements and removes temporary object: " + formatted.ToJsonString());
    formatTimer.Restart();
    var again = await Send(Request("format", (string)formatted["source"]!, 0));
    Console.WriteLine($"Warm explicit Scalafmt request: {formatTimer.ElapsedMilliseconds} ms");
    Check((string?)again["source"] == (string?)formatted["source"], "formatting is idempotent");
    string stringLiteral = "val text = \"\"\"first\n  two leading spaces\nlast\"\"\"\nquick_print(text)\n";
    var literalFormat = await Send(Request("format", stringLiteral, 0));
    Check((string?)literalFormat["source"] is string literal && literal.Contains("\n  two leading spaces\n", StringComparison.Ordinal), "formatter preserves multiline string literal contents: " + literalFormat.ToJsonString());
    string significant = "def inc(x:Int):Int =\n  x+1\nval result=inc(2)\nquick_print(result)\n";
    var significantFormat = await Send(Request("format", significant, 0));
    Check((string?)significantFormat["source"] is string significantResult && significantResult.Contains("\nval result = inc(2)", StringComparison.Ordinal), "formatter preserves significant indentation and top-level statement scope: " + significantFormat.ToJsonString());
    var significantDiagnostics = await Send(Request("diagnostics", (string)significantFormat["source"]!, 0));
    Check(significantDiagnostics["diagnostics"]!.AsArray().Count == 0, "formatted indentation-based script typechecks without running it: " + significantDiagnostics.ToJsonString());
    string interpolation = "val n=3\nval text=s\"\"\"first $n\n  keep ${n+1}\nlast\"\"\"\n";
    var interpolationFormat = await Send(Request("format", interpolation, 0));
    Check((string?)interpolationFormat["source"] is string interpolated && interpolated.Contains("\n  keep ${", StringComparison.Ordinal), "formatter preserves interpolation literal whitespace: " + interpolationFormat.ToJsonString());
    var badFormat = await Send(Request("format", "val broken = (\n", 0));
    Check(badFormat["source"] == null && (string?)badFormat["error"] is string, "invalid source formatting reports an error without replacement text");
    foreach (string name in new[] { "Direction", "Entities", "Items", "Grounds", "Unlocks", "Hats" })
    {
        string enumSource = "val value = " + name + "\n";
        var hoverEnum = await Send(Request("resolve", enumSource, enumSource.IndexOf(name, StringComparison.Ordinal) + name.Length));
        Check(((string?)hoverEnum["hover"]?["text"])?.Contains("enum " + name + ":", StringComparison.Ordinal) == true, "SDK enum declaration hover: " + name + " " + hoverEnum.ToJsonString());
        Check(((string?)hoverEnum["hover"]?["text"])?.Contains("case ", StringComparison.Ordinal) == true, "SDK enum hover includes its actual cases: " + name + " " + hoverEnum.ToJsonString());
        var enumMembers = await Send(Request("complete", name + ".", name.Length + 1));
        Check(enumMembers["completions"]!.AsArray().Any(n => (string?)n == "values") && enumMembers["completions"]!.AsArray().Any(n => (string?)n == "valueOf"), "normal Scala enum companion completion: " + name);
    }
    string docCode = "import scala.util.Try\nclass FancyProblem extends RuntimeException\n/** [[IllegalArgumentException]] [[FancyProblem]] [[Try]] [[List(1).ma]] */\ndef example(): Unit = ()\n";
    var docCompletion = await Send(Request("complete", docCode, docCode.IndexOf("List(1).ma", StringComparison.Ordinal) + 10));
    Check(docCompletion["completions"]!.AsArray().Any(n => (string?)n == "map"), "normal member completion inside Scaladoc reference: " + docCompletion.ToJsonString());
    var docSpans = await Send(Request("analyze", docCode, 0));
    foreach (string name in new[] { "IllegalArgumentException", "FancyProblem", "Try" })
    {
        int at = docCode.IndexOf("[[" + name, StringComparison.Ordinal) + 2;
        Check(docSpans["spans"]!.AsArray().Any(n => (int?)n?["start"] == at && (string?)n?["kind"] == "class"), "compiler class color in Scaladoc reference: " + name + " " + docSpans.ToJsonString());
    }
    var docDiagnostics = await Send(Request("diagnostics", docCode.Replace("List(1).ma", "List(1).map", StringComparison.Ordinal), 0));
    Check(docDiagnostics["diagnostics"]!.AsArray().Count == 0, "documentation projections do not introduce program diagnostics");
    string scopedDoc = "class Scoped(val items: List[Int]):\n  /** [[items.ma]] */\n  def run(): Unit = ()\n";
    var scopedCompletion = await Send(Request("complete", scopedDoc, scopedDoc.IndexOf("items.ma", StringComparison.Ordinal) + 8));
    Check(scopedCompletion["completions"]!.AsArray().Any(n => (string?)n == "map"), "reference completion preserves enclosing class scope");
    string builtinDoc = "/** [[harvest()]] */\ndef f(): Unit = ()\n";
    var builtinDocSpans = await Send(Request("analyze", builtinDoc, 0));
    Check(builtinDocSpans["spans"]!.AsArray().Any(n => (int?)n?["start"] == 6 && (string?)n?["kind"] == "builtin"), "documentation references preserve game builtin colors");
    var prefixDoc = await Send(Request("complete", "/** [[IllegalArg]] */\ndef f(): Unit = ()", 6 + 10));
    Check(prefixDoc["completions"]!.AsArray().Any(n => (string?)n == "IllegalArgumentException"), "exception class completion inside reference");
    string throwsDoc = "/** @throws IllegalArg */\ndef f(): Unit = ()";
    var throwsCompletion = await Send(Request("complete", throwsDoc, throwsDoc.IndexOf("IllegalArg", StringComparison.Ordinal) + 10));
    Check(throwsCompletion["completions"]!.AsArray().Any(n => (string?)n == "IllegalArgumentException"), "exception type completion after throws tag");
    string completeThrows = throwsDoc.Replace("IllegalArg", "IllegalArgumentException", StringComparison.Ordinal);
    var throwsColors = await Send(Request("analyze", completeThrows, 0));
    Check(throwsColors["spans"]!.AsArray().Any(n => (int?)n?["start"] == completeThrows.IndexOf("IllegalArgumentException", StringComparison.Ordinal) && (string?)n?["kind"] == "class"), "throws exception name receives compiler type color");
    Console.WriteLine($"Real Scala presentation compiler checks passed: {checks}");
}
finally
{
    process.StandardInput.Close();
    if (!process.WaitForExit(5000))
        process.Kill(true);
}
await ProviderChecks.Run(root, Check);
Console.WriteLine($"Scala analysis service/provider checks passed: {checks}");
