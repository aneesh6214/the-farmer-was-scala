using System;
using System.Collections.Generic;
using System.Reflection;
using FarmerScala.Editor;
using HarmonyLib;
using UnityEngine;

namespace FarmerScala;

internal static class ScalaSettings
{
    internal const string Unsupported = "Sorry! This is not yet supported.";
    private static readonly HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal) {
        "code highlights", "color theme", "error forgot call", "print warnings"
    };
    private static readonly FieldInfo Name = AccessTools.Field(typeof(OptionUI), "optionName");
    internal static bool Disabled(OptionUI? option) => option != null && EditorIntegration.Enabled &&
        Names.Contains((string)Name.GetValue(option));

    [HarmonyPatch(typeof(OptionUI), nameof(OptionUI.Setup))]
    private static class Setup
    {
        private static void Postfix(OptionUI __instance, OptionSO optionSO)
        {
            if (!Names.Contains(optionSO.optionName))
                return;
            var row = __instance.GetComponent<ScalaUnsupportedSetting>() ?? __instance.gameObject.AddComponent<ScalaUnsupportedSetting>();
            row.Initialize(__instance);
        }
    }

    [HarmonyPatch(typeof(OptionUI), nameof(OptionUI.GetTooltipInfo))]
    private static class RowTooltip
    {
        private static bool Prefix(OptionUI __instance, ref TooltipInfo __result)
        {
            if (!Disabled(__instance))
                return true;
            __result = new TooltipInfo(Unsupported, .3f);
            return false;
        }
    }

    [HarmonyPatch(typeof(ColoredButton), nameof(ColoredButton.GetTooltipInfo))]
    private static class ButtonTooltip
    {
        private static bool Prefix(ColoredButton __instance, ref TooltipInfo __result)
        {
            if (!Disabled(__instance.GetComponentInParent<OptionUI>()))
                return true;
            __result = new TooltipInfo(Unsupported, .3f);
            return false;
        }
    }
}

/// <summary>Native disabled-control visuals; leave raycasts enabled for tooltips.</summary>
internal sealed class ScalaUnsupportedSetting : MonoBehaviour
{
    private OptionUI option = null!;
    private CanvasGroup group = null!;
    private float alpha;
    private bool interactable, ready, disabled;
    internal void Initialize(OptionUI row)
    {
        if (!ready)
        {
            option = row;
            group = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
            alpha = group.alpha;
            interactable = group.interactable;
            ready = true;
        }
        Refresh();
    }
    private void LateUpdate()
    {
        if (ready)
            Refresh();
    }
    private void Refresh()
    {
        bool next = ScalaSettings.Disabled(option);
        if (next == disabled)
            return;
        disabled = next;
        group.interactable = next ? false : interactable;
        group.alpha = next ? alpha * .55f : alpha;
    }
    private void OnDisable()
    {
        if (!ready || group == null)
            return;
        group.interactable = interactable;
        group.alpha = alpha;
        disabled = false;
    }
}
