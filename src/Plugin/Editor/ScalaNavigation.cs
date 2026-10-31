using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FarmerScala.Editor;

/// <summary>Uses existing code windows and tooltips; never creates player source files.</summary>
internal static class ScalaNavigation
{
    private static readonly FieldInfo HoverCallback = AccessTools.Field(typeof(CodeWindow), "tooltipUpdateCallback");
    private static bool reportedFailure;
    private static long navigationRequest;
    private static CodeWindow? underlineWindow;
    private static string underlineSource = "";
    private static int underlineStart, underlineEnd;
    private static bool underlineShown;
    private static DateTime nextUnderlinePoll;

    private static bool ControlHeld => Application.isFocused &&
        (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));

    internal static void FocusLost()
    {
        // Unity can pause Update while unfocused, so the plugin forwards the
        // native focus event rather than waiting for another window frame.
        navigationRequest++;
        try
        {
            ClearUnderline();
        }
        catch (Exception error) { Report(error); }
    }

    private static void ClearUnderline()
    {
        var window = underlineWindow;
        bool clear = underlineShown;
        underlineWindow = null;
        underlineSource = "";
        underlineShown = false;
        if (clear && window != null)
            ScalaHighlighting.SetNavigationUnderline(window, -1, 0);
    }

    private static bool CurrentUnderlineSymbol()
    {
        return ControlHeld && underlineWindow != null && underlineWindow.isActiveAndEnabled &&
            underlineWindow.CodeInput.text == underlineSource &&
            PointerSymbol(underlineWindow, out int start, out int end, out _) &&
            start == underlineStart && end == underlineEnd;
    }

    private static void UpdateUnderline(CodeWindow window)
    {
        if (underlineWindow != null && !CurrentUnderlineSymbol())
            ClearUnderline();
        if (!ControlHeld || !PointerSymbol(window, out int start, out int end, out _))
            return;
        if (!ReferenceEquals(underlineWindow, window) || underlineStart != start || underlineEnd != end ||
            underlineSource != window.CodeInput.text)
        {
            ClearUnderline();
            underlineWindow = window;
            underlineSource = window.CodeInput.text;
            underlineStart = start;
            underlineEnd = end;
            nextUnderlinePoll = DateTime.MinValue;
        }
        if (DateTime.UtcNow < nextUnderlinePoll)
            return;
        nextUnderlinePoll = DateTime.UtcNow.AddMilliseconds(100);
        // Polling deduplicated exact-snapshot results avoids accumulating a
        // callback per frame. A discarded response will be requested again;
        // results from another source/save can never paint an underline.
        bool ready = ScalaAnalysis.TryResolve(window, end, out var result);
        bool show = ready && TryNavigationTarget(window, result, out _);
        if (show == underlineShown)
            return;
        underlineShown = show;
        ScalaHighlighting.SetNavigationUnderline(window, show ? start : -1, show ? end - start : 0);
    }

    private static void Report(Exception error)
    {
        if (reportedFailure)
            return;
        reportedFailure = true;
        Plugin.Instance?.LogInfo("Scala navigation/hover used native fallback: " + error.Message);
    }

    private static bool PointerSymbol(CodeWindow window, out int start, out int end, out int character)
    {
        start = end = 0;
        character = -1;
        if (window == null || !window.isActiveAndEnabled || !EditorIntegration.IsScalaWindow(window) || window.workspace.GetOpenWindow((Vector2)Input.mousePosition) != window.GetComponent<Window>()
            || !window.IsPointerOverCodeInput())
            return false;
        // A native menu can cover the workspace without being in openWindows.
        // Respect the same actual pointer target used by the tooltip owner.
        if (EventSystem.current?.currentInputModule is CustomStandaloneInputModule module)
        {
            var hit = module.GetPointerData()?.pointerCurrentRaycast.gameObject;
            if (hit == null || hit.GetComponentInParent<CodeWindow>() != window)
                return false;
        }
        var input = window.CodeInput;
        character = TMP_TextUtilities.FindIntersectingCharacter(input.textComponent, Input.mousePosition, window.workspace.uiCam, visibleOnly: true);
        var info = input.textComponent.textInfo;
        if (character < 0 || character >= info.characterCount || character >= info.characterInfo.Length)
            return false;
        return ScalaSymbolLocation.TryIdentifier(input.text, info.characterInfo[character].index, out start, out end);
    }

    private static bool TryNavigationTarget(CodeWindow origin, AnalysisResolution result, out CodeWindow? target)
    {
        target = null;
        if (origin == null || !EditorIntegration.IsScalaWindow(origin) || result.IsGameBuiltin || result.Definition == null)
            return false;
        var definition = result.Definition;
        if (definition.IsLibrary)
        {
            if (!ReadOnlyLibraryWindows.CanOpen(definition))
                return false;
            target = ReadOnlyLibraryWindows.Find(origin.workspace, definition);
            return true;
        }
        if (!origin.workspace.codeWindows.TryGetValue(definition.File, out var found) || found == null || !EditorIntegration.IsScalaWindow(found))
            return false;
        if (definition.Offset < 0 || definition.Offset > found.CodeInput.text.Length)
            return false;
        target = found;
        return true;
    }

    private static void Navigate(CodeWindow origin, AnalysisResolution result)
    {
        if (!TryNavigationTarget(origin, result, out var target))
            return;
        ClearUnderline();
        var definition = result.Definition!;
        if (definition.IsLibrary && target == null)
            target = ReadOnlyLibraryWindows.Open(origin.workspace, definition);
        var input = target!.CodeInput;
        var native = target.GetComponent<Window>();
        // Minimized native code windows retain their source. Show their existing
        // instance rather than duplicating a module or touching player files.
        native.SetMinmized(false);
        native.MoveToFront();
        target.workspace.activeWindow = native;
        if (target.workspace.codeCompleter.IsOpen)
            target.workspace.codeCompleter.Close();
        input.ActivateInputField();
        input.stringPosition = definition.Offset;
        input.selectionStringAnchorPosition = definition.Offset;
        input.selectionStringFocusPosition = definition.Offset;
        input.ForceLabelUpdate();
        input.textComponent.ForceMeshUpdate(true, true);
        int glyph = CharacterAt(input.textComponent.textInfo, definition.Offset);
        if (glyph < 0)
        {
            target.workspace.MoveCameraTo(native, alwaysMoveWindow: true);
            return;
        }
        // Native scrolling and camera APIs expect a TMP glyph index, despite
        // ScrollToStringPosition's historical parameter name.
        target.ScrollToStringPosition(glyph);
        target.workspace.MoveCameraTo(native, alwaysMoveWindow: true, charIndex: glyph, inputField: input);
    }

    [HarmonyPatch]
    private static class WindowGone
    {
        private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CodeWindow), "OnDisable");
            // Supported Core has OnDisable but no OnDestroy; Unity invokes
            // OnDisable before destroying an active window. Never yield a
            // missing Harmony target, which would disable plugin startup.
            var destroy = AccessTools.Method(typeof(CodeWindow), "OnDestroy");
            if (destroy != null)
                yield return destroy;
        }
        private static void Prefix(CodeWindow __instance)
        {
            try
            {
                if (ReferenceEquals(underlineWindow, __instance))
                    ClearUnderline();
            }
            catch (Exception error) { Report(error); }
        }
    }

    private static int CharacterAt(TMP_TextInfo info, int sourceOffset)
    {
        int count = Math.Min(info.characterCount, info.characterInfo.Length);
        if (count == 0)
            return -1;
        int character = 0;
        while (character + 1 < count && info.characterInfo[character + 1].index <= sourceOffset)
            character++;
        return character;
    }

    [HarmonyPatch(typeof(CodeWindow), "Update")]
    private static class Click
    {
        private static void Postfix(CodeWindow __instance)
        {
            try
            {
                UpdateUnderline(__instance);
                if (!Input.GetKeyDown(KeyCode.Mouse0) || !ControlHeld)
                    return;
                if (!PointerSymbol(__instance, out _, out int end, out _))
                    return;
                // Resolution is asynchronous. A newer Ctrl-click, including one
                // on a built-in, supersedes earlier navigation across all windows.
                long request = ++navigationRequest;
                ScalaAnalysis.RequestResolve(__instance, end, result =>
                {
                    if (__instance == null || request != navigationRequest)
                        return;
                    try
                    {
                        Navigate(__instance, result);
                    }
                    catch (Exception error) { Report(error); }
                });
            }
            catch (Exception error) { Report(error); }
        }
    }

    [HarmonyPatch(typeof(CodeWindow), nameof(CodeWindow.GetTooltipInfo))]
    private static class Hover
    {
        private static bool Prefix(CodeWindow __instance, Action __0, ref TooltipInfo __result)
        {
            try
            {
                if (ScalaDiagnostics.HoveringError(__instance))
                {
                    __result = null!;
                    return false;
                }
                if (!PointerSymbol(__instance, out int start, out int end, out _))
                    return true;
                HoverCallback.SetValue(__instance, __0);
                bool ready = ScalaAnalysis.TryResolve(__instance, end, out var result);
                string word = __instance.CodeInput.text.Substring(start, end - start).Trim('`');
                if (ScalaCatalogReference.TryFind(__instance.CodeInput.text, start, end, out string catalogName, out int catalogStart)
                    && catalogName.Contains(".")
                    && (result.IsGameBuiltin || ScalaAnalysis.IsGameCatalogValueReference(__instance, end)))
                {
                    // Native cached word indices can lag behind a different
                    // expression/argument. Resolve this pointer's catalog text
                    // directly, with the same native item/entity documentation.
                    var native = TooltipUtils.GetWordTooltip(catalogName, __instance);
                    if (native != null)
                    {
                        int catalogGlyph = CharacterAt(__instance.CodeInput.textComponent.textInfo, catalogStart);
                        if (catalogGlyph >= 0)
                        {
                            native.fixedPosition = __instance.CodeInput.transform.TransformPoint(__instance.CodeInput.textComponent.textInfo.characterInfo[catalogGlyph].bottomLeft)
                                + (Vector3)BlinkManager.GetCodeOffset(__instance.CodeInput.GetComponent<RectTransform>());
                            native.anchor = TooltipInfo.Anchor.BottomRight;
                            native.delay = .3f;
                        }
                    }
                    if (native != null)
                    {
                        __result = native;
                        return false;
                    }
                }
                string? docs = result.IsGameBuiltin ? GameDocs(result.GameFunctionName ?? DeclaredFunction(result.HoverText) ?? word) : null;
                if (!ready || result.IsGameBuiltin && string.IsNullOrWhiteSpace(result.HoverText))
                {
                    string source = __instance.CodeInput.text;
                    // Refresh through the game's own tooltip owner when analysis
                    // completes; changing the hovered symbol invalidates this request.
                    ScalaAnalysis.RequestCompilerResolve(__instance, end, resolved =>
                    {
                        if (__instance == null || __instance.CodeInput.text != source ||
                            !PointerSymbol(__instance, out int currentStart, out int currentEnd, out _) || currentStart != start || currentEnd != end)
                            return;
                        if (ReferenceEquals(HoverCallback.GetValue(__instance), __0))
                            __0();
                    });
                    // Game actions use exactly the same hover renderer as
                    // project and library symbols; await SDK Scaladoc instead
                    // of briefly flashing the old Python-oriented tooltip.
                    if (result.IsGameBuiltin)
                    {
                        __result = null!;
                        return false;
                    }
                    return true;
                }
                if (string.IsNullOrWhiteSpace(result.HoverText))
                    return true;
                var input = __instance.CodeInput;
                var info = input.textComponent.textInfo;
                int character = CharacterAt(info, start);
                if (character < 0)
                    return true;
                Vector3 anchor = input.transform.TransformPoint(info.characterInfo[character].bottomLeft)
                    + (Vector3)BlinkManager.GetCodeOffset(input.GetComponent<RectTransform>());
                string formatted = ScalaHoverFormatter.Format(result.HoverText!,
                    ScalaHighlighting.CreatePalette(ThemeManager.Inst.Theme), projectSymbol: !result.IsGameBuiltin, symbolKind: result.SymbolKind);
                __result = new TooltipInfo(formatted, 0.3f, anchor, TooltipInfo.Anchor.BottomRight, docs);
                return false;
            }
            catch (Exception error) { Report(error); return true; }
        }
    }

    private static string? GameDocs(string name)
    {
        if (name == "game_print")
            name = "print";
        return BuiltinFunctions.Functions.ContainsKey(name) ? "functions/" + name : null;
    }
    private static string? DeclaredFunction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var tokens = ScalaLexer.Lex(text!);
        for (int i = 0; i + 1 < tokens.Count; i++)
            if (tokens[i].Text == "def" && tokens[i + 1].Identifier)
                return tokens[i + 1].Text.Trim('`');
        return null;
    }
}
