using System;
using System.Collections.Generic;
using System.Reflection;
using FarmerScala.Editor;
using HarmonyLib;
using TMPro;

namespace FarmerScala.Game;

/// <summary>Native Scala starts with language/editor tools, not farming upgrades.</summary>
internal static class ScalaCodeUnlocks
{
    internal static readonly string[] UpgradeIds = {
        "loops", "operators", "senses", "variables", "functions", "import", "lists",
        "dictionaries", "debug", "debug_2", "timing", "simulation", "costs", "auto_unlock", "utilities"
    };
    internal static readonly string[] SyntaxIds = { "if", "else", "elif", "for", "range" };
    private static readonly HashSet<string> Approved = new HashSet<string>(UpgradeIds, StringComparer.Ordinal);
    private static readonly FieldInfo CodeText = AccessTools.Field(typeof(UnlockBox), "codeText");
    private static readonly FieldInfo Button = AccessTools.Field(typeof(UnlockBox), "button");
    private static bool reportedUiFailure;

    internal static bool IsCodeUpgrade(string id) => id != null && Approved.Contains(id);
    internal static bool IsLeaderboard(string id) => string.Equals(id, "leaderboard", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, "underground_leaderboard", StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies load input. Encoded IDs establish levels before native UnlockAllIn.</summary>
    internal static List<string> Augment(IEnumerable<string> original)
    {
        if (original == null)
            throw new ArgumentNullException(nameof(original));
        var result = new List<string>();
        foreach (string entry in original)
            result.Add(IsCodeUpgrade(entry) && !entry.StartsWith("debug", StringComparison.Ordinal) ? entry + "_1" : entry);
        foreach (string id in UpgradeIds)
            if (EffectiveLevel(result, id) < 1)
                result.Add(id.StartsWith("debug", StringComparison.Ordinal) ? id : id + "_1");
        foreach (string id in SyntaxIds)
            if (EffectiveLevel(result, id) < 1)
                result.Add(id);
        return result;
    }

    private static int EffectiveLevel(List<string> entries, string id)
    {
        int level = 0;
        foreach (string entry in entries)
        {
            if (entry == id)
            {
                level = 1;
                continue;
            }
            if (entry == null || id.StartsWith("debug", StringComparison.Ordinal))
                continue;
            int separator = entry.LastIndexOf('_');
            if (separator >= 0 && entry.Substring(0, separator) == id &&
                int.TryParse(entry.Substring(separator + 1), out int parsed))
                level = parsed;
        }
        return level;
    }

    [HarmonyPatch(typeof(MainSim), nameof(MainSim.SetupSim))]
    private static class LoadGrants
    {
        private static void Prefix(MainSim __instance, ref IEnumerable<string> __0)
        {
            if (EditorIntegration.IsLoadingScalaSave(__instance))
                __0 = Augment(__0);
        }
    }

    // Covers the UI and native unlock() API before native cost removal.
    [HarmonyPatch(typeof(Farm), nameof(Farm.UnlockOrUpgrade))]
    private static class LeaderboardPurchase
    {
        private static bool Prefix(UnlockSO __0, ref bool __result)
        {
            if (!EditorIntegration.BlocksNativeExecution || __0 == null || !IsLeaderboard(__0.unlockName))
                return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(UnlockBox), nameof(UnlockBox.SetupRec))]
    private static class TreePresentation
    {
        private static void Postfix(UnlockBox __instance, Dictionary<string, int> __3)
        {
            try
            {
                var unlock = __instance.unlockSO;
                if (unlock == null)
                    return;
                if (IsLeaderboard(unlock.unlockName) && EditorIntegration.BlocksNativeExecution)
                {
                    var button = Button.GetValue(__instance) as ColoredButton;
                    if (button == null)
                        return;
                    var theme = ThemeManager.Inst.Theme;
                    var colors = theme.unlocks.unlockable.ToColorBlock();
                    colors.disabledColor = theme.unlocks.NotUnlockableColor;
                    button.OverrideColors = colors;
                    button.Text = "Unavailable";
                    button.Interactable = false;
                    return;
                }
                var main = MainSim.Inst;
                if (main == null || !EditorIntegration.IsResolvedScalaSave(main) || !IsCodeUpgrade(unlock.unlockName) ||
                    !__3.TryGetValue(unlock.unlockName, out int level) || level < 1)
                    return;
                // A still-locked farming ancestor dims native descendants. Only
                // restore the granted code label; never pass unlockable=true to
                // SetupRec, which would also unlock physical child purchases.
                if (CodeText.GetValue(__instance) is TMP_Text code)
                {
                    var color = code.color;
                    color.a = 1f;
                    code.color = color;
                }
            }
            catch (Exception error)
            {
                if (reportedUiFailure)
                    return;
                reportedUiFailure = true;
                Plugin.Instance?.LogInfo("Code-upgrade tree presentation used native fallback: " + error.GetType().Name);
            }
        }
    }
}
