using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FarmerScala.Editor;

/// <summary>Native Markdown tooltip layout, with trusted markup around inert player text.</summary>
internal static class ScalaHoverFormatter
{
    private const string Font = "<font=\"FiraCode-Regular SDF\">";
    public static string Format(string hoverText, ScalaSyntaxPalette palette, bool projectSymbol = true, string? symbolKind = null)
    {
        if (string.IsNullOrWhiteSpace(hoverText))
            return "";
        string text = Bound(hoverText);
        int separator = text.IndexOf("\n\n", StringComparison.Ordinal);
        string signature = SafeText(separator >= 0 ? text.Substring(0, separator) : text).Trim();
        string description = separator >= 0 ? text.Substring(separator + 2).Trim() : "";
        var declarations = new ScalaCompletionModel(new Dictionary<string, string> { ["signature"] = signature }).Spans("signature").ToList();
        string title = "Scala";
        string titleKind = "functionDeclaration";
        int offset = 0;
        foreach (string line in signature.Split('\n'))
        {
            if (Name(line, out int start, out int length, out string declarationKind))
            {
                string name = line.Substring(start, length);
                string kind = !projectSymbol && (ScalaSyntaxHighlighter.IsBuiltinName(name) || name == "print") ? "builtin" : declarationKind;
                if (declarationKind == "value" && (symbolKind == "field" || symbolKind == "parameter" || symbolKind == "typeParameter"))
                    kind = symbolKind;
                declarations.Add(new SemanticSpan { Start = offset + start, Length = length, Kind = kind });
                if (title == "Scala")
                {
                    title = name;
                    titleKind = kind;
                }
            }
            offset += line.Length + 1;
        }
        string heading = ScalaSyntaxHighlighter.Colorize(title, palette, semanticSpans: new[] {
            new SemanticSpan { Start = 0, Length = title.Length, Kind = titleKind }
        });
        string code = ScalaSyntaxHighlighter.Colorize(signature, palette, semanticSpans: declarations);
        // The game's Markdown heading uses a 50% line-height newline. That is
        // too short for a larger code-font title followed by a code signature.
        // Normal line-height plus a blank line keeps the two rows separate at
        // every theme/font scale. Colored text never passes through backticks.
        return "<size=32px>" + Font + heading + "</font></size>\n\n"
            + Font + code + "</font>" + (description.Length == 0 ? "" : "\n\n" + Documentation(description, palette));
    }

    private sealed class DocTag
    {
        public string Kind = "", Name = "";
        public readonly List<string> Lines = new List<string>();
    }

    private static string Documentation(string text, ScalaSyntaxPalette palette)
    {
        var prose = new List<string>();
        var tags = new List<DocTag>();
        DocTag? active = null;
        bool code = false;
        foreach (string original in text.Split('\n'))
        {
            string line = original.TrimEnd();
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("{{{", StringComparison.Ordinal) || trimmed == "}}}")
                code = !code;
            if (!code && TryTag(trimmed, out var tag))
            {
                active = tag;
                tags.Add(tag);
                continue;
            }
            if (active == null)
                prose.Add(line);
            else
                active.Lines.Add(line);
        }
        var output = new StringBuilder();
        string body = string.Join("\n", prose).Trim();
        if (body.Length > 0)
            output.Append(Prose(body, palette));
        void Section(string label, IEnumerable<DocTag> entries, bool named, bool example = false)
        {
            var present = entries.ToList();
            if (present.Count == 0)
                return;
            if (output.Length > 0)
                output.Append("\n\n");
            output.Append("<b>").Append(label).Append("</b>\n");
            for (int i = 0; i < present.Count; i++)
            {
                if (i > 0)
                    output.Append('\n');
                var entry = present[i];
                // Parameter labels are names, even when one happens to be
                // spelled `Option` or `move`. Keep their code-font text in the
                // tooltip's ordinary foreground rather than guessing a kind.
                if (named)
                    output.Append(Font).Append(SafeText(entry.Name)).Append("</font> — ");
                string content = string.Join("\n", entry.Lines).Trim();
                if (example && !content.Contains("```") && !content.Contains("{{{"))
                    output.Append(Font).Append(ScalaSyntaxHighlighter.Colorize(SafeText(content), palette)).Append("</font>");
                else
                    output.Append(Prose(content, palette));
            }
        }
        Section("Type parameters", tags.Where(t => t.Kind == "tparam"), true);
        Section("Parameters", tags.Where(t => t.Kind == "param"), true);
        Section("Returns", tags.Where(t => t.Kind == "return"), false);
        Section("Throws", tags.Where(t => t.Kind == "throws" || t.Kind == "exception"), true);
        Section("Note", tags.Where(t => t.Kind == "note"), false);
        Section("Example", tags.Where(t => t.Kind == "example"), false, true);
        Section("See also", tags.Where(t => t.Kind == "see"), false);
        Section("Deprecated", tags.Where(t => t.Kind == "deprecated"), false);
        Section("Since", tags.Where(t => t.Kind == "since"), false);
        return output.ToString();
    }

    private static bool TryTag(string line, out DocTag tag)
    {
        tag = new DocTag();
        if (line.Length < 2 || line[0] != '@')
            return false;
        int end = 1;
        while (end < line.Length && char.IsLetter(line[end]))
            end++;
        string kind = line.Substring(1, end - 1);
        if (!new[] { "param", "tparam", "return", "throws", "exception", "note", "example", "see", "deprecated", "since", "group", "groupname", "groupdesc", "groupprio" }.Contains(kind))
            return false;
        string rest = line.Substring(end).TrimStart();
        tag.Kind = kind;
        if (kind == "param" || kind == "tparam" || kind == "throws" || kind == "exception")
        {
            int nameEnd = 0;
            if (rest.StartsWith("`", StringComparison.Ordinal))
            {
                int close = rest.IndexOf('`', 1);
                nameEnd = close < 0 ? rest.Length : close + 1;
            }
            else
                while (nameEnd < rest.Length && !char.IsWhiteSpace(rest[nameEnd]))
                    nameEnd++;
            tag.Name = rest.Substring(0, nameEnd).Trim('`');
            rest = rest.Substring(nameEnd).TrimStart();
        }
        tag.Lines.Add(rest);
        return true;
    }

    private static string Prose(string text, ScalaSyntaxPalette palette)
    {
        var output = new StringBuilder();
        var paragraph = new List<string>();
        var block = new List<string>();
        bool code = false, list = false;
        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            output.Append(Inline(string.Join(" ", paragraph), palette)).Append('\n');
            paragraph.Clear();
        }
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            bool boundary = trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed == "{{{" || trimmed == "}}}";
            if (boundary)
            {
                FlushParagraph();
                if (code)
                {
                    output.Append(Font).Append(ScalaSyntaxHighlighter.Colorize(SafeText(string.Join("\n", block)), palette)).Append("</font>\n");
                    block.Clear();
                }
                code = !code;
                list = false;
                continue;
            }
            if (code)
            {
                block.Add(line);
                continue;
            }
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                if (output.Length > 0 && output[output.Length - 1] == '\n')
                    output.Append('\n');
                list = false;
                continue;
            }
            bool listItem = trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+[.)]\s");
            if (listItem || trimmed.StartsWith("#", StringComparison.Ordinal) || list && line.Length > trimmed.Length)
            {
                FlushParagraph();
                output.Append(Inline(line, palette)).Append('\n');
                list = true;
            }
            else
            {
                list = false;
                paragraph.Add(trimmed);
            }
        }
        FlushParagraph();
        if (block.Count > 0)
            output.Append(Font).Append(ScalaSyntaxHighlighter.Colorize(SafeText(string.Join("\n", block)), palette)).Append("</font>");
        return output.ToString().TrimEnd();
    }

    private static string Inline(string line, ScalaSyntaxPalette palette)
    {
        var output = new StringBuilder();
        int at = 0;
        while (at < line.Length)
        {
            int tick = line.IndexOf('`', at);
            int link = line.IndexOf("[[", at, StringComparison.Ordinal);
            int open = tick < 0 ? link : link < 0 ? tick : Math.Min(tick, link);
            if (open < 0)
            {
                output.Append(SafeText(line.Substring(at)));
                break;
            }
            output.Append(SafeText(line.Substring(at, open - at)));
            if (open == link && TryReference(line, open, out int end, out string label))
            {
                output.Append(InlineCode(label, palette, reference: true));
                at = end;
            }
            else if (open == tick && line.IndexOf('`', open + 1) is int close && close >= 0)
            {
                output.Append(InlineCode(line.Substring(open + 1, close - open - 1), palette));
                at = close + 1;
            }
            else
            {
                // An unfinished doc edit remains literal and cannot swallow
                // later inline code or introduce native Markdown directives.
                output.Append(SafeText(line.Substring(open, 1)));
                at = open + 1;
            }
        }
        return output.ToString();
    }

    private static string InlineCode(string text, ScalaSyntaxPalette palette, bool reference = false)
    {
        text = SafeText(text);
        var spans = new List<SemanticSpan>();
        if (reference)
            foreach (var token in ScalaLexer.Lex(text))
                if (token.Identifier && ScalaSyntaxHighlighter.IsStandardTypeName(token.Text))
                    spans.Add(new SemanticSpan { Start = token.Start, Length = token.Length, Kind = "class" });
        return Font + ScalaSyntaxHighlighter.Colorize(text, palette, semanticSpans: spans) + "</font>";
    }

    private static bool TryReference(string text, int start, out int end, out string label)
    {
        end = start;
        label = "";
        int depth = 0, split = -1;
        bool backtick = false;
        for (int at = start + 2; at < text.Length; at++)
        {
            char c = text[at];
            if (c == '`')
                backtick = !backtick;
            if (backtick)
                continue;
            if (c == '[')
                depth++;
            else if (c == ']' && depth > 0)
                depth--;
            else if (c == ']' && at + 1 < text.Length && text[at + 1] == ']')
            {
                string target = text.Substring(start + 2, (split < 0 ? at : split) - start - 2).Trim();
                if (target.Length == 0)
                    return false;
                label = split < 0 ? target : text.Substring(split, at - split).Trim();
                if (label.Length == 0)
                    label = target;
                end = at + 2;
                return true;
            }
            else if (depth == 0 && split < 0 && char.IsWhiteSpace(c))
                split = at;
        }
        return false;
    }

    private static bool Name(string line, out int start, out int length, out string kind)
    {
        start = length = 0;
        kind = "functionDeclaration";
        var tokens = ScalaLexer.Lex(line);
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            string declaration = tokens[i].Text;
            if (declaration != "def" && declaration != "class" && declaration != "trait" && declaration != "enum" && declaration != "object" && declaration != "type" && declaration != "val" && declaration != "var")
                continue;
            var token = tokens[i + 1];
            if (!token.Identifier)
                return false;
            start = token.Start;
            length = token.Length;
            kind = declaration == "def" ? "functionDeclaration" : declaration == "type" ? "typeAlias" : declaration == "val" || declaration == "var" ? "value" : "class";
            return true;
        }
        return false;
    }

    private static string Bound(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = string.Join("\n", text.Split('\n').Take(64));
        if (text.Length <= 8192)
            return text;
        int end = char.IsHighSurrogate(text[8191]) && char.IsLowSurrogate(text[8192]) ? 8191 : 8192;
        return text.Substring(0, end) + "…";
    }
    private static string SafeText(string text)
    {
        // Native Markdown stages run regexes before TMP sees rich text, even
        // inside noparse. Neutralize their delimiters before adding our tags.
        // Keep ordinary Scala annotation brackets and return arrows readable.
        text = text.Replace('<', '‹').Replace('`', '′')
            .Replace("](", "]\u200B(")
            .Replace("{", "{\u200B").Replace("}", "\u200B}");
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("#", StringComparison.Ordinal))
                lines[i] = "\u200B" + lines[i];
        return string.Join("\n", lines);
    }
}
