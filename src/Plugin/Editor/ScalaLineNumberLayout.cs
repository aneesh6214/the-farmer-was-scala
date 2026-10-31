using System;
using System.Collections.Generic;

namespace FarmerScala.Editor;

internal readonly struct ScalaNumberRow
{
    internal readonly int Number;
    internal readonly float Baseline, Top, Bottom;
    internal ScalaNumberRow(int number, float baseline, float top, float bottom)
    {
        Number = number; Baseline = baseline; Top = top; Bottom = bottom;
    }
}

/// <summary>Logical source lines and viewport lookup. Offsets are UTF-16, never glyph indices.</summary>
internal static class ScalaLineNumberLayout
{
    internal static int[] LineStarts(string source)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\r')
            {
                if (i + 1 < source.Length && source[i + 1] == '\n')
                    i++;
                starts.Add(i + 1);
            }
            else if (source[i] == '\n')
                starts.Add(i + 1);
        }
        return starts.ToArray();
    }

    internal static int LineAt(IReadOnlyList<int> starts, int offset)
    {
        int lo = 0, hi = starts.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (starts[mid] <= offset) lo = mid + 1;
            else hi = mid;
        }
        return Math.Max(1, lo);
    }

    // Native breakpoints use zero-based source lines. Wrapped continuations (0)
    // and marked lines have no number; the native marker takes its place.
    internal static bool ShowNumber(int number, ISet<int> breakpoints) =>
        number > 0 && !breakpoints.Contains(number - 1);

    internal static int Digits(int lines)
    {
        int digits = 1;
        while (lines >= 10) { lines /= 10; digits++; }
        return Math.Max(2, digits);
    }

    // TMP rows are ordered from top to bottom. Find the first row intersecting
    // the viewport in logarithmic time, then render only the visible rows.
    internal static int FirstVisible(IReadOnlyList<ScalaNumberRow> rows, float top)
    {
        int lo = 0, hi = rows.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (rows[mid].Bottom > top) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
