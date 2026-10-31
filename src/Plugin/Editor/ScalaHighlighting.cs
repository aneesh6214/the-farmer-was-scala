using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;

namespace FarmerScala.Editor;

/// <summary>Changes only the colored overlay of the current Scala code window.</summary>
internal static class ScalaHighlighting
{
    [ThreadStatic] private static CodeWindow? renderingWindow;
    private static bool reportedFlags;
    private static bool reportedFailure;
    private static readonly FieldInfo CodeText = AccessTools.Field(typeof(CodeWindow), "codeText");
    internal static ScalaSyntaxPalette CreatePalette(ColorTheme theme) => ScalaSyntaxPalette.Create(
        theme.code.builtin, theme.code.function, theme.ui.search_match, theme.ui.search_match_current);
    private sealed class SearchState
    {
        public string Word = ""; public int Index = -1;
    }
    private static readonly ConditionalWeakTable<CodeWindow, SearchState> searches = new ConditionalWeakTable<CodeWindow, SearchState>();
    private sealed class NavigationState
    {
        public string Source = "", Project = "";
        public int Start = -1, Length;
    }
    private static readonly ConditionalWeakTable<CodeWindow, NavigationState> navigation = new ConditionalWeakTable<CodeWindow, NavigationState>();
    internal static void SetNavigationUnderline(CodeWindow window, int start, int length)
    {
        if (window == null)
            return;
        try
        {
            var state = navigation.GetOrCreateValue(window);
            string source = window.CodeInput.text;
            bool valid = start >= 0 && length > 0 && start <= source.Length - length;
            if (!valid)
            {
                start = -1;
                length = 0;
            }
            string project = EditorIntegration.ProjectDirectory;
            if (state.Start == start && state.Length == length &&
                (start < 0 || state.Source == source && state.Project == project))
                return;
            state.Start = start;
            state.Length = length;
            state.Source = source;
            state.Project = project;
            if (EditorIntegration.IsScalaWindow(window))
                Refresh(window);
        }
        catch (Exception error) { ReportFailure(error); }
    }
    internal static void Refresh(CodeWindow window)
    {
        var state = searches.GetOrCreateValue(window);
        window.UpdateSearch(state.Word, state.Index);
    }
    [HarmonyPatch(typeof(CodeWindow), nameof(CodeWindow.UpdateSearch))]
    private static class RememberSearch
    {
        private static void Prefix(CodeWindow __instance, string __0, int __1)
        {
            var state = searches.GetOrCreateValue(__instance);
            state.Word = __0;
            state.Index = __1;
        }
    }

    private static void ReportFailure(Exception error)
    {
        if (reportedFailure)
            return;
        reportedFailure = true;
        Plugin.Instance?.LogInfo("Scala highlighting fell back to native colors: " + error.GetType().Name + ": " + error.Message);
    }

    [HarmonyPatch]
    private static class WindowRenderingScope
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CodeWindow), "UpdateTokens");
            yield return AccessTools.Method(typeof(CodeWindow), "OnThemeChanged");
            yield return AccessTools.Method(typeof(CodeWindow), nameof(CodeWindow.UpdateSearch));
        }

        private static void Prefix(CodeWindow __instance, out CodeWindow? __state)
        {
            __state = renderingWindow;
            renderingWindow = EditorIntegration.IsScalaWindow(__instance) ? __instance : null;
        }

        private static Exception? Finalizer(Exception? __exception, CodeWindow? __state)
        {
            renderingWindow = __state;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(CodeUtilities), nameof(CodeUtilities.SyntaxColor2))]
    private static class ScalaColors
    {
        private static bool Prefix(string __0, ColorTheme __1, string __2, int __3, ref string __result)
        {
            var window = renderingWindow;
            if (window == null || !EditorIntegration.IsScalaWindow(window))
                return true;
            try
            {
                bool inputRichText = window.CodeInput.richText;
                var link = navigation.GetOrCreateValue(window);
                bool currentLink = link.Source == __0 && link.Project == EditorIntegration.ProjectDirectory;
                __result = ScalaSyntaxHighlighter.Colorize(__0, CreatePalette(__1), __2, __3,
                    escapeMarkup: !inputRichText, semanticSpans: ScalaAnalysis.Spans(window),
                    navigationStart: currentLink ? link.Start : -1, navigationLength: currentLink ? link.Length : 0);
                if (!reportedFlags)
                {
                    reportedFlags = true;
                    var overlay = CodeText.GetValue(window) as TMP_Text;
                    Plugin.Instance?.LogInfo("Scala syntax coloring active; source/input unchanged. inputRichText="
                        + inputRichText + ", inputParseControlCharacters=" + window.CodeInput.textComponent.parseCtrlCharacters
                        + ", overlayParseControlCharacters=" + overlay?.parseCtrlCharacters + ".");
                }
                return false;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(EditorIntegration), "AfterLoad")]
    private static class SaveRenderingRefresh
    {
        private static Exception? Finalizer(Exception? __exception)
        {
            // Saver.Load may have painted an identical buffer while loading
            // suppressed Scala rendering. An unchanged enabled flag alone
            // therefore cannot establish that a loaded overlay is current.
            // Repaint after save loading settles; this does not edit contents.
            try
            {
                var main = MainSim.Inst;
                if (main != null)
                    foreach (var window in main.workspace.codeWindows.Values.ToArray())
                        window.UpdateSearch("");
            }
            catch (Exception error) { ReportFailure(error); }
            return __exception;
        }
    }
}
