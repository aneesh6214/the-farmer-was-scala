using System;

namespace FarmerScala.Editor;

/// <summary>Diagnostics become eligible only after two seconds with one unchanged project revision.</summary>
internal sealed class DiagnosticDelay
{
    private long revision = long.MinValue;
    private double due;
    internal bool Observe(long current, double now)
    {
        if (revision != current)
        {
            revision = current;
            due = now + 2;
            return false;
        }
        return now >= due;
    }
    internal void Reset()
    {
        revision = long.MinValue;
        due = 0;
    }
}

internal static class DiagnosticRanges
{
    internal static bool Normalize(string source, int start, int length, out int from, out int to)
    {
        from = to = 0;
        if (source.Length == 0 || start < 0 || start > source.Length || length < 0)
            return false;
        from = Math.Min(start, source.Length - 1);
        to = (int)Math.Min(source.Length, Math.Max((long)from + 1, (long)start + length));
        if (from > 0 && char.IsLowSurrogate(source[from]) && char.IsHighSurrogate(source[from - 1]))
            from--;
        if (to < source.Length && char.IsLowSurrogate(source[to]) && char.IsHighSurrogate(source[to - 1]))
            to++;
        while (from < to && (source[from] == '\r' || source[from] == '\n'))
            from++;
        return from < to;
    }

    internal static int MapCaret(string before, string after, int caret)
    {
        caret = Math.Max(0, Math.Min(before.Length, caret));
        int prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
            prefix++;
        if (caret <= prefix)
            return Math.Min(caret, after.Length);
        int suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[before.Length - suffix - 1] == after[after.Length - suffix - 1])
            suffix++;
        if (caret >= before.Length - suffix)
            return Math.Max(0, Math.Min(after.Length, after.Length - (before.Length - caret)));
        // Preserve the ordinal of the preceding non-whitespace character. A
        // formatter mostly changes whitespace, so this keeps the cursor near
        // its original expression without depending on an editor-specific map.
        int ordinal = 0;
        for (int i = 0; i < caret; i++)
            if (!char.IsWhiteSpace(before[i]))
                ordinal++;
        int at = 0, seen = 0;
        while (at < after.Length && seen < ordinal)
        {
            if (!char.IsWhiteSpace(after[at]))
                seen++;
            at++;
        }
        return at;
    }
}
