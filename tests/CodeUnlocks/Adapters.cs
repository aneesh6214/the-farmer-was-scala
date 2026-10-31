using System.Reflection;

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string name)
        {
        }
    }
    public static class AccessTools
    {
        public static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    }
}
namespace UnityEngine
{
    public struct Color
    {
        public float r, g, b, a;
    }
}
namespace TMPro
{
    public class TMP_Text
    {
        public UnityEngine.Color color;
    }
}
public class UnlockSO
{
    public string unlockName = "", docs = "", parentUnlock = ""; public List<string> unlocks = new(); public int maxUnlockLevel = 1;
}
public struct TestColorBlock
{
    public UnityEngine.Color disabledColor;
}
public class TestColors
{
    public TestColorBlock ToColorBlock() => new();
}
public class ThemeManager
{
    public static ThemeManager Inst = new(); public TestTheme Theme = new();
    public class TestTheme
    {
        public UnlockColors unlocks = new();
    }
    public class UnlockColors
    {
        public TestColors unlockable = new(); public UnityEngine.Color NotUnlockableColor = new() { r = .1f, g = .2f, b = .3f, a = 1 };
    }
}
public class ColoredButton
{
    public string Text = "Unlock"; public bool Interactable = true; public TestColorBlock OverrideColors;
}
public class UnlockBox
{
    public UnlockSO unlockSO = new(); private TMPro.TMP_Text codeText = new(); private ColoredButton button = new();
    public TMPro.TMP_Text Label => codeText; public ColoredButton Button => button;
    public void SetupRec(bool unlockable, HashSet<string> docs, object inventory, Dictionary<string, int> unlocks, out bool allUnlocked)
    {
        allUnlocked = false;
    }
}
public class MainSim
{
    public static MainSim? Inst; public Farm Farm = new(Array.Empty<string>()); public HashSet<string> OpenedDocs = new();
    public void SetupSim(IEnumerable<string> unlocks)
    {
        Farm = new(unlocks);
        foreach (var node in Fixtures.Nodes.Values)
            if (Farm.Level(node.unlockName) > 0)
                OpenedDocs.Add(node.docs);
    }
}
public class Farm
{
    public Dictionary<string, int> Levels = new(StringComparer.Ordinal); public int Items = 100, PurchaseCalls;
    public Farm(IEnumerable<string> unlocks)
    {
        foreach (string value in unlocks)
        {
            int underscore = value.LastIndexOf('_');
            string name;
            int level;
            if (underscore >= 0 && int.TryParse(value[(underscore + 1)..], out level) && !value.StartsWith("debug", StringComparison.Ordinal))
            {
                name = value[..underscore];
                Unlock(name, level);
            }
            else
            {
                name = value;
                level = 1;
            }
            if (Fixtures.Nodes.TryGetValue(name, out var node))
                foreach (var token in node.unlocks)
                {
                    if (char.IsDigit(token[0]))
                    {
                        if (token[0] - '0' <= Level(node.unlockName))
                            Unlock(token[1..], 1);
                    }
                    else
                        Unlock(token, 1);
                }
            Unlock(name, level);
        }
    }
    public void Unlock(string name, int level) => Levels[name.ToLowerInvariant()] = level;
    public int Level(string name) => Levels.GetValueOrDefault(name.ToLowerInvariant());
    public bool UnlockOrUpgrade(UnlockSO node, bool requireParent = true)
    {
        PurchaseCalls++;
        if (Level(node.unlockName) > 0)
            return false;
        Items -= 10;
        Unlock(node.unlockName, 1);
        return true;
    }
    public List<string> Serialize() => Levels.Select(p => p.Value > 1 ? p.Key + "_" + p.Value : p.Key).ToList();
}
public static class Fixtures
{
    public static readonly Dictionary<string, UnlockSO> Nodes = new(StringComparer.Ordinal);
    public static void Add(string name, string parent, string docs, params string[] tokens) => Nodes[name] = new() { unlockName = name, parentUnlock = parent, docs = docs, unlocks = tokens.ToList() };
    public static void Initialize()
    {
        Nodes.Clear();
        Add("loops", "", "docs/scripting/while.md", "while", "True", "False", "break", "continue");
        Add("operators", "plant", "docs/scripting/operators.md", "and", "or", "not");
        Add("senses", "operators", "docs/unlocks/senses.md", "get_entity_type", "get_ground_type", "Grounds", "get_pos_x", "get_pos_y", "None", "num_items", "Items", "num_unlocked");
        Add("variables", "operators", "docs/scripting/variables.md");
        Add("functions", "variables", "docs/scripting/functions.md", "functions", "def", "return", "global");
        Add("import", "functions", "docs/scripting/import.md", "from");
        Add("lists", "variables", "docs/scripting/lists.md", "append", "remove", "pop", "insert", "len", "list");
        Add("dictionaries", "lists", "docs/scripting/dicts.md", "dicts", "sets", "add", "dict", "set");
        Add("debug", "plant", "docs/scripting/debug.md", "print", "quick_print", "Unlocks", "str");
        Add("debug_2", "debug", "docs/unlocks/debug2.md", "set_execution_speed", "set_world_size");
        Add("timing", "debug", "docs/unlocks/timing.md", "get_time", "get_tick_count");
        Add("simulation", "timing", "docs/unlocks/simulation.md", "simulate");
        Add("costs", "dictionaries", "docs/unlocks/costs.md", "get_cost", "Unlocks");
        Add("auto_unlock", "costs", "docs/unlocks/auto_unlock.md", "Unlocks", "unlock", "num_unlocked");
        Add("utilities", "functions", "", "min", "max", "abs", "random");
    }
}
namespace FarmerScala
{
    public class Plugin
    {
        public static Plugin? Instance = new(); public void LogInfo(string text)
        {
        }
    }
}
namespace FarmerScala.Editor
{
    internal static class EditorIntegration
    {
        public static bool Native = true, Loading = true, Failed; public static MainSim? Active;
        public static bool BlocksNativeExecution => Native || Failed;
        public static bool IsLoadingScalaSave(MainSim main) => Native && Loading && !Failed && ReferenceEquals(main, Active) && ReferenceEquals(main, MainSim.Inst);
        public static bool IsResolvedScalaSave(MainSim main) => Native && !Loading && !Failed && ReferenceEquals(main, Active) && ReferenceEquals(main, MainSim.Inst);
    }
}
