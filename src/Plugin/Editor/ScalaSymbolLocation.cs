using System;
using System.Globalization;

namespace FarmerScala.Editor;

/// <summary>Offsets are source UTF-16 indices, never TMP character indices.</summary>
internal static class ScalaSymbolLocation
{
    internal static bool TryIdentifier(string source, int index, out int start, out int end)
    {
        start = end = 0;
        if (index < 0 || index >= source.Length)
            return false;
        int lineStart = source.LastIndexOf('\n', index);
        int quoted = -1;
        for (int at = lineStart + 1; at <= index; at++)
            if (source[at] == '`')
            {
                if (quoted < 0)
                    quoted = at;
                else if (at == index)
                {
                    start = quoted;
                    end = at + 1;
                    return true;
                }
                else
                    quoted = -1;
            }
        if (quoted >= 0)
        {
            int close = source.IndexOf('`', Math.Max(index + 1, quoted + 1));
            int newline = source.IndexOf('\n', index);
            if (close >= 0 && (newline < 0 || close < newline))
            {
                start = quoted;
                end = close + 1;
                return true;
            }
        }
        if (ScalaLexer.Operator(source[index]))
        {
            start = index;
            end = index + 1;
            while (start > 0 && ScalaLexer.Operator(source[start - 1]))
                start--;
            while (end < source.Length && ScalaLexer.Operator(source[end]))
                end++;
            return true;
        }
        if (char.IsLowSurrogate(source[index]) && index > 0 && char.IsHighSurrogate(source[index - 1]))
            index--;
        if (!IsPart(source, index))
            return false;
        start = index;
        while (start > 0)
        {
            int previous = start - 1;
            if (char.IsLowSurrogate(source[previous]) && previous > 0 && char.IsHighSurrogate(source[previous - 1]))
                previous--;
            if (!IsPart(source, previous))
                break;
            start = previous;
        }
        end = index;
        while (end < source.Length && IsPart(source, end))
            end += char.IsHighSurrogate(source[end]) && end + 1 < source.Length && char.IsLowSurrogate(source[end + 1]) ? 2 : 1;
        // Numbers are not symbols. A combining mark cannot start a Scala name.
        var category = CharUnicodeInfo.GetUnicodeCategory(source, start);
        return source[start] == '_' || source[start] == '$' || category == UnicodeCategory.UppercaseLetter || category == UnicodeCategory.LowercaseLetter
            || category == UnicodeCategory.TitlecaseLetter || category == UnicodeCategory.ModifierLetter
            || category == UnicodeCategory.OtherLetter || category == UnicodeCategory.LetterNumber;
    }

    private static bool IsPart(string source, int index)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(source, index);
        return source[index] == '_' || source[index] == '$' || category == UnicodeCategory.UppercaseLetter || category == UnicodeCategory.LowercaseLetter
            || category == UnicodeCategory.TitlecaseLetter || category == UnicodeCategory.ModifierLetter
            || category == UnicodeCategory.OtherLetter || category == UnicodeCategory.LetterNumber
            || category == UnicodeCategory.DecimalDigitNumber || category == UnicodeCategory.NonSpacingMark
            || category == UnicodeCategory.SpacingCombiningMark || category == UnicodeCategory.ConnectorPunctuation;
    }
}
