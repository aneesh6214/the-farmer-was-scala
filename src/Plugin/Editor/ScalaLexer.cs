using System;
using System.Collections.Generic;

namespace FarmerScala.Editor;

internal enum ScalaTokenKind
{
    Identifier, Keyword, Operator, Number, String, Comment, Newline, Punctuation, Interpolation
}
internal sealed class ScalaToken
{
    public string Text = "";
    public int Start, Length, Line, Column;
    public int End => Start + Length;
    public ScalaTokenKind Kind;
    // Retain literal style when interpolation splits a string across tokens.
    public bool HasEscapes;
    public bool Identifier => Kind == ScalaTokenKind.Identifier || Kind == ScalaTokenKind.Operator;
}

/// <summary>Tolerant, display-only Scala 3 lexer. Offsets are raw UTF-16 source indices.</summary>
internal static class ScalaLexer
{
    internal static readonly HashSet<string> Keywords = new HashSet<string>((
        "abstract case catch class def do else end enum export extends false final finally for forSome given if implicit import " +
        "inline lazy match new null object opaque open override package private protected return sealed super then this throw " +
        "trait transparent true try type using val var while with yield as derives extension infix erased into").Split(' '), StringComparer.Ordinal);
    internal static bool NameStart(char c) => c == '_' || c == '$' || char.IsLetter(c) || c >= 128 && !char.IsPunctuation(c) && !char.IsSymbol(c);
    internal static bool NamePart(char c) => NameStart(c) || char.IsDigit(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark;
    internal static bool Operator(char c) => "!#%&*+-/:<=>?@\\^|~".IndexOf(c) >= 0 || c >= 128 && char.IsSymbol(c);

    public static List<ScalaToken> Lex(string source)
    {
        var result = new List<ScalaToken>();
        int at = 0, line = 0, column = 0;
        while (at < source.Length)
        {
            int start = at, startLine = line, startColumn = column;
            char c = source[at];
            if (c == '\r' || c == '\n')
            {
                at++;
                if (c == '\r' && at < source.Length && source[at] == '\n')
                    at++;
                result.Add(new ScalaToken { Text = "\n", Start = start, Length = at - start, Line = line, Column = column, Kind = ScalaTokenKind.Newline });
                line++;
                column = 0;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                at++;
                column += c == '\t' ? ScalaIndentation.Size : 1;
                continue;
            }
            ScalaTokenKind kind;
            if (c == '/' && at + 1 < source.Length && source[at + 1] == '/')
            {
                at += 2;
                while (at < source.Length && source[at] != '\n' && source[at] != '\r')
                    at++;
                kind = ScalaTokenKind.Comment;
            }
            else if (c == '/' && at + 1 < source.Length && source[at + 1] == '*')
            {
                at += 2;
                int nesting = 1;
                while (at < source.Length && nesting != 0)
                {
                    if (at + 1 < source.Length && source[at] == '/' && source[at + 1] == '*')
                    {
                        nesting++;
                        at += 2;
                    }
                    else if (at + 1 < source.Length && source[at] == '*' && source[at + 1] == '/')
                    {
                        nesting--;
                        at += 2;
                    }
                    else
                        at++;
                }
                kind = ScalaTokenKind.Comment;
            }
            else if (c == '"' || c == '\'')
            {
                if (c == '\'' && at + 1 < source.Length && (source[at + 1] == '{' || source[at + 1] == '['))
                {
                    at++;
                    kind = ScalaTokenKind.Punctuation;
                }
                else
                {
                    at = ReadString(source, at, c);
                    kind = ScalaTokenKind.String;
                }
            }
            else if (c == '`')
            {
                at++;
                while (at < source.Length && source[at] != '`' && source[at] != '\r' && source[at] != '\n')
                    at++;
                if (at < source.Length && source[at] == '`')
                    at++;
                kind = ScalaTokenKind.Identifier;
            }
            else if (NameStart(c))
            {
                at++;
                while (at < source.Length && NamePart(source[at]))
                    at++;
                if (at < source.Length && source[at] == '"')
                {
                    at = ReadInterpolated(source, start, at, result);
                    for (int i = start; i < at; i++)
                        if (source[i] == '\n')
                        {
                            line++;
                            column = 0;
                        }
                        else if (source[i] != '\r')
                            column += source[i] == '\t' ? ScalaIndentation.Size : 1;
                    continue;
                }
                else
                    kind = Keywords.Contains(source.Substring(start, at - start)) ? ScalaTokenKind.Keyword : ScalaTokenKind.Identifier;
            }
            else if (char.IsDigit(c) || c == '.' && at + 1 < source.Length && char.IsDigit(source[at + 1]))
            {
                at++;
                while (at < source.Length)
                {
                    char next = source[at];
                    if (char.IsLetterOrDigit(next) || next == '_')
                        at++;
                    else if (next == '.' && at + 1 < source.Length && char.IsDigit(source[at + 1]))
                        at++;
                    else if ((next == '+' || next == '-') && at > start && (source[at - 1] == 'e' || source[at - 1] == 'E'))
                        at++;
                    else
                        break;
                }
                kind = ScalaTokenKind.Number;
            }
            else if (Operator(c))
            {
                at++;
                while (at < source.Length && Operator(source[at]) && !(source[at - 1] == '/' && (source[at] == '/' || source[at] == '*')))
                    at++;
                kind = ScalaTokenKind.Operator;
            }
            else
            {
                at++;
                kind = ScalaTokenKind.Punctuation;
            }
            result.Add(new ScalaToken
            {
                Text = source.Substring(start, at - start),
                Start = start,
                Length = at - start,
                Line = startLine,
                Column = startColumn,
                Kind = kind,
                HasEscapes = kind == ScalaTokenKind.String && !(c == '"' && start + 2 < source.Length && source[start + 1] == '"' && source[start + 2] == '"')
            });
            for (int i = start; i < at; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                    column = 0;
                }
                else if (source[i] != '\r')
                    column += source[i] == '\t' ? ScalaIndentation.Size : 1;
            }
        }
        // Embedded expression tokens are initially relative to their substring.
        // Recompute coordinates once, preserving exact UTF-16 offsets throughout.
        int position = 0;
        line = column = 0;
        foreach (var token in result)
        {
            while (position < token.Start)
            {
                char ch = source[position++];
                if (ch == '\n')
                {
                    line++;
                    column = 0;
                }
                else if (ch != '\r')
                    column += ch == '\t' ? ScalaIndentation.Size : 1;
            }
            token.Line = line;
            token.Column = column;
        }
        return result;
    }

    private static int ReadInterpolated(string source, int start, int quote, List<ScalaToken> result)
    {
        bool triple = quote + 2 < source.Length && source[quote + 1] == '"' && source[quote + 2] == '"';
        int width = triple ? 3 : 1, at = quote + width, segment = start;
        bool escapes = !triple && source.Substring(start, quote - start) != "raw";
        void Add(int from, int to, ScalaTokenKind kind)
        {
            if (to > from)
                result.Add(new ScalaToken { Start = from, Length = to - from, Text = source.Substring(from, to - from), Kind = kind, HasEscapes = kind == ScalaTokenKind.String && escapes });
        }
        while (at < source.Length)
        {
            char c = source[at];
            if (!triple && c == '\\')
            {
                at = Math.Min(source.Length, at + 2);
                continue;
            }
            if (c == '"' && (!triple || at + 2 < source.Length && source[at + 1] == '"' && source[at + 2] == '"'))
            {
                at += width;
                break;
            }
            if (!triple && (c == '\r' || c == '\n'))
                break;
            if (c != '$')
            {
                at++;
                continue;
            }
            if (at + 1 < source.Length && source[at + 1] == '$')
            {
                at += 2;
                continue;
            }
            if (at + 1 < source.Length && source[at + 1] == '{')
            {
                Add(segment, at, ScalaTokenKind.String);
                // Keep braces as ordinary punctuation for scope/bracket consumers.
                Add(at, at + 1, ScalaTokenKind.Interpolation);
                Add(at + 1, at + 2, ScalaTokenKind.Punctuation);
                int content = at + 2, close = ExpressionEnd(source, content);
                foreach (var token in Lex(source.Substring(content, close - content)))
                {
                    token.Start += content;
                    result.Add(token);
                }
                at = close;
                if (at < source.Length && source[at] == '}')
                {
                    Add(at, at + 1, ScalaTokenKind.Punctuation);
                    at++;
                }
                segment = at;
            }
            else if (at + 1 < source.Length && NameStart(source[at + 1]) && source[at + 1] != '$')
            {
                Add(segment, at, ScalaTokenKind.String);
                Add(at, at + 1, ScalaTokenKind.Interpolation);
                int name = ++at;
                while (at < source.Length && NamePart(source[at]) && source[at] != '$')
                    at++;
                Add(name, at, ScalaTokenKind.Identifier);
                segment = at;
            }
            else
                at++;
        }
        Add(segment, at, ScalaTokenKind.String);
        return at;
    }

    private static int ExpressionEnd(string source, int at)
    {
        int depth = 1;
        while (at < source.Length)
        {
            char c = source[at];
            if (c == '"' || c == '\'')
            {
                at = ReadString(source, at, c);
                continue;
            }
            if (c == '`')
            {
                at++;
                while (at < source.Length && source[at] != '`')
                    at++;
                if (at < source.Length)
                    at++;
                continue;
            }
            if (c == '/' && at + 1 < source.Length && source[at + 1] == '/')
            {
                while (at < source.Length && source[at] != '\n')
                    at++;
                continue;
            }
            if (c == '/' && at + 1 < source.Length && source[at + 1] == '*')
            {
                at += 2;
                int comments = 1;
                while (at < source.Length && comments > 0)
                {
                    if (at + 1 < source.Length && source[at] == '/' && source[at + 1] == '*')
                    {
                        comments++;
                        at += 2;
                    }
                    else if (at + 1 < source.Length && source[at] == '*' && source[at + 1] == '/')
                    {
                        comments--;
                        at += 2;
                    }
                    else
                        at++;
                }
                continue;
            }
            // A nested interpolator may contain quotes in its own expressions.
            if (NameStart(c))
            {
                int start = at++;
                while (at < source.Length && NamePart(source[at]))
                    at++;
                if (at < source.Length && source[at] == '"')
                    at = ReadInterpolated(source, start, at, new List<ScalaToken>());
                continue;
            }
            if (c == '{')
                depth++;
            if (c == '}' && --depth == 0)
                return at;
            at++;
        }
        return at;
    }

    private static int ReadString(string source, int at, char quote)
    {
        bool triple = quote == '"' && at + 2 < source.Length && source[at + 1] == quote && source[at + 2] == quote;
        at += triple ? 3 : 1;
        while (at < source.Length)
        {
            char c = source[at];
            if (!triple && c == '\\')
            {
                at = Math.Min(source.Length, at + 2);
                continue;
            }
            if (c == quote && (!triple || at + 2 < source.Length && source[at + 1] == quote && source[at + 2] == quote))
                return at + (triple ? 3 : 1);
            if (!triple && (c == '\r' || c == '\n'))
                return at;
            at++;
        }
        return at;
    }
}
