using System.Collections.Generic;
using System.Linq;

namespace FarmerScala.Editor;

internal static class SemanticSpanMerger
{
    // A compiler may omit unresolved/synthetic nodes while an edit is incomplete.
    // Keep exact-source local declarations until authoritative answers arrive.
    internal static IReadOnlyList<SemanticSpan> Merge(IReadOnlyList<SemanticSpan> local, IReadOnlyList<SemanticSpan> compiler)
    {
        var result = new Dictionary<(int Start, int Length), SemanticSpan>();
        foreach (var span in local)
            result[(span.Start, span.Length)] = span;
        foreach (var span in compiler)
            result[(span.Start, span.Length)] = span;
        return result.Values.OrderBy(s => s.Start).ToArray();
    }
}
