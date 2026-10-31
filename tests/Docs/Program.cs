using System.Text.RegularExpressions;
using FarmerScala.Editor;

int checks = 0;
void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
var palette = new ScalaSyntaxPalette { Keyword = "K", String = "S", Comment = "C", Number = "N", Function = "F", Class = "T", Builtin = "B", Search = "M", SelectedSearch = "X" };
string Format(string value, bool project = true) => ScalaHoverFormatter.Format(value, palette, project);
string plain(string value) => Regex.Replace(value, "<[^>]*>", "");

string move = Format("def move(direction: Direction): Boolean\n\nMoves one tile.\n@param direction North, East, South, or West.\n@return `true` if movement succeeds.\n@note Crossing the edge wraps.\n@example move(North)", false);
Check(move.Contains("</size>\n\n", StringComparison.Ordinal) && !move.Contains("line-height=50%", StringComparison.Ordinal), "title reserves a full blank line before signature");
Check(move.Contains("<color=B>move</color>", StringComparison.Ordinal), "SDK function title and signature keep game color");
Check(Format("def print(values: Any*): Unit\n\nShows smoke.", false).Contains("<color=B>print</color>", StringComparison.Ordinal), "game print hover uses builtin color");
Check(Format("def print(): Unit\n\nUser function.", true).Contains("<color=F>print</color>", StringComparison.Ordinal), "user print hover retains user function color");
Check(move.Contains("<b>Parameters</b>\n", StringComparison.Ordinal) && plain(move).Contains("direction — North, East, South, or West.", StringComparison.Ordinal), "parameter tag becomes a readable named section");
Check(move.Contains("<b>Returns</b>", StringComparison.Ordinal) && !move.Contains("@return", StringComparison.Ordinal), "return tag becomes readable section");
Check(move.Contains("<color=K>true</color>", StringComparison.Ordinal), "inline return-value code receives syntax colors");
Check(move.Contains("<b>Example</b>", StringComparison.Ordinal) && move.Contains("<color=B>North</color>", StringComparison.Ordinal), "example uses common Scala syntax renderer");
Check(move.Contains("<b>Note</b>", StringComparison.Ordinal), "native action note is retained");
string generic = Format("def identity[A](value: A): A\n\nReturns its input.\n@tparam A the input type.\n@param value first line\n  continued description\n@return the same value\n@throws IllegalArgumentException if invalid");
Check(generic.Contains("<b>Type parameters</b>", StringComparison.Ordinal), "type parameters render separately");
Check(plain(generic).Contains("continued description", StringComparison.Ordinal), "multiline parameter prose survives");
Check(generic.Contains("<b>Throws</b>", StringComparison.Ordinal) && generic.Contains("IllegalArgumentException", StringComparison.Ordinal), "exceptions render readable named section");
string klass = Format("class Option[+A]\n\nAn optional value.\n@tparam A the value type.");
Check(klass.Contains("<color=T>Option</color>", StringComparison.Ordinal), "library and user class signatures share class coloring");
Check(Format("def move(): Unit", true).Contains("<color=F>move</color>", StringComparison.Ordinal), "user function named like game function remains user-colored");
Check(!Format("def run(): Unit\n\nRuns the program.").Contains("<b>Returns</b>", StringComparison.Ordinal), "Unit docs have no invented return section");
string block = Format("def sample(): Unit\n\nAn example:\n```scala\nval note = \"@param hidden\"\n// @return no tag\n```\n@param real actual parameter");
Check(block.Contains("<color=K>val</color>", StringComparison.Ordinal) && plain(block).Contains("@return no tag", StringComparison.Ordinal), "fenced examples retain code and do not parse comment tags");
Check(plain(block).Contains("real — actual parameter", StringComparison.Ordinal), "tags following fenced examples still render");
string scalaBlock = Format("def sample(): Unit\n\n{{{\nval n = 5\n}}}\n@return a number");
Check(scalaBlock.Contains("<color=N>5</color>", StringComparison.Ordinal), "Scaladoc triple-brace code example is colored");
string unsafeText = Format("def run(): Unit\n\n<size=999>bad</size> ![x](file:///x) {{output}}\n# title\n@param x <unlock=debug>secret</unlock>");
Check(!unsafeText.Contains("<size=999>", StringComparison.Ordinal) && !unsafeText.Contains("<unlock=", StringComparison.Ordinal), "player rich text and unlock directives stay inert");
Check(!unsafeText.Contains("](file:", StringComparison.Ordinal) && !unsafeText.Contains("{{output}}", StringComparison.Ordinal), "native image/link and custom section directives stay inert");
Check(unsafeText.Contains("\u200B# title", StringComparison.Ordinal), "player headings cannot alter tooltip layout");
Check(Format("") == "" && Format(" \n ") == "", "empty compiler hover is empty");
string longDoc = Format("def run(): Unit\n\n" + new string('x', 8170) + "😀" + new string('y', 100));
Check(!Regex.IsMatch(longDoc, @"[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]"), "bounded hover preserves UTF-16 pairs");
Check(longDoc.Length < 10000, "hover output remains bounded");
string reference = Format("class Double\n\nA subtype of [[scala.AnyVal]]. Converts [[scala.Double]] to [[scala.runtime.RichDouble]].");
Check(!reference.Contains("[[", StringComparison.Ordinal) && !reference.Contains("]]", StringComparison.Ordinal), "Scaladoc symbol references render without raw brackets");
Check(reference.Contains("scala.<color=T>AnyVal</color>", StringComparison.Ordinal) && reference.Contains("scala.<color=T>Double</color>", StringComparison.Ordinal), "qualified standard type references receive class syntax colors");
Check(reference.Contains("scala.runtime.RichDouble</font>", StringComparison.Ordinal), "unknown library references still use the code font");
string labeledReference = Format("def f(): Unit\n\nSee [[scala.Option[scala.Int] Option[Int]]] and [[scala.Option#map map[B]]].");
Check(!plain(labeledReference).Contains("scala.Option", StringComparison.Ordinal) && plain(labeledReference).Contains("Option[Int]", StringComparison.Ordinal) && plain(labeledReference).Contains("map[B]", StringComparison.Ordinal), "link labels and nested type brackets remain readable code");
Check(labeledReference.Contains("<color=T>Option</color>", StringComparison.Ordinal) && labeledReference.Contains("<color=T>Int</color>", StringComparison.Ordinal), "link labels use ordinary Scala type syntax colors");
string mixedMarkup = Format("def f(): Unit\n\nUse [[scala.Int]] with `Some(1)` and [[scala.Option Option]].");
Check(!mixedMarkup.Contains("[[", StringComparison.Ordinal) && mixedMarkup.Contains("<color=N>1</color>", StringComparison.Ordinal), "Scaladoc references and Markdown code can share a line");
string incompleteReference = Format("def f(): Unit\n\nIncomplete [[scala.Int then `val x = 1` and [[ ]] remain visible.");
Check(plain(incompleteReference).Contains("Incomplete [[scala.Int", StringComparison.Ordinal) && incompleteReference.Contains("<color=K>val</color>", StringComparison.Ordinal), "unfinished links stay literal without swallowing later code");
string unsafeReference = Format("def f(): Unit\n\n[[scala.Int <size=999>evil</size>]] [[scala.Int ![x](file:///x)]]");
Check(!unsafeReference.Contains("<size=999>", StringComparison.Ordinal) && !unsafeReference.Contains("](file:", StringComparison.Ordinal), "Scaladoc link labels cannot inject native markup");
string namedTypes = Format("def f(Option: Int, move: Int): Unit\n\n@param Option first\n@param move second");
Check(namedTypes.Contains("<font=\"FiraCode-Regular SDF\">Option</font> — first", StringComparison.Ordinal) && namedTypes.Contains("<font=\"FiraCode-Regular SDF\">move</font> — second", StringComparison.Ordinal), "parameter names use plain code foreground even when spelled like a type or builtin");

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string sdk = File.ReadAllText(Path.Combine(root, "sdk/game/api.scala"));
string publicApi = sdk.Substring(sdk.IndexOf("object api:", StringComparison.Ordinal));
publicApi = publicApi.Substring(0, publicApi.IndexOf("final class SimulationGlobals", StringComparison.Ordinal));
var methods = Regex.Matches(publicApi, @"(?m)^  def (?<name>\w+)(?<signature>[^\r\n]*)");
int documented = 0, parameters = 0;
foreach (Match method in methods)
{
    string before = publicApi.Substring(0, method.Index).TrimEnd();
    int open = before.LastIndexOf("/**", StringComparison.Ordinal);
    if (!before.EndsWith("*/", StringComparison.Ordinal) || open < 0)
        throw new Exception("Missing SDK Scaladoc: " + method.Groups["name"].Value);
    string docs = before.Substring(open);
    string signature = method.Groups["signature"].Value;
    int start = signature.IndexOf('('), depth = 0, end = start;
    if (start >= 0)
    {
        // Signatures such as simulate span several source lines.
        signature = publicApi.Substring(method.Groups["signature"].Index);
        for (int at = start; at < signature.Length; at++)
        {
            if (signature[at] == '(')
                depth++;
            else if (signature[at] == ')' && --depth == 0)
            {
                end = at;
                break;
            }
        }
        foreach (Match parameter in Regex.Matches(signature.Substring(start, end - start + 1), @"\b(?<name>\w+)\s*:"))
        {
            string name = parameter.Groups["name"].Value;
            if (!Regex.IsMatch(docs, @"@param\s+" + Regex.Escape(name) + @"\b"))
                throw new Exception("Undocumented parameter: " + method.Groups["name"].Value + "." + name);
            parameters++;
        }
    }
    documented++;
}
Check(documented == 43, "all 43 SDK entry-point signatures, including aliases and overloads, have Scaladoc");
Check(parameters >= 40, "SDK argument descriptions cover complete typed signatures");
Check(!sdk.Contains("Items.Value", StringComparison.Ordinal) && sdk.Contains("`Some(cost)`", StringComparison.Ordinal), "SDK hover keeps useful Option semantics and omits enum-migration implementation notes");
string sourceArchive = Path.Combine(root, ".local/analysis/sources/scala-library-3.9.0-sources.jar");
using (var archive = System.IO.Compression.ZipFile.OpenRead(sourceArchive))
using (var reader = new StreamReader(archive.GetEntry("scala/Double.scala")!.Open()))
{
    string librarySource = reader.ReadToEnd();
    int open = librarySource.IndexOf("/**", StringComparison.Ordinal), close = librarySource.IndexOf("*/", open, StringComparison.Ordinal);
    string description = string.Join("\n", librarySource.Substring(open + 3, close - open - 3).Split('\n').Select(line => line.Trim().TrimStart('*').TrimStart()));
    string realDouble = Format("final abstract class Double\n\n" + description);
    Check(!realDouble.Contains("[[", StringComparison.Ordinal) && realDouble.Contains("scala.<color=T>AnyVal</color>", StringComparison.Ordinal), "actual bundled Double Scaladoc renders symbol links as colored code");
}
Console.WriteLine($"Scala documentation checks passed: {checks} ({documented} SDK signatures, {parameters} parameter tags)");
