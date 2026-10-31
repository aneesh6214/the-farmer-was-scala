using System;
using System.Collections.Generic;
using HarmonyLib;

namespace FarmerScala.Editor;

/// <summary>Routes Scala members through the existing native completion popup.</summary>
[HarmonyPatch(typeof(CodeWindow), "GetSubWordList")]
internal static class ScalaCompletion
{
    private static bool reportedFailure;

    private static bool Prefix(CodeWindow __instance, string __0, ref List<string> __result)
    {
        if (!EditorIntegration.IsScalaWindow(__instance))
            return true;
        if (__instance.CodeInput.readOnly || !ScalaEditingContext.AllowsCompletion(__instance.CodeInput.text, __instance.CodeInput.stringPosition))
        {
            __result = new List<string>();
            return false;
        }
        try
        {
            if (ScalaAnalysis.TryMembers(__instance, __instance.CodeInput.stringPosition, out var members))
            {
                __result = members;
                return false;
            }
            __result = new List<string>();
            return false;
        }
        catch (Exception error)
        {
            if (!reportedFailure)
            {
                reportedFailure = true;
                Plugin.Instance?.LogInfo("Scala completion used native fallback: " + error.Message);
            }
        }
        __result = new List<string>();
        return false;
    }
}
