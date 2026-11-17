// Test-only adapters: no Steam API or Unity objects are executed.
public sealed class ItemBlock
{
    public Dictionary<string, int> Values = new();
    public ItemBlock() { }
    public ItemBlock(ItemBlock other) { Values = new(other.Values); }
    public static ItemBlock CreateEmpty() => new();
}
public static class Achievements
{
    private static object lockObject = new();
    private static ItemBlock best = ItemBlock.CreateEmpty(), sum = ItemBlock.CreateEmpty(), total_stats = ItemBlock.CreateEmpty();
    private static List<int> window = new();
    private static HashSet<string> unlockedAchievements = new();
    private static HashSet<int> unlockedItemHats = new();
    private static int richPresenceItemId = -1;
}
namespace FarmerScala.Editor
{
    internal static class ScalaSyntaxHighlighter
    {
        internal static bool IsBuiltinName(string name) => false;
    }
}
namespace Steamworks
{
    public static class SteamUserStats
    {
        public static readonly List<string> Writes = new();
        private static bool Record(string text) { Writes.Add(text); return true; }
        public static bool SetAchievement(string key) => Record(key);
        public static bool ClearAchievement(string key) => Record(key);
        public static bool SetStat(string key, int value) => Record(key);
        public static bool SetStat(string key, float value) => Record(key);
        public static bool UpdateAvgRateStat(string key, float count, double seconds) => Record(key);
        public static bool ResetAllStats(bool achievementsToo) => Record("reset");
        public static bool IndicateAchievementProgress(string key, uint current, uint maximum) => Record(key);
        public static bool StoreStats() => Record("store");
        public static bool GetAchievement(string key, out bool achieved) { achieved = false; return true; }
    }
}
