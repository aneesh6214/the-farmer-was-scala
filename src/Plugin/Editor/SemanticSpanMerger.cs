using System.Collections.Generic;
using System.Linq;

namespace FarmerScala.Editor;

internal static class SemanticSpanMerger
{
    // A compiler may omit unresolved/synthetic nodes while an edit is incomplete.
    // Keep exact-source local declarations until authoritative answers arrive.
    internal static IReadOnlyList<SemanticSpan> Merge(IReadOnlyList<SemanticSpan> local, IReadOnlyList<SemanticSpan> compiler, bool authoritative = true)
    {
        var result = new Dictionary<(int Start, int Length), SemanticSpan>();
        foreach (var span in local)
            result[(span.Start, span.Length)] = span;
        foreach (var span in compiler)
        {
            var key = (span.Start, span.Length);
            // A current source binding wins over an old role from another scope.
            // Exact current compiler answers still refine fields/parameters.
            if (!authoritative && result.TryGetValue(key, out var current) &&
                (current.Kind == "value" || current.Kind == "field" || current.Kind == "parameter" || current.Kind == "typeParameter" || current.Kind == "typeAlias") &&
                span.Kind != "builtin")
                continue;
            result[key] = span;
        }
        return result.Values.OrderBy(s => s.Start).ToArray();
    }
}
