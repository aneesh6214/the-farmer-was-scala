using FarmerScala.Editor;

// Tests the single shared palette, based on the documented IntelliJ Dark reference
// with directly adjusted keyword/string/documentation hues and native yellow calls.
// Independent expected values catch accidental changes to that agreed palette.
internal static class DarkPaletteChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var palette = ScalaSyntaxPalette.Create("GAME", "YELLOW", "SEARCH", "CURRENT");
        foreach (var pair in new[] {
            (palette.Foreground, "#BCBEC4", "ordinary foreground"),
            (palette.Class, "#BCBEC4", "class/type names"),
            (palette.Keyword, "#DF915E", "keywords"),
            (palette.String, "#79A978", "strings/characters"),
            (palette.Comment, "#7A7E85", "ordinary comments"),
            (palette.DocComment, "#445649", "Scaladoc prose"),
            (palette.Documentation, "#4E6B55", "Scaladoc tags/parameter labels"),
            (palette.DocMarkup, "#4E6B55", "Scaladoc markup"),
            (palette.Number, "#2AACB8", "numbers"),
            (palette.FunctionDeclaration, "#56A8F5", "method declarations"),
            (palette.FunctionCall, "YELLOW", "method references/calls"),
            (palette.Field, "#C77DBB", "fields"),
            (palette.TypeParameter, "#4E807D", "Scala inherited type parameters/aliases"),
            (palette.NamedArgument, "#56C1D6", "Scala Dark named arguments"),
            (palette.Interpolation, "#00B8BB", "Scala inherited interpolation injection"),
            (palette.Annotation, "#B3AE60", "annotation names"),
            (palette.Escape, "#DF915E", "string escapes")
        })
            check(pair.Item1 == pair.Item2, "shared palette color: " + pair.Item3);
        check(palette.Builtin == "GAME" && palette.Search == "SEARCH" && palette.SelectedSearch == "CURRENT", "game symbols and native search marks retain supplied game colors");

        string Paint(string source, IReadOnlyList<SemanticSpan>? spans = null) => ScalaSyntaxHighlighter.Colorize(source, palette, semanticSpans: spans);
        void At(string source, string needle, string expected, string name, IReadOnlyList<SemanticSpan>? spans = null)
        {
            int start = source.IndexOf(needle, StringComparison.Ordinal);
            if (start < 0) throw new Exception("Missing fixture text: " + needle);
            var colors = Colors(Paint(source, spans));
            check(Enumerable.Range(start, needle.Length).All(i => colors[i] == expected), name);
        }
        foreach (string number in new[] { "0", "42L", "0xFF", "0b1010", "1.25f", "6.02e23", ".5", "1_000" })
            At("val value = " + number, number, "#2AACB8", "numeric literal follows Dark: " + number);
        At("true && false", "true", "#DF915E", "boolean literals are keywords rather than numbers");
        At("true && false", "&&", "#BCBEC4", "operator symbols retain ordinary foreground");
        At("val text = \"hello\"", "hello", "#79A978", "string contents are green");
        At("val char = 'x'", "'x'", "#79A978", "character literal uses string color");
        At("val text = \"a\\nb\"", "\\n", "#DF915E", "normal string escape uses Dark escape color");
        At("val char = '\\t'", "\\t", "#DF915E", "character escape uses Dark escape color");
        At("val text = \"\\u0041\"", "\\u0041", "#DF915E", "entire Unicode escape shares escape color");
        At("val text = raw\"a\\nb\"", "\\n", "#79A978", "raw interpolator keeps backslashes string-colored");
        At("val text = \"\"\"a\\nb\"\"\"", "\\n", "#79A978", "triple-quoted string keeps backslashes literal");
        At("s\"value: $count\"", "$", "#00B8BB", "interpolation marker inherits Scala's reference color");
        At("s\"value: ${1 + 2}\"", "1", "#2AACB8", "interpolated expression uses ordinary numeric rules");
        At("s\"${1} newline \\n\"", "\\n", "#DF915E", "escape coloring resumes after an interpolation");
        At("s\"${\"\\t\"} newline \\n\"", "\\n", "#DF915E", "nested strings do not replace outer interpolation context");
        At("s\"${raw\"${1} raw \\t\"} normal \\n\"", "\\t", "#79A978", "nested raw interpolation preserves its own literal style");
        At("s\"${raw\"${1} raw \\t\"} normal \\n\"", "\\n", "#DF915E", "outer escape style resumes after nested raw interpolation closes");
        At("s\"$count\" + raw\"\\t\"", "\\t", "#79A978", "closing a simple interpolation cannot leak escape style to the next raw string");
        At("// plain comment", "plain", "#7A7E85", "line comment follows Dark");
        At("/* block comment */", "block", "#7A7E85", "block comment follows Dark");
        At("/** documentation */", "documentation", "#445649", "Scaladoc has its own muted green");
        At("/** @param value description */", "@param", "#4E6B55", "Scaladoc tag is brighter than prose");
        At("/** @param value description */", "value", "#4E6B55", "Scaladoc parameter name follows upstream tag category");
        At("/** @throws IllegalArgumentException description */", "@throws", "#4E6B55", "throws uses the shared tag color");
        At("/** [[Int]] */", "[[", "#4E6B55", "reference brackets follow upstream markup color");
        At("/** [[Int]] */", "Int", "#BCBEC4", "reference types use the same neutral color as code types");
        At("/** [[Some(count = 2)]] */", "count", "#56C1D6", "Scaladoc named argument shares regular code color");
        At("@deprecated(\"reason\", \"1\") def old(): Unit = ()", "deprecated", "#B3AE60", "annotation name follows metadata category");
        At("@scala.annotation.tailrec def loop(): Unit = ()", "tailrec", "#B3AE60", "qualified annotation name is metadata-colored");
        At("val count: Int = 1", "Int", "#BCBEC4", "primitive type is neutral rather than custom cyan");
        At("plant(crop = value)", "crop", "#56C1D6", "named argument uses Scala Dark override");
        At("harvest()", "harvest", "GAME", "native game function retains its unique green override");
        At("Entities.Grass", "Grass", "GAME", "resolved game enum case keeps game color", new[] {
            new SemanticSpan { Start = 0, Length = 8, Kind = "builtin" },
            new SemanticSpan { Start = 9, Length = 5, Kind = "builtin" }
        });
        At("def harvest(): Int = 1", "harvest", "#56A8F5", "user declaration shadowing game name is blue", new[] {
            new SemanticSpan { Start = 4, Length = 7, Kind = "functionDeclaration" }
        });
        string source = "type Coordinate = (Int, Int)\nclass Farm(val size: Int) {\n def inspect[A](arg: A): Int = { val local = 1; size + local }\n}\nval farm = new Farm(2)\nval point: Coordinate = (0, 1)\nfarm.inspect(1)\n";
        var model = new ScalaCompletionModel(new Dictionary<string, string> { ["main"] = source });
        var spans = model.Spans("main");
        At(source, "inspect", "#56A8F5", "local method declaration is blue immediately", spans);
        At(source, "size", "#C77DBB", "field name is purple", spans.Where(s => s.Start == source.IndexOf("size:", StringComparison.Ordinal)).ToArray());
        // Only inspect the actual identifier, never the punctuation following it.
        At(source, "Coordinate", "#4E807D", "type alias declaration uses the inherited Scala alias color", spans);
        int aliasAt = source.LastIndexOf("Coordinate", StringComparison.Ordinal);
        check(spans.Any(s => s.Start == aliasAt && s.Kind == "typeAlias"), "alias references retain identity in the local model");
        check(new SemanticColorSnapshot(source, spans).Rebase(source + "\n").Spans.Any(s => s.Start == aliasAt && s.Kind == "typeAlias"), "alias color survives unrelated typing");
        check(Colors(Paint(source, spans))[source.LastIndexOf("inspect", StringComparison.Ordinal)] == "YELLOW", "method calls use native yellow while declarations remain blue");
        check(Colors(Paint(source, spans))[source.IndexOf("local =", StringComparison.Ordinal)] == "#BCBEC4", "local value is neutral despite its Int type");

        At("farm.inspect", "inspect", "YELLOW", "uncalled method references restore native yellow", new[] {
            new SemanticSpan { Start = 5, Length = 7, Kind = "function" }
        });
        At("List(1).foreach", "foreach", "YELLOW", "standard-library calls share the restored yellow", new[] {
            new SemanticSpan { Start = 8, Length = 7, Kind = "function" }
        });
        At("harvest()", "harvest", "GAME", "game functions keep native green rather than function yellow");
        check(palette.Escape == palette.Keyword, "every use of the keyword color shares the adjusted hue");
    }

    private static List<string> Colors(string rich)
    {
        var colors = new List<string>();
        var stack = new Stack<string>();
        for (int i = 0; i < rich.Length;)
        {
            if (rich.AsSpan(i).StartsWith("<color=", StringComparison.Ordinal))
            {
                int end = rich.IndexOf('>', i);
                stack.Push(rich.Substring(i + 7, end - i - 7));
                i = end + 1;
            }
            else if (rich.AsSpan(i).StartsWith("</color>", StringComparison.Ordinal))
            {
                stack.Pop();
                i += 8;
            }
            else
            {
                colors.Add(stack.Count > 0 ? stack.Peek() : "");
                i++;
            }
        }
        return colors;
    }
}
