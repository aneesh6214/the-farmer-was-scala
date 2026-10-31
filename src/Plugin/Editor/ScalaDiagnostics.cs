using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FarmerScala.Editor;

/// <summary>Exact-revision compiler errors, rendered inside native clipped editor surfaces.</summary>
internal static class ScalaDiagnostics
{
    private sealed class State
    {
        internal CodeWindow Window = null!;
        internal string Source = "";
        internal List<AnalysisDiagnostic> Errors = new List<AnalysisDiagnostic>();
        internal DiagnosticUnderlineGraphic Underline = null!;
        internal ErrorMessage? Popup;
        internal AnalysisDiagnostic? Hover;
        internal int HoverGlyph = -1;
        internal double HoverAfter;
        internal float Width, Height, Font;
        internal Color Color;
    }
    private static readonly Dictionary<CodeWindow, State> states = new Dictionary<CodeWindow, State>();
    private static readonly DiagnosticDelay delay = new DiagnosticDelay();
    private static readonly FieldInfo NativeError = AccessTools.Field(typeof(CodeWindow), "errorMessage");
    private static long visibleVersion = long.MinValue;
    private static long observedVersion = long.MinValue;
    private static bool warned;

    internal static void Tick()
    {
        try
        {
            var main = MainSim.Inst;
            var first = main?.workspace?.codeWindows.Values.FirstOrDefault(w => EditorIntegration.IsScalaWindow(w));
            if (first == null)
            {
                Clear();
                return;
            }
            foreach (var item in states.Keys.Where(w => w == null || w.workspace != first.workspace || !first.workspace.codeWindows.Values.Contains(w)).ToArray())
                Remove(item);
            long version = ScalaAnalysis.SourceVersion;
            if (observedVersion != version)
            {
                observedVersion = version;
                foreach (var state in states.Values)
                {
                    state.Errors.Clear();
                    state.Underline.SetVerticesDirty();
                    ClosePopup(state);
                }
            }
            bool eligible = delay.Observe(version, Time.unscaledTime);
            if (eligible && visibleVersion != version && ScalaAnalysis.TryDiagnostics(first, out var diagnostics) && version == ScalaAnalysis.SourceVersion)
            {
                foreach (var window in first.workspace.codeWindows.Values)
                {
                    var state = Get(window);
                    state.Source = window.CodeInput.text;
                    state.Errors = diagnostics.Where(d => d.File == window.fileName && d.Severity == "error" &&
                        DiagnosticRanges.Normalize(state.Source, d.Start, d.Length, out _, out _)).Take(200).ToList();
                    state.Underline.SetVerticesDirty();
                }
                visibleVersion = version;
            }
            foreach (var state in states.Values)
                Update(state);
        }
        catch (Exception error)
        {
            if (!warned)
            {
                warned = true;
                Plugin.Instance?.LogInfo("Scala editor diagnostics: " + error.Message);
            }
        }
    }

    private static State Get(CodeWindow window)
    {
        if (states.TryGetValue(window, out var state))
            return state;
        state = new State { Window = window };
        var graphicObject = new GameObject("Scala compiler diagnostics", typeof(RectTransform), typeof(CanvasRenderer), typeof(DiagnosticUnderlineGraphic));
        var parent = window.CodeInput.textComponent.rectTransform;
        var rect = graphicObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = parent.pivot;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        var graphic = graphicObject.GetComponent<DiagnosticUnderlineGraphic>();
        graphic.raycastTarget = false;
        graphic.Build = mesh => Draw(state, mesh);
        state.Underline = graphic;
        states.Add(window, state);
        return state;
    }

    private static void Update(State state)
    {
        var input = state.Window.CodeInput;
        var rect = input.textComponent.rectTransform.rect;
        Color color = ThemeManager.Inst.Theme.code.ErrorBorderColor;
        if (state.Width != rect.width || state.Height != rect.height || state.Font != input.textComponent.fontSize || state.Color != color)
        {
            state.Width = rect.width;
            state.Height = rect.height;
            state.Font = input.textComponent.fontSize;
            state.Color = color;
            state.Underline.SetVerticesDirty();
            ClosePopup(state);
        }
        var error = Hit(state, out int glyph);
        if (!ReferenceEquals(state.Hover, error))
        {
            ClosePopup(state);
            state.Hover = error;
            state.HoverAfter = Time.unscaledTime + .35;
        }
        if (error == null || Time.unscaledTime < state.HoverAfter || glyph < 0)
            return;
        if (state.Popup != null && state.Popup.gameObject.activeSelf && state.HoverGlyph == glyph)
            return;
        if (state.Popup == null)
        {
            state.Popup = UnityEngine.Object.Instantiate((ErrorMessage)NativeError.GetValue(state.Window), input.transform);
            state.Popup.gameObject.name = "Scala diagnostic explanation";
            state.Popup.gameObject.SetActive(false);
            // A tooltip must not replace the editor as the pointer owner.
            foreach (var graphic in state.Popup.GetComponentsInChildren<Graphic>(true))
                graphic.raycastTarget = false;
            if (AccessTools.Field(typeof(ErrorMessage), "errorText").GetValue(state.Popup) is TMP_Text message)
                message.richText = false;
        }
        var info = input.textComponent.textInfo;
        int last = glyph;
        DiagnosticRanges.Normalize(state.Source, error.Start, error.Length, out _, out int end);
        while (last + 1 < info.characterCount && info.characterInfo[last + 1].index < end && info.characterInfo[last + 1].lineNumber == info.characterInfo[glyph].lineNumber)
            last++;
        Vector3 offset = BlinkManager.GetCodeOffset(input.GetComponent<RectTransform>());
        state.Window.workspace.tooltip?.CloseTooltip();
        state.HoverGlyph = glyph;
        state.Popup.ShowError(error.Message, input.transform.TransformPoint(info.characterInfo[glyph].bottomLeft) + offset,
            input.transform.TransformPoint(info.characterInfo[last].bottomRight) + offset);
    }

    internal static bool HoveringError(CodeWindow window) => states.TryGetValue(window, out var state) && Hit(state, out _) != null;
    private static AnalysisDiagnostic? Hit(State state, out int glyph)
    {
        glyph = -1;
        var window = state.Window;
        if (!Application.isFocused || window == null || !window.isActiveAndEnabled || state.Source != window.CodeInput.text ||
            window.workspace.GetOpenWindow((Vector2)Input.mousePosition) != window.GetComponent<Window>() || !window.IsPointerOverCodeInput())
            return null;
        if (EventSystem.current?.currentInputModule is CustomStandaloneInputModule module)
        {
            var hit = module.GetPointerData()?.pointerCurrentRaycast.gameObject;
            if (hit == null || hit.GetComponentInParent<CodeWindow>() != window)
                return null;
        }
        var text = window.CodeInput.textComponent;
        int at = TMP_TextUtilities.FindIntersectingCharacter(text, Input.mousePosition, window.workspace.uiCam, true);
        if (at < 0)
        {
            // The wave sits just below a glyph's descender. Its hit area must
            // include that narrow band as well as the character itself.
            at = TMP_TextUtilities.FindNearestCharacter(text, Input.mousePosition, window.workspace.uiCam, false);
            if (at >= 0 && at < text.textInfo.characterCount &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(text.rectTransform, Input.mousePosition, window.workspace.uiCam, out var point))
            {
                var nearest = text.textInfo.characterInfo[at];
                if (point.x < nearest.origin || point.x > nearest.xAdvance || point.y < nearest.descender - 8 || point.y > nearest.ascender)
                    at = -1;
            }
            else
                at = -1;
        }
        if (at < 0 || at >= text.textInfo.characterCount)
            return null;
        int offset = text.textInfo.characterInfo[at].index;
        foreach (var diagnostic in state.Errors)
            if (DiagnosticRanges.Normalize(state.Source, diagnostic.Start, diagnostic.Length, out int start, out int end) && offset >= start && offset < end)
            {
                glyph = at;
                while (glyph > 0 && text.textInfo.characterInfo[glyph - 1].index >= start &&
                    text.textInfo.characterInfo[glyph - 1].lineNumber == text.textInfo.characterInfo[at].lineNumber)
                    glyph--;
                return diagnostic;
            }
        return null;
    }

    private static void Draw(State state, VertexHelper mesh)
    {
        mesh.Clear();
        if (state.Window == null || state.Source != state.Window.CodeInput.text)
            return;
        var info = state.Window.CodeInput.textComponent.textInfo;
        Color color = ThemeManager.Inst.Theme.code.ErrorBorderColor;
        float wave = Math.Max(3.5f, state.Window.CodeInput.textComponent.fontSize * .13f);
        float amplitude = Math.Max(2.4f, state.Window.CodeInput.textComponent.fontSize * .075f);
        int budget = 6000;
        foreach (var diagnostic in state.Errors)
        {
            if (!DiagnosticRanges.Normalize(state.Source, diagnostic.Start, diagnostic.Length, out int start, out int end))
                continue;
            int line = -1;
            float left = 0, right = 0, bottom = 0;
            void Flush()
            {
                if (line < 0 || right <= left)
                    return;
                float previousX = left, previousY = bottom;
                int step = 0;
                for (float x = left + wave; previousX < right && budget > 0; x += wave)
                {
                    x = Math.Min(x, right);
                    float y = bottom + ((step++ & 1) == 0 ? amplitude : 0);
                    Vector2 direction = new Vector2(x - previousX, y - previousY);
                    Vector2 normal = new Vector2(-direction.y, direction.x).normalized * .8f;
                    int vertex = mesh.currentVertCount;
                    mesh.AddVert(new Vector3(previousX + normal.x, previousY + normal.y), color, Vector2.zero);
                    mesh.AddVert(new Vector3(x + normal.x, y + normal.y), color, Vector2.zero);
                    mesh.AddVert(new Vector3(x - normal.x, y - normal.y), color, Vector2.zero);
                    mesh.AddVert(new Vector3(previousX - normal.x, previousY - normal.y), color, Vector2.zero);
                    mesh.AddTriangle(vertex, vertex + 1, vertex + 2);
                    mesh.AddTriangle(vertex, vertex + 2, vertex + 3);
                    previousX = x;
                    previousY = y;
                    budget--;
                }
            }
            for (int i = 0; i < info.characterCount && i < info.characterInfo.Length; i++)
            {
                var ch = info.characterInfo[i];
                if (ch.index >= end)
                    break;
                if (ch.index < start || ch.character == '\r' || ch.character == '\n')
                    continue;
                if (ch.lineNumber != line)
                {
                    Flush();
                    line = ch.lineNumber;
                    left = ch.origin;
                    bottom = ch.descender - .5f;
                }
                right = ch.xAdvance;
            }
            Flush();
            if (budget <= 0)
                break;
        }
    }
    private static void ClosePopup(State state)
    {
        state.Hover = null;
        state.HoverGlyph = -1;
        if (state.Popup != null)
            state.Popup.Close();
    }
    private static void Remove(CodeWindow window)
    {
        if (!states.TryGetValue(window, out var state))
            return;
        if (state.Underline != null)
            UnityEngine.Object.Destroy(state.Underline.gameObject);
        if (state.Popup != null)
            UnityEngine.Object.Destroy(state.Popup.gameObject);
        states.Remove(window);
    }
    internal static void Clear()
    {
        foreach (var window in states.Keys.ToArray())
            Remove(window);
        delay.Reset();
        observedVersion = visibleVersion = long.MinValue;
    }
}

internal sealed class DiagnosticUnderlineGraphic : MaskableGraphic
{
    internal Action<VertexHelper>? Build;
    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        Build?.Invoke(mesh);
    }
}
