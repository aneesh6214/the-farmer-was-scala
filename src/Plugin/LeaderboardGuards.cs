using System.Collections.Generic;
using System.Reflection;
using FarmerScala.Editor;
using HarmonyLib;

namespace FarmerScala;

[HarmonyPatch(typeof(MainSim), nameof(MainSim.ScheduleLeaderboardStart))]
internal static class RankedRunGuard
{
    private static bool Prefix(MainSim.LeaderboardStartArgs startArgs)
    {
        if (!EditorIntegration.BlocksNativeExecution || startArgs.leaderboardType == LeaderboardType.simulation)
            return true;
        Plugin.Instance?.LogInfo("Leaderboards are disabled in Scala mode.");
        return false;
    }
}

// Also block asynchronous callbacks arriving after a Scala save was loaded.
[HarmonyPatch]
internal static class SteamScoreGuard
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SteamLeaderboard), "LoadLeaderboard");
        yield return AccessTools.Method(typeof(SteamLeaderboard), "Load");
        yield return AccessTools.Method(typeof(SteamLeaderboard), "OnLeaderboardFindResult");
        yield return AccessTools.Method(typeof(SteamLeaderboard), "OnLeaderboardUploadResult");
        yield return AccessTools.Method(typeof(SteamLeaderboard), "OnPlayerEntryLoaded");
        yield return AccessTools.Method(typeof(SteamLeaderboard), "OnTopEntriesLoaded");
        yield return AccessTools.Method(typeof(Leaderboard), nameof(Leaderboard.FillLeaderboard));
        yield return AccessTools.Method(typeof(Leaderboard), "OnPlayerLoaded");
        yield return AccessTools.Method(typeof(Leaderboard), "OnEntriesLoaded");
        yield return AccessTools.Method(typeof(LeaderboardManager), "StartLeaderboardRun");
    }
    private static bool Prefix(MethodBase __originalMethod) => !EditorIntegration.BlocksNativeExecution ||
        (__originalMethod.DeclaringType == typeof(LeaderboardManager) && SimulationParseScope.Active);
}

// Ranked completion normally replaces the workspace with a score screen. Suppress
// that UI too, while returning to the stored ordinary farm and removing its overlay.
// Native simulations finish through a separate path and never call this method.
[HarmonyPatch(typeof(LeaderboardManager), nameof(LeaderboardManager.StopLeaderboardRun))]
internal static class RankedResultGuard
{
    private static bool Prefix(LeaderboardManager __instance)
    {
        if (!EditorIntegration.BlocksNativeExecution)
            return true;
        __instance.RemoveOverlay();
        var main = MainSim.Inst;
        if (main != null && main.MightBeSimulating())
            main.RestoreMainSim();
        Plugin.Instance?.LogInfo("Leaderboards are disabled in Scala mode.");
        return false;
    }
}

// Clear any residual ranked route after loading a Scala save; ordinary simulate is kept.
[HarmonyPatch(typeof(EditorIntegration), "AfterLoad")]
internal static class RankedSaveLoadGuard
{
    private static readonly FieldInfo CurrentSimulation = AccessTools.Field(typeof(MainSim), "sim");
    private static readonly FieldInfo CancellationFlag = AccessTools.Field(typeof(MainSim), "leaderboardCancelled");
    private static void Postfix()
    {
        if (!EditorIntegration.BlocksNativeExecution)
            return;
        var main = MainSim.Inst;
        if (main == null)
            return;
        var sim = CurrentSimulation.GetValue(main) as Simulation;
        if (sim == null || sim.leaderboardType == LeaderboardType.none || sim.leaderboardType == LeaderboardType.simulation)
            return;
        main.StopMainExecution();
        main.leaderboardManager.RemoveOverlay();
        if (main.leaderboardManager.IsLeaderBoardScreenOpen)
            main.leaderboardManager.OkPressed();
        else if (main.MightBeSimulating())
            main.RestoreMainSim();
        ResetRankedBookkeeping(main, sim.leaderboardType);
    }

    internal static void ResetRankedBookkeeping(MainSim main, LeaderboardType discardedType)
    {
        if (!EditorIntegration.BlocksNativeExecution || discardedType == LeaderboardType.none || discardedType == LeaderboardType.simulation)
            return;
        // Stopping the ranked execution sets this flag. We bypass its normal
        // completion path when restoring, so it must not cancel a future vanilla
        // ranked run or carry ranked speedup/averages into the ordinary farm.
        CancellationFlag.SetValue(main, false);
        main.totalLeaderboardTime = new Duration(0L);
        main.numLeaderboardRuns = 0;
        main.TimeFactor = 1.0;
    }
}
