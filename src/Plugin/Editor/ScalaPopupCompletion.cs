using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace FarmerScala.Editor;

/// <summary>Fills the existing popup without changing its keyboard/selection UI.</summary>
[HarmonyPatch(typeof(CodeCompleter), nameof(CodeCompleter.Open))]
internal static class ScalaPopupCompletion
{
    private static readonly FieldInfo Owner = AccessTools.Field(typeof(CodeCompleter), "func");
    private static readonly FieldInfo Options = AccessTools.Field(typeof(CodeCompleter), "optionStrings");
    private static readonly MethodInfo UpdateOptions = AccessTools.Method(typeof(CodeCompleter), "UpdateOptions");
    private sealed class Revision
    {
        public long Value;
        public Dictionary<string, int> Order = new Dictionary<string, int>(StringComparer.Ordinal);
    }
    private static readonly ConditionalWeakTable<CodeCompleter, Revision> revisions = new ConditionalWeakTable<CodeCompleter, Revision>();
    private static long revision;
    private static bool reopening;
    private static bool Prefix(CodeCompleter __instance, CodeWindow func, int startStringIndex, ref List<string> optionStrings)
    {
        if (!EditorIntegration.IsScalaWindow(func))
            return true;
        if (!func.CodeInput.readOnly && ScalaEditingContext.AllowsCompletion(func.CodeInput.text, func.CodeInput.stringPosition))
        {
            if (!reopening && !(startStringIndex > 0 && func.CodeInput.text[startStringIndex - 1] == '.'))
                optionStrings = ScalaAnalysis.NativeSuggestions(func, func.CodeInput.stringPosition, optionStrings);
            SetOrder(__instance, optionStrings);
            return true;
        }
        if (__instance.IsOpen)
            __instance.Close();
        return false;
    }
    private static void Postfix(CodeCompleter __instance, Vector3 pos, string compareString, int startStringIndex, CodeWindow func)
    {
        if (reopening || !EditorIntegration.IsScalaWindow(func) || func.CodeInput.readOnly ||
            !ScalaEditingContext.AllowsCompletion(func.CodeInput.text, func.CodeInput.stringPosition))
            return;
        long token = ++revision;
        revisions.GetOrCreateValue(__instance).Value = token;
        Schedule(__instance, func, pos, compareString, startStringIndex, token);
    }
    private static void Schedule(CodeCompleter popup, CodeWindow window, Vector3 pos, string compare, int start, long token)
    {
        string source = window.CodeInput.text;
        int cursor = window.CodeInput.stringPosition;
        // Preserve native catalogs and their unlock filtering.
        if (!ScalaEditingContext.AllowsCompletion(source, cursor))
            return;
        ScalaAnalysis.Complete(window, cursor, names =>
        {
            if (popup == null || window == null || !revisions.TryGetValue(popup, out var active) || active.Value != token ||
                !window.CodeInput.isFocused || window.CodeInput.readOnly || window.CodeInput.text != source || window.CodeInput.stringPosition != cursor ||
                !ScalaEditingContext.AllowsCompletion(source, cursor))
                return;
            if (popup.IsOpen && !ReferenceEquals(Owner.GetValue(popup), window))
                return;
            reopening = true;
            try
            {
                popup.Open(pos, MergeNative(popup, source, start, names), compare, start, window);
            }
            finally { reopening = false; }
        });
    }
    private static List<string> MergeNative(CodeCompleter popup, string source, int start, List<string> names)
    {
        // A resolved receiver supplies its own members. At global scope, keep
        // native game APIs alongside project declarations and Scala keywords.
        if (ScalaModuleCompletion.IsImport(source, start))
            return names;
        if (start > 0 && start <= source.Length && (source[start - 1] == '.' || source[start - 1] == '@'))
            return names;
        var native = Options.GetValue(popup) as List<string> ?? new List<string>();
        var fallback = ScalaCompletionUnlocks.Native(native.Where(ScalaSyntaxHighlighter.IsBuiltinName)).Concat(ScalaLexer.Keywords)
            .Concat(new[] { "List", "Vector", "Seq", "Map", "Set", "Option", "Some", "None", "Either", "Left", "Right", "println" })
            .OrderBy(n => n, StringComparer.Ordinal);
        return names.Concat(fallback).Distinct(StringComparer.Ordinal).ToList();
    }
    private static void SetOrder(CodeCompleter popup, List<string> names)
    {
        var order = revisions.GetOrCreateValue(popup).Order;
        order.Clear();
        foreach (string name in names) if (!order.ContainsKey(name)) order.Add(name, order.Count);
    }
    // Native UpdateOptions re-sorts by prefix match and then alphabetically.
    // Preserve its matching behavior, using provider relevance before that tie-break.
    [HarmonyPatch(typeof(CodeCompleter), "MatchPriority")]
    private static class Relevance
    {
        private static void Postfix(CodeCompleter __instance, string s, ref int __result)
        {
            if (Owner.GetValue(__instance) is CodeWindow window && EditorIntegration.IsScalaWindow(window) &&
                revisions.TryGetValue(__instance, out var state) && state.Order.TryGetValue(s, out int rank))
                __result = __result * (state.Order.Count + 1) + rank;
        }
    }
    [HarmonyPatch(typeof(CodeCompleter), nameof(CodeCompleter.Close))]
    private static class Closed
    {
        private static void Prefix(CodeCompleter __instance)
        {
            revisions.Remove(__instance);
        }
    }
    [HarmonyPatch]
    private static class Typed
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CodeCompleter), nameof(CodeCompleter.UpdateString));
            yield return AccessTools.Method(typeof(CodeCompleter), nameof(CodeCompleter.SetTypedString));
            yield return AccessTools.Method(typeof(CodeCompleter), nameof(CodeCompleter.Backspace));
        }
        private static void Postfix(CodeCompleter __instance)
        {
            if (!__instance.IsOpen || !(Owner.GetValue(__instance) is CodeWindow window) || !EditorIntegration.IsScalaWindow(window))
                return;
            if (window.CodeInput.readOnly || !ScalaEditingContext.AllowsCompletion(window.CodeInput.text, window.CodeInput.stringPosition))
            {
                __instance.Close();
                return;
            }
            // Open's anchor already includes native popup translation, so use the
            // options field for refresh while typing instead of moving the popup.
            string source = window.CodeInput.text;
            int cursor = window.CodeInput.stringPosition;
            long token = ++revision;
            revisions.GetOrCreateValue(__instance).Value = token;
            ScalaAnalysis.Complete(window, cursor, names =>
            {
                if (__instance == null || !__instance.IsOpen || !revisions.TryGetValue(__instance, out var active) || active.Value != token ||
                    !ReferenceEquals(Owner.GetValue(__instance), window) || window.CodeInput.text != source || window.CodeInput.stringPosition != cursor)
                    return;
                var ordered = MergeNative(__instance, source, __instance.startStringIndex, names);
                SetOrder(__instance, ordered);
                Options.SetValue(__instance, ordered);
                UpdateOptions.Invoke(__instance, null);
            });
        }
    }
}
