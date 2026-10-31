using System;
using FarmerScala.Editor;
using HarmonyLib;

namespace FarmerScala;

// Native simulation startup asks the selected CodeWindow to Parse on the game thread.
// Only that call is redirected to an isolated Scala program node.
[HarmonyPatch(typeof(MainSim), "StartLeaderboard")]
internal static class SimulationParseScope
{
    [ThreadStatic] private static int depth;
    public static bool Active => depth > 0;
    private static bool Prefix(MainSim.LeaderboardStartArgs startArgs, ref bool __state)
    {
        if (!EditorIntegration.Enabled)
            return true;
        if (startArgs.leaderboardType != LeaderboardType.simulation)
            return false;
        if (Plugin.Instance?.Host?.IsRunning != true)
            return false;
        depth++;
        __state = true;
        return true;
    }
    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__state)
            depth--;
        return __exception;
    }
}

[HarmonyPatch(typeof(Simulation), nameof(Simulation.EvaluateName))]
internal static class ScalaTooltipGuard
{
    private static bool Prefix(ref IPyObject? value, ref bool __result)
    {
        if (!EditorIntegration.Enabled)
            return true;
        value = null;
        __result = false;
        return false;
    }
}
