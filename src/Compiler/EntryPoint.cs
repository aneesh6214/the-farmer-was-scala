using FarmerScala.Editor;

namespace FarmerScala.Compiler;

internal static class EntryPoint
{
    internal sealed record Result(string Source, string Body, Diagnostic? Error, Diagnostic? Location = null);
    internal static Result Prepare(string file, string source)
    {
        var tokens = ScalaLexer.Lex(source).Where(t => t.Kind != ScalaTokenKind.Comment && t.Kind != ScalaTokenKind.Newline).ToList();
        var found = new List<(ScalaToken annotation, ScalaToken name, int end)>();
        int depth = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text == "{") depth++;
            if (tokens[i].Text == "}") depth--;
            if (tokens[i].Text != "@") continue;
            int end = i + 1;
            if (end + 2 < tokens.Count && tokens[end].Text == "scala" && tokens[end + 1].Text == ".") end += 2;
            if (end >= tokens.Count || tokens[end].Text != "main") continue;
            var at = tokens[i];
            Result Error(string text) => new(source, "", new(file, at.Line + 1, at.Column + 1, at.Line + 1, at.Column + 6, text));
            int method = end + 1;
            while (method < tokens.Count && tokens[method].Text is "private" or "final" or "inline") method++;
            if (depth != 0 || method + 3 >= tokens.Count || tokens[method].Text != "def")
                return Error("Place @main on a file-level method with an empty parameter list and a Unit return type.");
            var name = tokens[method + 1];
            if (tokens[method + 2].Text != "(" || tokens[method + 3].Text != ")")
                return Error("@main methods must have an empty parameter list: @main def farm(): Unit = ...");
            int next = method + 4;
            if (next < tokens.Count && tokens[next].Text == "(") return Error("@main methods cannot have additional parameter lists.");
            found.Add((at, name, tokens[end].End));
        }
        if (found.Count > 1)
            return new(source, "", new(file, found[1].annotation.Line + 1, found[1].annotation.Column + 1,
                found[1].annotation.Line + 1, found[1].annotation.Column + 6, "Only one @main method is supported per file."));
        if (found.Count == 0) return new(source, "()", null);
        var entry = found[0];
        // Preserve all source offsets. The mod dispatches the selected method,
        // so Scala need not generate an additional command-line launcher class.
        var chars = source.ToCharArray();
        for (int at = entry.annotation.Start; at < entry.end; at++) if (chars[at] != '\n' && chars[at] != '\r') chars[at] = ' ';
        string body = "{ def requireUnit[A](value: A)(using A =:= Unit): Unit = (); requireUnit(this." + entry.name.Text + "()) }";
        return new(new string(chars), body, null, new(file, entry.annotation.Line + 1, entry.annotation.Column + 1, entry.annotation.Line + 1, entry.annotation.Column + 6, "The @main method must be a file-level method returning Unit."));
    }
}
