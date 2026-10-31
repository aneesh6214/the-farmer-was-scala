using FarmerScala.Editor;

internal static class ConventionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var palette = new ScalaSyntaxPalette { Keyword = "K", String = "S", Comment = "C",
            FunctionDeclaration = "D", FunctionCall = "C", Class = "T", TypeParameter = "P", Builtin = "B" };
        string source = "class Farm(val size: Int, label: String) {\n  val crop = 2\n  def inspect[A](arg: A): Int = { val inside = 1; crop + inside + size }\n}\nval rowToCrop: Map[Int, Int] = Map(0 -> 1)\ndef run(): Unit = {\n  List((0, 1)).foreach { tile => rowToCrop(tile._1) }\n}\nval farm = new Farm(2, \"x\")\nfarm.crop\nfarm.inspect(1)\n";
        var sources = new Dictionary<string, string> { ["main"] = source };
        var spans = new ScalaCompletionModel(sources).Spans("main");
        string Kind(int at) => spans.First(s => s.Start == at).Kind;
        check(Kind(source.IndexOf("size:", StringComparison.Ordinal)) == "field" && Kind(source.IndexOf("label:", StringComparison.Ordinal)) == "parameter", "local index distinguishes constructor fields from plain parameters");
        check(Kind(source.IndexOf("crop =", StringComparison.Ordinal)) == "field" && Kind(source.LastIndexOf("crop", StringComparison.Ordinal)) == "field", "class field declarations and references share member identity");
        check(Kind(source.IndexOf("inside =", StringComparison.Ordinal)) == "value", "method-local values are not fields");
        check(Kind(source.IndexOf("inspect[", StringComparison.Ordinal)) == "functionDeclaration" && Kind(source.LastIndexOf("inspect", StringComparison.Ordinal)) == "function", "local index distinguishes method declaration from invocation");
        int mapAt = source.LastIndexOf("rowToCrop", StringComparison.Ordinal);
        check(Kind(mapAt) == "value", "Map apply syntax retains the local/script value category before compiler analysis");
        string colored = ScalaSyntaxHighlighter.Colorize(source, palette, semanticSpans: spans);
        check(!colored.Contains("<color=C>rowToCrop", StringComparison.Ordinal) && !colored.Contains("<color=#C77DBB>rowToCrop", StringComparison.Ordinal), "callable script val is neither a declaration nor a class field");
        check(colored.Contains("<color=#C77DBB>crop</color>", StringComparison.Ordinal), "local class field rendering uses purple");
        check(colored.Contains("<color=D>inspect</color>", StringComparison.Ordinal), "function declaration keeps a distinct role from calls");
        check(colored.Contains("farm.<color=C>inspect</color>(", StringComparison.Ordinal), "method calls retain their reference role");
        string methodReference = "farm.inspect";
        check(ScalaSyntaxHighlighter.Colorize(methodReference, palette, semanticSpans: new[] { new SemanticSpan { Start = 5, Length = 7, Kind = "function" } }).Contains("<color=C>inspect</color>", StringComparison.Ordinal), "uncalled method references use the call/reference role");
        string libraryCall = "List(1).foreach";
        check(ScalaSyntaxHighlighter.Colorize(libraryCall, palette, semanticSpans: new[] { new SemanticSpan { Start = 8, Length = 7, Kind = "function" } }).Contains("<color=C>foreach</color>", StringComparison.Ordinal), "standard-library methods use the same category as custom methods");
        check(!ScalaSyntaxHighlighter.Colorize("unknown(1)", new ScalaSyntaxPalette { FunctionDeclaration = "D", FunctionCall = "C" }).Contains("<color=C>unknown", StringComparison.Ordinal), "parentheses alone never guess that a value is a function");
        var stale = new[] { new SemanticSpan { Start = mapAt, Length = 9, Kind = "function" } };
        check(SemanticSpanMerger.Merge(spans, stale, authoritative: false).First(s => s.Start == mapAt).Kind == "value", "current Map binding defeats stale callable classification");
        var oldField = new[] { new SemanticSpan { Start = mapAt, Length = 9, Kind = "field" } };
        check(SemanticSpanMerger.Merge(spans, oldField, authoritative: false).First(s => s.Start == mapAt).Kind == "value", "moving a binding out of a class cannot retain old field coloring");
        var field = new[] { new SemanticSpan { Start = source.IndexOf("crop =", StringComparison.Ordinal), Length = 4, Kind = "field" } };
        check(new SemanticColorSnapshot(source, field).Rebase(source + "\n").Spans.Single().Kind == "field", "field color survives unrelated edits");
        foreach (string role in new[] { "parameter", "typeParameter", "functionDeclaration", "namespace" })
        {
            var one = new SemanticColorSnapshot("symbol", new[] { new SemanticSpan { Start = 0, Length = 6, Kind = role } });
            check(one.Rebase("symbol\n").Spans.Single().Kind == role, "cached semantic category survives edits: " + role);
        }
        string tuple = "tile._1";
        var tupleSpans = new[] { new SemanticSpan { Start = 0, Length = 4, Kind = "parameter" }, new SemanticSpan { Start = 5, Length = 2, Kind = "field" } };
        check(ScalaSyntaxHighlighter.Colorize(tuple, palette, semanticSpans: tupleSpans) == "tile.<color=#C77DBB>_1</color>", "tuple accessor is purple and its dot/parameter stay white");
        check(ScalaSyntaxHighlighter.Colorize("harvest()", palette).Contains("<color=B>harvest</color>", StringComparison.Ordinal), "game APIs keep their reserved game color");
        string helper = "val rowToCrop = Map(0 -> 1)\n";
        string caller = "import helper.rowToCrop\nrowToCrop(0)\n";
        var imported = new ScalaCompletionModel(new Dictionary<string, string> { ["helper"] = helper, ["main"] = caller });
        check(imported.Spans("main").Any(s => s.Start == caller.LastIndexOf("rowToCrop", StringComparison.Ordinal) && s.Kind == "value"), "imported callable value retains neutral category without the compiler");
    }
}
