using System;
using System.Collections.Generic;
using System.Text;

namespace FarmerScala.Editor;

internal sealed class ScalaSyntaxPalette
{
    public string Keyword = "", String = "", Comment = "", Number = "", Function = "", Class = "", Builtin = "", Search = "", SelectedSearch = "", Documentation = "#B8B8B8";
}

/// <summary>Immediate Scala lexical colors plus exact-source project symbol overlays.</summary>
internal static class ScalaSyntaxHighlighter
{
    private static readonly HashSet<string> GameNames = new HashSet<string>((
        "harvest can_harvest swap plant move can_move till get_pos_x get_pos_y get_world_size get_entity_type get_ground_type get_time get_tick_count " +
        "use_item get_water do_a_flip change_hat game_print quick_print num_items get_cost clear get_companion unlock num_unlocked measure random simulate " +
        "set_execution_speed set_world_size spawn_drone num_drones max_drones wait_for has_finished pet_the_piggy tap " +
        "Entities Grounds Items Hats Unlocks Direction North East South West").Split(' '), StringComparer.Ordinal);
    internal static bool IsBuiltinName(string name) => GameNames.Contains(name);
    private static readonly HashSet<string> StandardTypes = new HashSet<string>((
        "Any AnyRef AnyVal Nothing Null Unit Boolean Byte Short Int Long Float Double Char String " +
        "Option Some None Either Left Right List Nil Seq IndexedSeq Vector Set Map Array Iterator Iterable LazyList BigInt BigDecimal").Split(' '), StringComparer.Ordinal);
    internal static bool IsStandardTypeName(string name) => StandardTypes.Contains(name);

    public static string Colorize(string source, ScalaSyntaxPalette palette, string searchWord = "", int searchIndex = -1,
        bool escapeMarkup = true, IReadOnlyList<SemanticSpan>? semanticSpans = null, int navigationStart = -1, int navigationLength = 0)
    {
        if (source.Length == 0)
            return source;
        var colors = new string[source.Length];
        var semantics = new Dictionary<int, SemanticSpan>();
        if (semanticSpans != null)
            foreach (var span in semanticSpans)
                if (span.Start >= 0 && span.Length > 0 && span.Start <= source.Length - span.Length)
                    semantics[span.Start] = span;
        var tokens = ScalaLexer.Lex(source);
        string declaration = "";
        for (int t = 0; t < tokens.Count; t++)
        {
            var token = tokens[t];
            string color = "";
            switch (token.Kind)
            {
                case ScalaTokenKind.Comment:
                    color = palette.Comment;
                    break;
                case ScalaTokenKind.String:
                    color = palette.String;
                    break;
                case ScalaTokenKind.Number:
                    color = palette.Number;
                    break;
                case ScalaTokenKind.Keyword:
                    color = palette.Keyword;
                    break;
                case ScalaTokenKind.Interpolation:
                    color = palette.Keyword;
                    break;
                case ScalaTokenKind.Identifier:
                case ScalaTokenKind.Operator:
                    bool member = t > 0 && tokens[t - 1].Text == ".";
                    if (semantics.TryGetValue(token.Start, out var semantic) && semantic.Length == token.Length)
                        color = semantic.Kind == "builtin" ? palette.Builtin : semantic.Kind == "class" || semantic.Kind == "object" ? palette.Class : semantic.Kind == "function" ? palette.Function : "";
                    else if (declaration == "def")
                        color = palette.Function;
                    else if (declaration == "class" || declaration == "trait" || declaration == "enum" || declaration == "object" || declaration == "type")
                        color = palette.Class;
                    else if (!member && GameNames.Contains(token.Text))
                        color = palette.Builtin;
                    else if (!member && StandardTypes.Contains(token.Text))
                        color = palette.Class;
                    else if (t + 1 < tokens.Count && tokens[t + 1].Text == "(")
                        color = palette.Function;
                    break;
            }
            if (token.Kind != ScalaTokenKind.Comment)
                declaration = token.Kind == ScalaTokenKind.Keyword && (token.Text == "class" || token.Text == "trait" || token.Text == "enum" || token.Text == "object" || token.Text == "type" || token.Text == "def") ? token.Text : "";
            for (int i = token.Start; i < token.End; i++)
                colors[i] = color;
            if (token.Kind == ScalaTokenKind.Comment && token.Text.StartsWith("/**", StringComparison.Ordinal))
                ColorScaladoc(source, token.Start, token.End, palette, colors, semantics);
        }
        var marks = new byte[source.Length];
        if (!string.IsNullOrEmpty(searchWord))
            for (int offset = 0; offset <= source.Length - searchWord.Length;)
            {
                int found = source.IndexOf(searchWord, offset, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    break;
                int end = found + searchWord.Length;
                if (!SplitsPair(source, found) && !SplitsPair(source, end))
                    for (int i = found; i < end; i++)
                        marks[i] = found == searchIndex ? (byte)2 : (byte)1;
                offset = end;
            }
        bool navigation = navigationStart >= 0 && navigationLength > 0 && navigationStart <= source.Length - navigationLength &&
            ScalaSymbolLocation.TryIdentifier(source, navigationStart, out int symbolStart, out int symbolEnd) &&
            symbolStart == navigationStart && symbolEnd == navigationStart + navigationLength;
        var output = new StringBuilder(source.Length + 64);
        string currentColor = "", currentMark = "";
        bool underlined = false;
        for (int i = 0; i < source.Length; i++)
        {
            string color = colors[i] ?? "";
            string mark = marks[i] == 0 ? "" : marks[i] == 2 ? palette.SelectedSearch : palette.Search;
            bool underline = navigation && i >= navigationStart && i < navigationStart + navigationLength;
            if (color != currentColor || mark != currentMark || underline != underlined)
            {
                if (currentMark.Length != 0)
                    output.Append("</mark>");
                if (currentColor.Length != 0)
                    output.Append("</color>");
                if (underlined)
                    output.Append("</u>");
                currentColor = color;
                currentMark = mark;
                underlined = underline;
                if (underlined)
                    output.Append("<u>");
                if (color.Length != 0)
                    output.Append("<color=").Append(color).Append('>');
                if (mark.Length != 0)
                    output.Append("<mark=").Append(mark).Append(" from_search>");
            }
            if (escapeMarkup && source[i] == '<')
                output.Append("<noparse><</noparse>");
            else
                output.Append(source[i]);
        }
        if (currentMark.Length != 0)
            output.Append("</mark>");
        if (currentColor.Length != 0)
            output.Append("</color>");
        if (underlined)
            output.Append("</u>");
        return output.ToString();
    }

    private static void ColorScaladoc(string source, int start, int end, ScalaSyntaxPalette palette, string[] colors, Dictionary<int, SemanticSpan> semantics)
    {
        void Paint(int from, int to, string color)
        {
            for (int i = from; i < to && i < end; i++)
                colors[i] = color;
        }
        for (int line = start; line < end;)
        {
            int at = line == start ? start + 3 : line;
            while (at < end && (char.IsWhiteSpace(source[at]) && source[at] != '\n' || source[at] == '*'))
                at++;
            if (at < end && source[at] == '@')
            {
                int tag = at++;
                while (at < end && char.IsLetter(source[at]))
                    at++;
                string name = source.Substring(tag + 1, at - tag - 1);
                Paint(tag, at, palette.Documentation);
                if (name == "param" || name == "tparam" || name == "throws" || name == "exception")
                {
                    while (at < end && (source[at] == ' ' || source[at] == '\t'))
                        at++;
                    int parameter = at;
                    while (at < end && !char.IsWhiteSpace(source[at]))
                        at++;
                    Paint(parameter, at, "");
                    if (name == "throws" || name == "exception")
                        foreach (var token in ScalaLexer.Lex(source.Substring(parameter, at - parameter)))
                            if (semantics.TryGetValue(parameter + token.Start, out var type) && type.Length == token.Length && type.Kind == "class")
                                Paint(parameter + token.Start, parameter + token.End, palette.Class);
                }
            }
            int newline = source.IndexOf('\n', line);
            if (newline < 0 || newline >= end)
                break;
            line = newline + 1;
        }
        for (int at = start + 3; at < end; at++)
        {
            if (source[at] != '[')
                continue;
            int depth = 1, close = at + 1;
            for (; close < end; close++)
            {
                if (source[close] == '[')
                    depth++;
                else if (source[close] == ']' && --depth == 0)
                    break;
            }
            if (close >= end)
                continue;
            int content = at + 1, contentEnd = close;
            if (content < close && source[content] == '[' && source[close - 1] == ']')
            {
                content++;
                contentEnd--;
            }
            Paint(at, close + 1, "");
            Paint(at, content, palette.Documentation);
            Paint(contentEnd, close + 1, palette.Documentation);
            var code = ScalaLexer.Lex(source.Substring(content, contentEnd - content));
            for (int i = 0; i < code.Count; i++)
            {
                var token = code[i];
                string color = token.Kind == ScalaTokenKind.String ? palette.String : token.Kind == ScalaTokenKind.Number ? palette.Number :
                    token.Kind == ScalaTokenKind.Keyword ? palette.Keyword : token.Kind == ScalaTokenKind.Comment ? palette.Comment : "";
                if (token.Identifier)
                    color = StandardTypes.Contains(token.Text) ? palette.Class : GameNames.Contains(token.Text) ? palette.Builtin :
                        i + 1 < code.Count && code[i + 1].Text == "(" ? palette.Function : "";
                if (semantics.TryGetValue(content + token.Start, out var semantic) && semantic.Length == token.Length)
                    color = semantic.Kind == "builtin" ? palette.Builtin : semantic.Kind == "class" || semantic.Kind == "object" ? palette.Class : semantic.Kind == "function" ? palette.Function : "";
                Paint(content + token.Start, content + token.End, color);
            }
            at = close;
        }
    }
    private static bool SplitsPair(string source, int at) => at > 0 && at < source.Length && char.IsHighSurrogate(source[at - 1]) && char.IsLowSurrogate(source[at]);
}
