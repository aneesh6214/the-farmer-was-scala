using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FarmerScala.Editor;

/// <summary>Small tolerant context scanner for editor keystrokes, including code inside s"${...}".</summary>
internal static class ScalaEditingContext
{
    private enum Mode
    {
        Code, LineComment, BlockComment, String, TripleString, Character, Backtick
    }
    private sealed class Frame
    {
        public Mode Mode; public int Depth; public bool Interpolated, Documentation; public int Braces;
    }

    internal static bool AllowsCompletion(string source, int cursor) => IsCode(source, cursor) || IsDocumentationTag(source, cursor) ||
        TryReference(source, cursor, out int from, out _) && IsCode(source.Substring(from, Math.Max(0, cursor - from)), cursor - from);

    internal static bool IsDocumentationTag(string source, int cursor)
    {
        if (!IsScaladoc(source, cursor))
            return false;
        int at = Math.Min(cursor, source.Length);
        while (at > 0 && char.IsLetter(source[at - 1]))
            at--;
        return at > 0 && source[at - 1] == '@';
    }
    internal static bool TryReference(string source, int cursor, out int from, out int to)
    {
        from = to = 0;
        foreach (var token in ScalaLexer.Lex(source))
        {
            if (token.Kind != ScalaTokenKind.Comment || !token.Text.StartsWith("/**", StringComparison.Ordinal))
                continue;
            int limit = token.End - (token.Text.EndsWith("*/", StringComparison.Ordinal) ? 2 : 0);
            for (int at = token.Start + 3; at < limit; at++)
            {
                if (source[at] != '[')
                    continue;
                int depth = 1, close = at + 1;
                for (; close < limit; close++)
                {
                    if (source[close] == '[')
                        depth++;
                    else if (source[close] == ']' && --depth == 0)
                        break;
                }
                int begin = at + 1, end = close;
                if (begin < limit && source[begin] == '[')
                {
                    begin++;
                    if (close < limit && source[close - 1] == ']')
                        end--;
                }
                if (cursor >= begin && cursor <= end)
                {
                    from = begin;
                    to = end;
                    return true;
                }
                at = close;
            }
            foreach (Match match in Regex.Matches(token.Text, @"@(?:throws|exception)[ \t]+([^\s*]*)"))
            {
                var type = match.Groups[1];
                if (type.Value.StartsWith("[", StringComparison.Ordinal))
                    continue;
                int begin = token.Start + type.Index, end = begin + type.Length;
                if (cursor >= begin && cursor <= end)
                {
                    from = begin;
                    to = end;
                    return true;
                }
            }
        }
        return false;
    }

    internal static bool IsCode(string source, int cursor) => Scan(source, cursor).Mode == Mode.Code;
    internal static bool IsScaladoc(string source, int cursor)
    {
        var frame = Scan(source, cursor);
        return frame.Mode == Mode.BlockComment && frame.Documentation;
    }

    private static Frame Scan(string source, int cursor)
    {
        cursor = Math.Max(0, Math.Min(source.Length, cursor));
        var stack = new List<Frame> { new Frame { Mode = Mode.Code } };
        for (int at = 0; at < cursor; at++)
        {
            Frame frame = stack[stack.Count - 1];
            char c = source[at], next = at + 1 < cursor ? source[at + 1] : '\0';
            switch (frame.Mode)
            {
                case Mode.LineComment:
                    if (c == '\r' || c == '\n')
                        stack.RemoveAt(stack.Count - 1);
                    break;
                case Mode.BlockComment:
                    if (c == '/' && next == '*')
                    {
                        frame.Depth++;
                        at++;
                    }
                    else if (c == '*' && next == '/')
                    {
                        if (--frame.Depth == 0)
                            stack.RemoveAt(stack.Count - 1);
                        at++;
                    }
                    break;
                case Mode.Character:
                    if (c == '\\')
                        at++;
                    else if (c == '\'' || c == '\n' || c == '\r')
                        stack.RemoveAt(stack.Count - 1);
                    break;
                case Mode.Backtick:
                    if (c == '`' || c == '\n' || c == '\r')
                        stack.RemoveAt(stack.Count - 1);
                    break;
                case Mode.String:
                case Mode.TripleString:
                    if (frame.Mode == Mode.String && c == '\\')
                    {
                        at++;
                        break;
                    }
                    if (frame.Interpolated && c == '$' && next == '$')
                    {
                        at++;
                        break;
                    }
                    if (frame.Interpolated && c == '$' && next == '{')
                    {
                        stack.Add(new Frame { Mode = Mode.Code, Braces = 1 });
                        at++;
                        break;
                    }
                    if (c == '"' && frame.Mode == Mode.String)
                        stack.RemoveAt(stack.Count - 1);
                    else if (c == '"' && next == '"' && at + 2 < cursor && source[at + 2] == '"')
                    {
                        stack.RemoveAt(stack.Count - 1);
                        at += 2;
                    }
                    else if (frame.Mode == Mode.String && (c == '\n' || c == '\r'))
                        stack.RemoveAt(stack.Count - 1);
                    break;
                default:
                    if (frame.Braces > 0 && c == '}')
                    {
                        if (--frame.Braces == 0)
                            stack.RemoveAt(stack.Count - 1);
                        break;
                    }
                    if (frame.Braces > 0 && c == '{')
                        frame.Braces++;
                    if (c == '/' && next == '/')
                    {
                        stack.Add(new Frame { Mode = Mode.LineComment });
                        at++;
                    }
                    else if (c == '/' && next == '*')
                    {
                        stack.Add(new Frame
                        {
                            Mode = Mode.BlockComment,
                            Depth = 1,
                            Documentation = at + 2 < cursor && source[at + 2] == '*'
                        });
                        at++;
                    }
                    else if (c == '`')
                        stack.Add(new Frame { Mode = Mode.Backtick });
                    else if (c == '\'' && next != '{' && next != '[')
                        stack.Add(new Frame { Mode = Mode.Character });
                    else if (c == '"')
                    {
                        bool triple = next == '"' && at + 2 < cursor && source[at + 2] == '"';
                        bool interpolated = at > 0 && (ScalaLexer.NamePart(source[at - 1]) || source[at - 1] == '`');
                        stack.Add(new Frame { Mode = triple ? Mode.TripleString : Mode.String, Interpolated = interpolated });
                        if (triple)
                            at += 2;
                    }
                    break;
            }
        }
        return stack[stack.Count - 1];
    }
}
