using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FarmerScala.Editor;

internal static class ScalaModuleCompletion
{
    internal static bool IsImport(string source, int offset)
    {
        string prefix = source.Substring(0, Math.Max(0, Math.Min(offset, source.Length)));
        return Regex.IsMatch(prefix, @"(?:^|[\n;])\s*import\s+[^;\n]*$");
    }
    internal static List<string> Merge(IEnumerable<string> names, IEnumerable<string> modules, string source, int offset)
    {
        string prefix = source.Substring(0, Math.Max(0, Math.Min(offset, source.Length)));
        var match = Regex.Match(prefix, @"([\p{L}_][\p{L}\p{N}_]*(?:\.[\p{L}_][\p{L}\p{N}_]*)*\.?)?$");
        string path = match.Value;
        int dot = path.LastIndexOf('.');
        string parent = dot < 0 ? "" : path.Substring(0, dot + 1);
        string typed = path.Substring(dot + 1);
        bool import = IsImport(source, offset);
        if (import && Regex.IsMatch(prefix, @"import[^\n]*[{},]")) return names.Distinct().ToList();
        var roots = modules.Where(n => n.StartsWith(parent, StringComparison.Ordinal))
            .Select(n => n.Substring(parent.Length).Split('.')[0]);
        var result = names.Concat(roots).Distinct(StringComparer.Ordinal);
        if (import) result = result.Where(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
        return result.ToList();
    }
}
