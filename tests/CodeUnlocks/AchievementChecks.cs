using System.Reflection;
using FarmerScala;
using FarmerScala.Editor;

internal static class AchievementChecks
{
    internal static void Run(Action<bool, string> check, Assembly core, string managed)
    {
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Static;
        Type guard = typeof(SteamAchievementGuard);
        var boundary = guard.GetNestedType("SaveBoundary", BindingFlags.NonPublic)!.GetMethod("Postfix", hidden)!;
        Type writes = guard.GetNestedType("SteamWrites", BindingFlags.NonPublic)!;
        var prefix = writes.GetMethod("Prefix", hidden)!;
        var targets = ((IEnumerable<MethodBase>)writes.GetMethod("TargetMethods", hidden)!.Invoke(null, null)!).Cast<MethodInfo>().ToArray();
        check(targets.Length == 8 && targets.All(m => m.Name != "GetAchievement"), "achievement isolation covers every write overload while leaving reads available");

        // Assert that the adapters still match the installed native metadata.
        var steam = Assembly.LoadFrom(Path.Combine(managed, "com.rlabrecque.steamworks.net.dll")).GetType("Steamworks.SteamUserStats", true)!;
        check(targets.All(m => steam.GetMethod(m.Name, BindingFlags.Public | BindingFlags.Static, null, m.GetParameters().Select(p => p.ParameterType).ToArray(), null)?.ReturnType == typeof(bool)), "all achievement/stat write hooks match the installed Steam API signatures");
        string[] names = { "best", "sum", "total_stats", "window", "unlockedAchievements", "unlockedItemHats", "richPresenceItemId" };
        var fields = names.Select(name => typeof(Achievements).GetField(name, hidden)!).ToArray();
        Type native = core.GetType("Achievements", true)!;
        check(names.Append("lockObject").All(name => native.GetField(name, hidden) != null), "native achievement counter and lock fields remain available");
        ((ItemBlock)fields[0].GetValue(null)!).Values["coal"] = 12;
        ((HashSet<string>)fields[4].GetValue(null)!).Add("existing-native-achievement");
        var original = fields.Select(f => f.GetValue(null)).ToArray();
        bool Allowed(out bool result)
        {
            object?[] args = { true };
            bool allowed = (bool)prefix.Invoke(null, args)!;
            result = (bool)args[0]!;
            return allowed;
        }
        EditorIntegration.Native = false;
        EditorIntegration.Failed = false;
        boundary.Invoke(null, null);
        check(Allowed(out _) && fields.Select(f => f.GetValue(null)).SequenceEqual(original), "Default saves retain native Steam writes and achievement counters");
        EditorIntegration.Native = true;
        boundary.Invoke(null, null);
        var isolated = fields.Select(f => f.GetValue(null)).ToArray();
        check(fields.Take(6).Select((f, i) => !ReferenceEquals(f.GetValue(null), original[i])).All(x => x), "Scala isolates all progress counters, including mining item totals");
        check(((ItemBlock)isolated[0]!).Values["coal"] == 12, "isolated mining counters start from a copy of native totals");
        ((ItemBlock)isolated[0]!).Values["coal"] = 900;
        ((HashSet<string>)isolated[4]!).Add("scala-mining-achievement");
        check(((ItemBlock)original[0]!).Values["coal"] == 12 && !((HashSet<string>)original[4]!).Contains("scala-mining-achievement"), "Scala mining progress cannot contaminate counters restored to a Default save");
        foreach (string key in new[] { "base-game-achievement", "mining-achievement" })
        {
            bool allowed = Allowed(out bool result);
            if (allowed) Steamworks.SteamUserStats.SetAchievement(key);
            check(!allowed && !result && Steamworks.SteamUserStats.Writes.Count == 0, key + " is blocked before the Steam cache is updated");
        }
        foreach (var target in targets)
        {
            bool allowed = Allowed(out bool result);
            if (allowed) throw new Exception("Unexpected permitted write: " + target.Name);
            check(!result, "Scala rejects " + target.Name + " with a false result");
        }
        boundary.Invoke(null, null);
        check(fields.Select(f => f.GetValue(null)).SequenceEqual(isolated), "reloading a Scala save does not replace or publish its isolated counters");
        EditorIntegration.Native = false;
        check(!Allowed(out _), "Steam writes remain blocked until the save boundary restores native counters");
        boundary.Invoke(null, null);
        check(Allowed(out _) && fields.Select(f => f.GetValue(null)).SequenceEqual(original), "switching to Default restores the exact original achievement state");
        EditorIntegration.Failed = true;
        boundary.Invoke(null, null);
        check(!Allowed(out bool failedResult) && !failedResult, "unresolved save metadata blocks base and mining achievements");
        EditorIntegration.Failed = false;
        boundary.Invoke(null, null);
        check(Allowed(out _) && fields.Select(f => f.GetValue(null)).SequenceEqual(original), "recovery to Default restores Steam behavior without modded progress");
        EditorIntegration.Native = true;
    }
}
