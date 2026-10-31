using System.Text.Json.Nodes;

internal static class FormatterChecks
{
    internal static async Task Run(Func<JsonObject, Task<JsonObject>> send, Action<bool, string> check)
    {
        async Task<string> Format(string source)
        {
            var result = await send(new JsonObject
            {
                ["op"] = "format", ["file"] = "draft",
                ["sources"] = new JsonObject { ["draft"] = source }
            });
            return (string?)result["source"] ?? throw new Exception("Formatting failed: " + result.ToJsonString());
        }
        const string description = "A crop's entity, preferred ground, and harvested item.";
        const string declaration = "case class Crop(entity: String)\n";
        string expected = "/**\n * " + description + "\n */\n";
        foreach (string comment in new[] { "/** " + description + " */\n", "/** " + description + "\n  */\n", expected })
        {
            string formatted = await Format(comment + declaration);
            check(formatted.Contains(expected, StringComparison.Ordinal), "formatter uses a separate Scaladoc opener and aligned asterisk: " + formatted);
            check(await Format(formatted) == formatted, "Scaladoc formatting is idempotent");
        }
        string nested = await Format("class Example {\n/** A nested method. */\ndef run(): Unit = ()\n}\n");
        check(nested.Contains("  /**\n   * A nested method.\n   */", StringComparison.Ordinal), "nested Scaladoc uses the same one-space asterisk alignment");
        string tags = await Format("/** Describes a value.\n * @param value keep this explanation\n * @return keep this result\n * @throws IllegalArgumentException keep this reason\n */\ndef value(value: Int): Int = value\n");
        check(tags.Contains("/**\n * Describes a value.", StringComparison.Ordinal) &&
            tags.Contains("keep this explanation", StringComparison.Ordinal) && tags.Contains("keep this result", StringComparison.Ordinal) && tags.Contains("keep this reason", StringComparison.Ordinal), "Scaladoc tags and descriptions survive formatting");
        const string literal = "val text = \"/** literal text */\"\n";
        check((await Format(literal)).Contains("\"/** literal text */\"", StringComparison.Ordinal), "comment delimiters inside strings are not rewritten");
    }
}
