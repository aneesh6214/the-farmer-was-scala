using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FarmerScala.Editor;

internal sealed class ScalaEditingResult
{
    internal string Source = "";
    internal int Anchor, Focus;
}

/// <summary>Pure keystroke transformations; never runs or evaluates the player's code.</summary>
internal sealed class ScalaEditingSession
{
    private sealed class Pair
    {
        internal int Open, Close; internal char Left, Right;
    }
    private string previous = "";
    private readonly List<Pair> pairs = new List<Pair>();
    public ScalaEditingSession()
    {
    }
    internal void Reset(string source)
    {
        previous = source;
        pairs.Clear();
    }

    internal void Synchronize(string source)
    {
        if (source == previous)
            return;
        int prefix = 0, oldEnd = previous.Length, newEnd = source.Length;
        while (prefix < oldEnd && prefix < newEnd && previous[prefix] == source[prefix])
            prefix++;
        while (oldEnd > prefix && newEnd > prefix && previous[oldEnd - 1] == source[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }
        int delta = newEnd - oldEnd;
        for (int i = pairs.Count - 1; i >= 0; i--)
        {
            var pair = pairs[i];
            int Rebase(int at) => at < prefix ? at : at >= oldEnd ? at + delta : -1;
            int open = Rebase(pair.Open), close = Rebase(pair.Close);
            if (open < 0 || close < 0 || open >= source.Length || close >= source.Length ||
                source[open] != pair.Left || source[close] != pair.Right)
                pairs.RemoveAt(i);
            else
            {
                pair.Open = open;
                pair.Close = close;
            }
        }
        previous = source;
    }

    internal bool TryEdit(string source, int anchor, int focus, char typed, bool backspace, out ScalaEditingResult result)
    {
        Synchronize(source);
        anchor = Math.Max(0, Math.Min(source.Length, anchor));
        focus = Math.Max(0, Math.Min(source.Length, focus));
        int start = Math.Min(anchor, focus), end = Math.Max(anchor, focus);
        result = new ScalaEditingResult { Source = source, Anchor = anchor, Focus = focus };
        if (backspace)
        {
            var pair = pairs.FirstOrDefault(p => p.Open == start - 1 && p.Close == start);
            if (start != end || pair == null)
                return false;
            pairs.Remove(pair);
            Set(result, source.Remove(start - 1, 2), start - 1);
            Synchronize(result.Source);
            return true;
        }
        if (typed == '\r')
            typed = '\n';
        if (typed == '\n')
        {
            if (TryNewline(source, start, end, out string next, out int cursor))
            {
                Set(result, next, cursor);
                Synchronize(next);
                return true;
            }
            return false;
        }
        if (!ScalaEditingContext.IsCode(source, start) &&
            !(ScalaEditingContext.IsScaladoc(source, start) && (typed == '[' || typed == ']')))
            return false;
        var closing = pairs.FirstOrDefault(p => p.Close == start && p.Right == typed);
        if (start == end && closing != null)
        {
            pairs.Remove(closing);
            Set(result, source, start + 1);
            return true;
        }
        char right = typed == '(' ? ')' : typed == '[' ? ']' : typed == '{' ? '}' : '\0';
        if (right == '\0')
            return false;
        if (start == end && start < source.Length && !char.IsWhiteSpace(source[start]) && ")]},;".IndexOf(source[start]) < 0)
            return false;
        string selected = source.Substring(start, end - start);
        string output = source.Substring(0, start) + typed + selected + right + source.Substring(end);
        Synchronize(output);
        pairs.Add(new Pair { Open = start, Close = start + selected.Length + 1, Left = typed, Right = right });
        result.Source = output;
        result.Anchor = start + 1;
        result.Focus = start + 1 + selected.Length;
        return true;
    }

    private static void Set(ScalaEditingResult result, string source, int cursor)
    {
        result.Source = source;
        result.Anchor = result.Focus = cursor;
    }

    internal static ScalaEditingResult ToggleLineComment(string source, int anchor, int focus)
    {
        anchor = Math.Max(0, Math.Min(source.Length, anchor));
        focus = Math.Max(0, Math.Min(source.Length, focus));
        int first = Math.Min(anchor, focus), last = Math.Max(anchor, focus);
        int lineStart = first == 0 ? 0 : source.LastIndexOf('\n', first - 1) + 1;
        int finalPosition = last > first ? last - 1 : last;
        var lines = new List<int>();
        for (int at = lineStart; at <= finalPosition;)
        {
            lines.Add(at);
            int newline = source.IndexOf('\n', at);
            if (newline < 0 || newline + 1 > finalPosition)
                break;
            at = newline + 1;
        }
        var targets = lines.Select(start =>
        {
            int at = start;
            while (at < source.Length && (source[at] == ' ' || source[at] == '\t'))
                at++;
            return at;
        }).ToList();
        bool anyContent = targets.Any(at => at < source.Length && source[at] != '\r' && source[at] != '\n');
        if (anyContent)
            targets = targets.Where(at => at < source.Length && source[at] != '\r' && source[at] != '\n').ToList();
        bool remove = targets.Count > 0 && targets.All(at => at + 1 < source.Length && source[at] == '/' && source[at + 1] == '/');
        var edits = targets.Select(at => (At: at, Remove: remove ? (at + 2 < source.Length && source[at + 2] == ' ' ? 3 : 2) : 0, Add: remove ? "" : "// ")).ToList();
        var output = new StringBuilder(source);
        foreach (var edit in edits.AsEnumerable().Reverse())
        {
            output.Remove(edit.At, edit.Remove);
            output.Insert(edit.At, edit.Add);
        }
        int Map(int value)
        {
            int delta = 0;
            foreach (var edit in edits)
            {
                if (value < edit.At)
                    break;
                if (value <= edit.At + edit.Remove)
                    return edit.At + delta + edit.Add.Length;
                delta += edit.Add.Length - edit.Remove;
            }
            return value + delta;
        }
        return new ScalaEditingResult { Source = output.ToString(), Anchor = Map(anchor), Focus = Map(focus) };
    }

    private static bool TryNewline(string source, int start, int end, out string next, out int cursor)
    {
        int lineStart = source.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        if (start == 0)
            lineStart = 0;
        string before = source.Substring(lineStart, start - lineStart);
        string indent = new string(before.TakeWhile(c => c == ' ' || c == '\t').ToArray());
        string trimmed = before.TrimEnd();
        string newline = source.Contains("\r\n") ? "\r\n" : "\n";
        string tail = source.Substring(end);
        string insertion;
        int caret;
        var preceding = ScalaLexer.Lex(source.Substring(0, start)).LastOrDefault(t => t.Kind != ScalaTokenKind.Newline);
        bool inlineComment = preceding != null && preceding.Kind == ScalaTokenKind.Comment &&
            preceding.Text.StartsWith("//", StringComparison.Ordinal) && preceding.Start >= lineStart &&
            source.Substring(lineStart, preceding.Start - lineStart).Trim().Length > 0;
        int opening = lineStart + trimmed.Length - 3;
        if (trimmed.EndsWith("/**", StringComparison.Ordinal) && ScalaEditingContext.IsCode(source, opening))
        {
            var comment = ScalaLexer.Lex(source).FirstOrDefault(t => t.Start == opening && t.Kind == ScalaTokenKind.Comment);
            string body = "";
            if (comment != null && comment.Text.EndsWith("*/", StringComparison.Ordinal) && comment.End - 2 >= end)
            {
                // Expand the existing comment as one edit, rather than placing
                // a new closer before its description and leaving the old one.
                body = source.Substring(end, comment.End - 2 - end);
                tail = source.Substring(comment.End);
            }
            var lines = ScaladocLines(body);
            foreach (string tag in ScaladocTags(tail))
                if (!lines.Any(line => HasScaladocTag(line, tag)))
                    lines.Add(tag);
            insertion = newline + string.Concat(lines.Select(line => indent + " * " + line + newline)) + indent + " */";
            next = source.Substring(0, opening + 3) + insertion + tail;
            cursor = opening + 3 + newline.Length + indent.Length + 3;
            return true;
        }
        else if (!ScalaEditingContext.IsCode(source, start) && !inlineComment)
        {
            // Only block/Scaladoc comment continuation. Strings and // comments
            // get an ordinary newline with the current line's leading whitespace.
            bool comment = IsOpenBlockComment(source, start);
            insertion = newline + indent + (comment && before.TrimStart().StartsWith("*", StringComparison.Ordinal) &&
                !before.TrimEnd().EndsWith("*/", StringComparison.Ordinal) ? "* " : "");
            caret = start + insertion.Length;
        }
        else
        {
            int closerAt = 0;
            while (closerAt < tail.Length && (tail[closerAt] == ' ' || tail[closerAt] == '\t'))
                closerAt++;
            if (closerAt < tail.Length && ")]}".IndexOf(tail[closerAt]) >= 0)
            {
                var stack = new Stack<ScalaToken>();
                foreach (var token in ScalaLexer.Lex(source.Substring(0, start)))
                {
                    if (token.Kind == ScalaTokenKind.Comment || token.Kind == ScalaTokenKind.String)
                        continue;
                    if (token.Text == "(" || token.Text == "[" || token.Text == "{")
                        stack.Push(token);
                    else if (token.Text == ")" || token.Text == "]" || token.Text == "}")
                    {
                        if (stack.Count > 0)
                            stack.Pop();
                    }
                }
                if (stack.Count > 0 && (stack.Peek().Text == "(" && tail[closerAt] == ')' || stack.Peek().Text == "[" && tail[closerAt] == ']' || stack.Peek().Text == "{" && tail[closerAt] == '}'))
                {
                    int open = stack.Peek().Start;
                    int openingLine = open == 0 ? 0 : source.LastIndexOf('\n', open - 1) + 1;
                    string openingIndent = new string(source.Substring(openingLine, open - openingLine).TakeWhile(c => c == ' ' || c == '\t').ToArray());
                    // Expanding an empty pair still creates an inner blank line.
                    if (ScalaLexer.Lex(source.Substring(open + 1, start - open - 1)).Any(token => token.Kind != ScalaTokenKind.Comment && token.Kind != ScalaTokenKind.Newline))
                    {
                        insertion = newline + openingIndent;
                        next = source.Substring(0, start) + insertion + tail.Substring(closerAt);
                        cursor = start + insertion.Length;
                        return true;
                    }
                }
            }
            if (inlineComment)
                trimmed = source.Substring(lineStart, preceding!.Start - lineStart).TrimEnd();
            var lastToken = ScalaLexer.Lex(trimmed).LastOrDefault(t => t.Kind != ScalaTokenKind.Comment && t.Kind != ScalaTokenKind.Newline);
            char last = lastToken != null && lastToken.Text.Length == 1 ? lastToken.Text[0] : '\0';
            char matching = last == '{' ? '}' : last == '(' ? ')' : last == '[' ? ']' : '\0';
            bool more = matching != '\0' || IndentsAfter(trimmed);
            string inner = indent + (more ? ScalaIndentation.Spaces : "");
            insertion = newline + inner;
            caret = start + insertion.Length;
            int whitespace = 0;
            while (whitespace < tail.Length && (tail[whitespace] == ' ' || tail[whitespace] == '\t'))
                whitespace++;
            if (matching != '\0' && whitespace < tail.Length && tail[whitespace] == matching)
            {
                insertion += newline + indent;
                tail = tail.Substring(whitespace);
            }
        }
        next = source.Substring(0, start) + insertion + tail;
        cursor = caret;
        return true;
    }

    private static List<string> ScaladocLines(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n').Select((line, index) =>
        {
            string text = line.TrimStart(' ', '\t');
            if (index > 0 && text.StartsWith("*", StringComparison.Ordinal) &&
                (text.Length == 1 || text[1] == ' ' || text[1] == '\t'))
                text = text.Length == 1 ? "" : text.Substring(2);
            return text.TrimEnd(' ', '\t', '\r');
        }).ToList();
        while (lines.Count > 1 && lines[0].Length == 0)
            lines.RemoveAt(0);
        while (lines.Count > 1 && lines[lines.Count - 1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static bool HasScaladocTag(string line, string tag)
    {
        string existing = line.TrimStart();
        string prefix = tag.TrimEnd();
        return existing == prefix || existing.StartsWith(prefix + " ", StringComparison.Ordinal) ||
            existing.StartsWith(prefix + "\t", StringComparison.Ordinal);
    }

    private static bool IsOpenBlockComment(string source, int cursor)
    {
        // Lexer handles nested comments, complete comments and string delimiters.
        var token = ScalaLexer.Lex(source.Substring(0, cursor)).LastOrDefault(t => t.Kind != ScalaTokenKind.Newline);
        return token != null && token.Kind == ScalaTokenKind.Comment && token.Text.StartsWith("/*", StringComparison.Ordinal) &&
            !token.Text.EndsWith("*/", StringComparison.Ordinal);
    }

    private static bool IndentsAfter(string text)
    {
        var tokens = ScalaLexer.Lex(text).Where(t => t.Kind != ScalaTokenKind.Comment && t.Kind != ScalaTokenKind.Newline).ToArray();
        if (tokens.Length == 0)
            return false;
        string last = tokens[tokens.Length - 1].Text;
        return new[] { "=", "=>", ":", "then", "do", "match", "else", "try", "catch", "finally", "yield" }.Contains(last);
    }

    private static List<string> ScaladocTags(string following)
    {
        var result = new List<string>();
        string bounded = following.Length > 12000 ? following.Substring(0, 12000) : following;
        var tokens = ScalaLexer.Lex(bounded).Where(t => t.Kind != ScalaTokenKind.Comment && t.Kind != ScalaTokenKind.Newline).ToList();
        int declaration = -1;
        var modifiers = new HashSet<string> { "private", "protected", "final", "sealed", "abstract", "override", "inline", "transparent", "open", "case", "lazy" };
        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i].Text;
            if (t == "def" || t == "class" || t == "trait" || t == "enum")
            {
                declaration = i;
                break;
            }
            if (modifiers.Contains(t))
                continue;
            // Allow an annotation preceding the declaration, including its argument list.
            if (t == "@")
            {
                i++;
                if (i < tokens.Count && tokens[i].Text == "throws" && i + 1 < tokens.Count && tokens[i + 1].Text == "[")
                {
                    int close = GroupEnd(tokens, i + 1);
                    if (close < tokens.Count && close > i + 2)
                        result.Add("@throws " + string.Concat(tokens.Skip(i + 2).Take(close - i - 2).Select(token => token.Text)) + " ");
                    i = close;
                }
                if (i + 1 < tokens.Count && tokens[i + 1].Text == "(")
                    i = GroupEnd(tokens, i + 1);
                continue;
            }
            return result;
        }
        if (declaration < 0 || declaration + 1 >= tokens.Count || !tokens[declaration + 1].Identifier)
            return result;
        int at = declaration + 2;
        if (at < tokens.Count && tokens[at].Text == "[")
        {
            int close = GroupEnd(tokens, at);
            foreach (string name in ParameterNames(tokens, at + 1, close, type: true))
                result.Add("@tparam " + name + " ");
            at = close + 1;
        }
        while (at < tokens.Count && tokens[at].Text == "(")
        {
            int close = GroupEnd(tokens, at);
            foreach (string name in ParameterNames(tokens, at + 1, close, type: false))
                result.Add("@param " + name + " ");
            at = close + 1;
        }
        if (tokens[declaration].Text == "def" && at + 1 < tokens.Count && tokens[at].Text == ":")
        {
            string returnName = tokens[at + 1].Text;
            int p = at + 2;
            for (; p + 1 < tokens.Count && tokens[p].Text == "." && tokens[p + 1].Identifier; p += 2)
                returnName = tokens[p + 1].Text;
            bool composed = p < tokens.Count && (tokens[p].Text == "=>" || tokens[p].Text == "|" || tokens[p].Text == "&");
            if (composed || returnName != "Unit" && returnName != "Nothing")
                result.Add("@return ");
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private static int GroupEnd(List<ScalaToken> tokens, int start)
    {
        string opening = tokens[start].Text, closing = opening == "(" ? ")" : opening == "[" ? "]" : "}";
        int depth = 1;
        for (int i = start + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Text == opening)
                depth++;
            else if (tokens[i].Text == closing && --depth == 0)
                return i;
        }
        return tokens.Count;
    }
    private static IEnumerable<string> ParameterNames(List<ScalaToken> tokens, int start, int end, bool type)
    {
        bool beginning = true;
        bool contextual = start < tokens.Count && (tokens[start].Text == "using" || tokens[start].Text == "implicit");
        for (int i = start; i < end && i < tokens.Count; i++)
        {
            string text = tokens[i].Text;
            if (text == "(" || text == "[" || text == "{")
            {
                i = GroupEnd(tokens, i);
                continue;
            }
            if (text == ",")
            {
                beginning = true;
                continue;
            }
            if (!beginning || text == "using" || text == "implicit" || text == "val" || text == "var" || text == "+" || text == "-")
                continue;
            if (tokens[i].Kind == ScalaTokenKind.Identifier && (type || i + 1 < end &&
                (tokens[i + 1].Text == ":" || !contextual && tokens[i + 1].Text == ",")))
            {
                yield return text;
                beginning = false;
            }
        }
    }

    internal static ScalaEditingResult Indent(string source, int anchor, int focus, bool unindent)
    {
        int first = System.Math.Min(anchor, focus), last = System.Math.Max(anchor, focus);
        if (first == last && !unindent)
            return new ScalaEditingResult { Source = source.Insert(first, ScalaIndentation.Spaces), Anchor = first + ScalaIndentation.Size, Focus = first + ScalaIndentation.Size };
        var edits = new System.Collections.Generic.List<(int At, int Remove, string Add)>();
        int line = first == 0 ? 0 : source.LastIndexOf('\n', first - 1) + 1;
        int limit = last > first && last > 0 && source[last - 1] == '\n' ? last - 1 : last;
        while (line <= limit)
        {
            int remove = 0;
            if (unindent)
            {
                if (line < source.Length && source[line] == '\t')
                    remove = 1;
                else
                    while (remove < ScalaIndentation.Size && line + remove < source.Length && source[line + remove] == ' ')
                        remove++;
            }
            edits.Add((line, remove, unindent ? "" : ScalaIndentation.Spaces));
            int next = source.IndexOf('\n', line);
            if (next < 0) break;
            line = next + 1;
        }
        int Map(int at)
        {
            int delta = 0;
            foreach (var edit in edits)
            {
                if (at < edit.At) break;
                if (at < edit.At + edit.Remove) return edit.At + delta + edit.Add.Length;
                delta += edit.Add.Length - edit.Remove;
            }
            return at + delta;
        }
        var output = new System.Text.StringBuilder(source);
        for (int n = edits.Count - 1; n >= 0; n--)
            output.Remove(edits[n].At, edits[n].Remove).Insert(edits[n].At, edits[n].Add);
        return new ScalaEditingResult { Source = output.ToString(), Anchor = Map(anchor), Focus = Map(focus) };
    }
}
