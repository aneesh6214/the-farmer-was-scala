using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FarmerScala.Editor;
using HarmonyLib;

namespace FarmerScala;

/// <summary>Keep local rewards, but never credit Scala progress to Steam.</summary>
internal static class SteamAchievementGuard
{
    private static volatile bool isolated;
    private static readonly FieldInfo Gate = AccessTools.Field(typeof(Achievements), "lockObject");
    private static readonly FieldInfo[] Counters = new[] {
        "best", "sum", "total_stats", "window", "unlockedAchievements", "unlockedItemHats", "richPresenceItemId"
    }.Select(name => AccessTools.Field(typeof(Achievements), name)).ToArray();
    private static object?[]? nativeCounters;

    // Called after language resolution, before Saver.Load grants any upgrades.
    // Restore the original objects, rather than letting modded totals/achievement
    // deduplication suppress or credit a later ordinary-game achievement.
    [HarmonyPatch(typeof(EditorIntegration), nameof(EditorIntegration.BeforeLoad))]
    private static class SaveBoundary
    {
        private static void Postfix()
        {
            bool next = EditorIntegration.BlocksNativeExecution;
            lock (Gate.GetValue(null))
            {
                if (next == isolated)
                    return;
                if (next)
                {
                    nativeCounters = Counters.Select(field => field.GetValue(null)).ToArray();
                    isolated = true;
                    foreach (var field in Counters)
                    {
                        object? value = field.GetValue(null);
                        object replacement = field.FieldType == typeof(ItemBlock)
                            ? value is ItemBlock items ? new ItemBlock(items) : ItemBlock.CreateEmpty()
                            : field.FieldType == typeof(int) ? -1 : Activator.CreateInstance(field.FieldType)!;
                        field.SetValue(null, replacement);
                    }
                }
                else
                {
                    if (nativeCounters == null)
                        throw new InvalidOperationException("Missing ordinary-game achievement counters.");
                    for (int i = 0; i < Counters.Length; i++)
                        Counters[i].SetValue(null, nativeCounters[i]);
                    nativeCounters = null;
                    isolated = false;
                }
            }
        }
    }

    // Block at the Steam cache boundary, not merely StoreStats: Steam can flush
    // cached achievements on exit or on a later ordinary save. Reads stay native.
    [HarmonyPatch]
    private static class SteamWrites
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("Steamworks.SteamUserStats")
                ?? throw new TypeLoadException("SteamUserStats is required for achievement isolation.");
            var names = new HashSet<string> { "SetAchievement", "ClearAchievement", "SetStat",
                "UpdateAvgRateStat", "ResetAllStats", "IndicateAchievementProgress", "StoreStats" };
            return type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => names.Contains(method.Name) && method.ReturnType == typeof(bool));
        }
        private static bool Prefix(ref bool __result)
        {
            if (!isolated && !EditorIntegration.BlocksNativeExecution)
                return true;
            __result = false;
            return false;
        }
    }
}
