using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FarmerScala.Editor;

/// <summary>Merges external edits against the last synchronized source, preserving local changes.</summary>
internal static class SourceMerge
{
    internal sealed class Result
    {
        public readonly Dictionary<string, string> Sources;
        // A null external value records a deletion which conflicts with a local edit.
        public readonly Dictionary<string, string?> Conflicts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        public Result(IDictionary<string, string> editor) => Sources = new Dictionary<string, string>(editor, StringComparer.OrdinalIgnoreCase);
    }

    public static bool Equal(IDictionary<string, string> left, IDictionary<string, string> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var text) && text == pair.Value);

    public static Result Reconcile(IDictionary<string, string> baseline, IDictionary<string, string> disk, IDictionary<string, string> editor)
    {
        var result = new Result(editor);
        foreach (string module in baseline.Keys.Union(disk.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (SameModule(baseline, disk, module))
                continue;
            if (SameModule(baseline, editor, module) || SameModule(disk, editor, module))
            {
                if (disk.TryGetValue(module, out var text))
                    result.Sources[module] = text;
                else
                    result.Sources.Remove(module);
            }
            else
                result.Conflicts[module] = disk.TryGetValue(module, out var text) ? text : null;
        }
        return result;
    }

    public static string? ArchiveConflicts(Result result, string projectDirectory)
    {
        if (result.Conflicts.Count == 0)
            return null;
        string history = Path.Combine(projectDirectory, "history");
        Directory.CreateDirectory(history);
        string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        foreach (var conflict in result.Conflicts)
        {
            string suffix = conflict.Value == null ? ".deleted.txt" : ".scala";
            string content = conflict.Value ?? "The external editor deleted this module while the game editor had unsaved changes.\n";
            File.WriteAllText(Path.Combine(history, stamp + "-external-" + conflict.Key + suffix), content, new UTF8Encoding(false));
        }
        return history;
    }

    private static bool SameModule(IDictionary<string, string> left, IDictionary<string, string> right, string module)
    {
        bool hasLeft = left.TryGetValue(module, out var a);
        bool hasRight = right.TryGetValue(module, out var b);
        return hasLeft == hasRight && (!hasLeft || a == b);
    }
}
