using FarmerScala.Editor;

internal static class NamedArgumentChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (string source in new[] {
            "plantOrHarvest(crop = variable)", "farm.plant(crop = variable)",
            "plant[Entities](crop = variable)", "new Plan(crop = variable)",
            "plant(1)(crop = variable)", "plant(first = 1, crop = variable)",
            "plant(first = inner(seed = 2), crop = variable)", "plant(\n  // label\n  crop = variable)",
            "plant(crop =", "plant(`crop name` = variable)",
            "s\"${plant(crop = variable)}\""
        })
        {
            var tokens = ScalaLexer.Lex(source);
            var labels = ScalaNamedArguments.Starts(tokens);
            var crop = tokens.First(t => t.Text == "crop" || t.Text == "`crop name`");
            check(labels.Contains(crop.Start), "named argument is immediate and context-aware: " + source);
        }
        foreach (string source in new[] {
            "val crop = variable", "crop = variable", "(crop = variable)",
            "if (crop = variable) ()", "while (crop = variable) ()", "if (true) (crop = variable)",
            "plant({ crop = variable })", "plant((crop = variable))", "plant(obj.crop = variable)",
            "plant(crop == variable)", "def plant(crop: Entities = variable): Unit = ()",
            "class Plan(crop: Entities = variable)", "def plant[A](crop: A = variable): Unit = ()",
            "\"plant(crop = variable)\"", "// plant(crop = variable)"
        })
            check(ScalaNamedArguments.Starts(ScalaLexer.Lex(source)).Count == 0, "ordinary source is not mistaken for named arguments: " + source);
        var palette = new ScalaSyntaxPalette { FunctionCall = "C", Builtin = "B" };
        string call = "plantOrHarvest(crop = variable)";
        var spans = new[] { new SemanticSpan { Start = 0, Length = 14, Kind = "function" },
            new SemanticSpan { Start = 15, Length = 4, Kind = "parameter" },
            new SemanticSpan { Start = 22, Length = 8, Kind = "value" } };
        string painted = ScalaSyntaxHighlighter.Colorize(call, palette, semanticSpans: spans);
        check(painted.Contains("<color=C>plantOrHarvest</color>", StringComparison.Ordinal), "named argument call retains its function-reference role");
        check(painted.Contains("<color=#56C1D6>crop</color> = variable", StringComparison.Ordinal), "only named label is blue; equals and argument value retain foreground");
        check(palette.NamedArgument != palette.Class && palette.NamedArgument == "#56C1D6", "named argument cyan is distinct from ordinary type foreground");
        string reference = ScalaSyntaxHighlighter.Colorize("/** [[plant(crop = variable)]] */", palette);
        check(reference.Contains("<color=#56C1D6>crop</color>", StringComparison.Ordinal), "Scaladoc reference code uses the same named argument rule");
    }
}
