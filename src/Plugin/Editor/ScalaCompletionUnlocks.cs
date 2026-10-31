using System;
using System.Collections.Generic;
using System.Linq;

namespace FarmerScala.Editor;

/// <summary>Filter resolved game suggestions against the current save at display time.</summary>
internal static class ScalaCompletionUnlocks
{
    internal static Func<string, bool>? IsUnlocked;
    internal static bool Allows(string key) => key != "leaderboard" && key != "leaderboard_run" &&
        (IsUnlocked == null || IsUnlocked(key));
    internal static List<string> Filter(IEnumerable<string> names, IReadOnlyDictionary<string, string> identities)
        => names.Where(name => !identities.TryGetValue(name, out var key) || Allows(key)).ToList();
    internal static List<string> Native(IEnumerable<string> names) => names.Where(name =>
        !ScalaSyntaxHighlighter.IsBuiltinName(name) || Allows(name == "game_print" ? "print" : name == "Directions" ? "move" : name.ToLowerInvariant())).ToList();
}
