using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FarmerScala.Editor;

/// <summary>Display only. Never supplies navigation, hover, completion or execution data.</summary>
internal sealed class SemanticColorSnapshot
{
    public string Source
    {
        get;
    }
    public IReadOnlyList<SemanticSpan> Spans
    {
        get;
    }

    public SemanticColorSnapshot(string source, IReadOnlyList<SemanticSpan> spans)
    {
        Source = source;
        var tokens = ColorTokens(source).Where(t => t.Identifier).ToDictionary(t => t.Start);
        Spans = spans.Where(s => s != null && (s.Kind == "class" || s.Kind == "object" || s.Kind == "function" || s.Kind == "builtin" || s.Kind == "value") &&
            tokens.TryGetValue(s.Start, out var token) && token.Length == s.Length).Take(5000)
            .Select(s => new SemanticSpan { Start = s.Start, Length = s.Length, Kind = s.Kind }).ToArray();
    }

    public SemanticColorSnapshot Rebase(string next)
    {
        if (next == Source)
            return this;
        int prefix = 0;
        while (prefix < Source.Length && prefix < next.Length && Source[prefix] == next[prefix])
            prefix++;
        if (SplitsPair(Source, prefix) || SplitsPair(next, prefix))
            prefix--;
        int suffix = 0;
        while (suffix < Source.Length - prefix && suffix < next.Length - prefix &&
            Source[Source.Length - suffix - 1] == next[next.Length - suffix - 1])
            suffix++;
        int oldEnd = Source.Length - suffix, newEnd = next.Length - suffix;
        if (SplitsPair(Source, oldEnd) || SplitsPair(next, newEnd))
        {
            oldEnd++;
            newEnd++;
        }
        int delta = next.Length - Source.Length;
        var oldTokens = ColorTokens(Source);
        var newTokens = ColorTokens(next);
        // A new or changed declaration can shadow unchanged references. Let
        // the current local index recolor those names immediately.
        var oldBindings = Bindings(oldTokens);
        var newBindings = Bindings(newTokens);
        var changedBindings = new HashSet<string>(oldBindings.Keys.Concat(newBindings.Keys).Where(name =>
            !oldBindings.TryGetValue(name, out var before) || !newBindings.TryGetValue(name, out var after) || before != after), StringComparer.Ordinal);
        var oldIndex = oldTokens.Select((t, i) => (t.Start, i)).ToDictionary(p => p.Start, p => p.i);
        var newIndex = newTokens.Select((t, i) => (t.Start, i)).ToDictionary(p => p.Start, p => p.i);
        var retained = new List<SemanticSpan>();
        foreach (var span in Spans)
        {
            int at;
            if (span.Start + span.Length <= prefix)
                at = span.Start;
            else if (span.Start >= oldEnd)
                at = span.Start + delta;
            else
                continue;
            if (!oldIndex.TryGetValue(span.Start, out int before) || !newIndex.TryGetValue(at, out int after))
                continue;
            var token = newTokens[after];
            if (!token.Identifier || token.Length != span.Length || token.Text != oldTokens[before].Text || changedBindings.Contains(token.Text))
                continue;
            // An unchanged member name can refer to something else after its
            // receiver changes. Compare the expression prefix, ignoring trivia.
            if (Context(oldTokens, before) != Context(newTokens, after))
                continue;
            retained.Add(new SemanticSpan { Start = at, Length = span.Length, Kind = span.Kind });
        }
        return new SemanticColorSnapshot(next, retained);
    }

    private static Dictionary<string, string> Bindings(List<ScalaToken> tokens)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != ScalaTokenKind.Keyword ||
                !(token.Text == "class" || token.Text == "trait" || token.Text == "enum" || token.Text == "object" ||
                  token.Text == "type" || token.Text == "def" || token.Text == "val" || token.Text == "var"))
                continue;
            var name = tokens[i + 1];
            if (name.Identifier)
                bindings[name.Text] = (bindings.TryGetValue(name.Text, out var kinds) ? kinds + "," : "") + token.Text;
        }
        return bindings;
    }

    private static string Context(List<ScalaToken> tokens, int at)
    {
        int previous = at - 1;
        while (previous >= 0 && tokens[previous].Kind == ScalaTokenKind.Newline)
            previous--;
        if (previous < 0 || tokens[previous].Text != ".")
            return "";
        int start = previous - 1, depth = 0;
        while (start >= 0 && tokens[start].Kind == ScalaTokenKind.Newline)
            start--;
        for (; start >= 0; start--)
        {
            var token = tokens[start];
            if (token.Text == ")" || token.Text == "]")
                depth++;
            else if (token.Text == "(" || token.Text == "[")
            {
                if (depth == 0)
                    break;
                depth--;
            }
            if (depth == 0 && (token.Kind == ScalaTokenKind.Newline || token.Text == ";" || token.Text == "{" || token.Text == "}"))
                break;
        }
        return string.Join("\0", tokens.Skip(start + 1).Take(previous - start).Where(t => t.Kind != ScalaTokenKind.Newline).Select(t => t.Text));
    }

    private static bool SplitsPair(string text, int at) => at > 0 && at < text.Length &&
        char.IsHighSurrogate(text[at - 1]) && char.IsLowSurrogate(text[at]);

    private static List<ScalaToken> ColorTokens(string source)
    {
        var tokens = ScalaLexer.Lex(source);
        var result = tokens.Where(t => t.Kind != ScalaTokenKind.Comment).ToList();
        foreach (var comment in tokens.Where(t => t.Kind == ScalaTokenKind.Comment && t.Text.StartsWith("/**", StringComparison.Ordinal)))
        {
            for (int at = comment.Start + 3; at < comment.End; at++)
                if (source[at] == '[' && ScalaEditingContext.TryReference(source, Math.Min(at + 2, comment.End), out int from, out int end))
                {
                    foreach (var token in ScalaLexer.Lex(source.Substring(from, end - from)))
                        result.Add(new ScalaToken { Start = from + token.Start, Length = token.Length, Text = token.Text, Kind = token.Kind });
                    at = end;
                }
            foreach (Match match in Regex.Matches(comment.Text, @"@(?:throws|exception)[ \t]+([^\s*]*)"))
            {
                var type = match.Groups[1];
                if (type.Value.StartsWith("[", StringComparison.Ordinal))
                    continue;
                foreach (var token in ScalaLexer.Lex(type.Value))
                    result.Add(new ScalaToken { Start = comment.Start + type.Index + token.Start, Length = token.Length, Text = token.Text, Kind = token.Kind });
            }
        }
        return result.OrderBy(t => t.Start).ToList();
    }
}
