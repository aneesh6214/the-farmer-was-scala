using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace FarmerScala.Editor;

/// <summary>Visual line-number gutters, scoped to Scala and transient library windows.</summary>
internal static class ScalaLineNumbers
{
    private static readonly HashSet<ScalaLineNumberGutter> owners = new HashSet<ScalaLineNumberGutter>();
    private static bool warned;
    internal static void Forget(ScalaLineNumberGutter owner) => owners.Remove(owner);
    internal static void Report(Exception error)
    {
        if (warned) return;
        warned = true;
        Plugin.Instance?.LogInfo("Scala line numbers: " + error.Message);
    }
    internal static void Clear()
    {
        foreach (var owner in owners.ToArray())
            if (owner != null)
            {
                owner.Stop();
                UnityEngine.Object.Destroy(owner);
            }
        owners.Clear();
    }

    [HarmonyPatch(typeof(CodeWindow), "Update")]
    private static class WindowUpdate
    {
        private static void Postfix(CodeWindow __instance)
        {
            try
            {
                var gutter = __instance.GetComponent<ScalaLineNumberGutter>();
                if (!EditorIntegration.IsScalaWindow(__instance))
                {
                    gutter?.Stop();
                    return;
                }
                if (gutter == null)
                {
                    gutter = __instance.gameObject.AddComponent<ScalaLineNumberGutter>();
                    owners.Add(gutter);
                }
                gutter.StartFor(__instance);
            }
            catch (Exception error) { Report(error); }
        }
    }
}

internal sealed class ScalaLineNumberGutter : MonoBehaviour
{
    private static readonly FieldInfo BreakpointPanel = AccessTools.Field(typeof(CodeWindow), "breakpointPanel");
    private static readonly FieldInfo BreakpointLines = AccessTools.Field(typeof(BreakPointPanel), "breakpointLines");
    private static readonly FieldInfo CodeText = AccessTools.Field(typeof(CodeWindow), "codeText");
    private static readonly FieldInfo ExtraSize = AccessTools.Field(typeof(CodeWindow), "extraSize");
    private static readonly MethodInfo UpdateSize = AccessTools.Method(typeof(CodeWindow), "UpdateSize");
    private const float ExtraSidePadding = 4f;
    private const float AddedWidth = ExtraSidePadding * 2;
    private const float SidePadding = 2f + ExtraSidePadding;
    private readonly List<TextMeshProUGUI> labels = new List<TextMeshProUGUI>();
    private readonly List<ScalaNumberRow> rows = new List<ScalaNumberRow>();
    private CodeWindow? window;
    private TMP_Text? input, overlay;
    private float originalPanelWidth, originalInputLeft, originalOverlayLeft, originalExtraX;
    private RectTransform? root;
    private BreakPointPanel? panel;
    private readonly HashSet<int> breakpoints = new HashSet<int>();
    private bool active, meshDirty, forceMesh;
    private float width, numberSize, reservedWidth;
    private float fontSize, viewTop = float.NaN, viewBottom = float.NaN;
    private Vector2 textSize;
    private TMP_FontAsset? font;
    private Material? material;
    private string? source;
    private int[] starts = Array.Empty<int>();
    private Color labelColor;

    internal void StartFor(CodeWindow target)
    {
        if (active) return;
        window = target;
        input = target.CodeInput.textComponent;
        overlay = CodeText.GetValue(target) as TMP_Text;
        panel = BreakpointPanel.GetValue(target) as BreakPointPanel;
        if (input == null || overlay == null || panel == null || !(panel.transform is RectTransform parent))
            return;
        // Keep the requested side padding and widen further for larger numbers. Widen
        // the native strip so its divider and centered breakpoint markers follow.
        // TMP margins move code, selections, and diagnostics together without
        // moving the Text Area transform used by native pointer/tooltip helpers.
        originalPanelWidth = parent.sizeDelta.x;
        originalInputLeft = input.margin.x;
        originalOverlayLeft = overlay.margin.x;
        originalExtraX = ((Vector2)ExtraSize.GetValue(target)).x;
        active = true;
        reservedWidth = AddedWidth;
        parent.sizeDelta = new Vector2(originalPanelWidth + AddedWidth, parent.sizeDelta.y);
        SetLeftMargin(input, originalInputLeft + AddedWidth);
        SetLeftMargin(overlay, originalOverlayLeft + AddedWidth);
        var extra = (Vector2)ExtraSize.GetValue(target);
        ExtraSize.SetValue(target, new Vector2(originalExtraX + AddedWidth, extra.y));
        var go = new GameObject("Scala line numbers", typeof(RectTransform));
        root = go.GetComponent<RectTransform>()!;
        root.SetParent(parent, false);
        root.SetAsFirstSibling();
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.pivot = new Vector2(0f, 1f);
        root.offsetMin = root.offsetMax = Vector2.zero;
        input.OnPreRenderText += MeshChanged;
        active = meshDirty = forceMesh = true;
        source = null;
        font = null;
        width = 0;
        UpdateSize.Invoke(target, null);
    }

    private void MeshChanged(TMP_TextInfo info) => meshDirty = true;

    private TextMeshProUGUI Label(int index)
    {
        while (labels.Count <= index)
        {
            var go = new GameObject("Line number", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            var label = go.GetComponent<TextMeshProUGUI>()!;
            label.rectTransform.SetParent(root, false);
            label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(1f, 1f);
            label.rectTransform.pivot = new Vector2(1f, .5f);
            label.alignment = TextAlignmentOptions.BaselineRight;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.enableAutoSizing = false;
            label.overflowMode = TextOverflowModes.Overflow;
            label.richText = false;
            label.raycastTarget = false;
            label.margin = Vector4.zero;
            labels.Add(label);
        }
        return labels[index];
    }

    private void LateUpdate()
    {
        if (!active) return;
        try
        {
            if (window == null || !EditorIntegration.IsScalaWindow(window))
            {
                Stop();
                return;
            }
            if (input == null || root == null) return;
            string next = window.CodeInput.text;
            bool sourceChanged = source != next;
            bool typeChanged = font != input.font || fontSize != input.fontSize;
            if (sourceChanged)
            {
                source = next;
                starts = ScalaLineNumberLayout.LineStarts(next);
                meshDirty = forceMesh = true;
            }
            if (typeChanged)
            {
                font = input.font;
                fontSize = input.fontSize;
                material = ScalaFontRendering.CodeMaterial(font);
                meshDirty = forceMesh = true;
            }
            float nextWidth = Math.Max(1f, root.rect.width);
            if (sourceChanged || typeChanged || nextWidth != width)
            {
                numberSize = fontSize * .8f;
                var measure = Label(0);
                SetTypography(measure);
                float preferred = measure.GetPreferredValues(new string('9', ScalaLineNumberLayout.Digits(starts.Length))).x;
                width = Math.Max(originalPanelWidth + AddedWidth, Mathf.Ceil(preferred + SidePadding * 2));
                if (panel != null && panel.transform is RectTransform strip && strip.sizeDelta.x != width)
                {
                    reservedWidth = width - originalPanelWidth;
                    strip.sizeDelta = new Vector2(width, strip.sizeDelta.y);
                    SetLeftMargin(input, originalInputLeft + reservedWidth);
                    SetLeftMargin(overlay!, originalOverlayLeft + reservedWidth);
                    var extra = (Vector2)ExtraSize.GetValue(window);
                    ExtraSize.SetValue(window, new Vector2(originalExtraX + reservedWidth, extra.y));
                    UpdateSize.Invoke(window, null);
                    forceMesh = true;
                }
                meshDirty = true;
            }
            var nativeBreakpoints = BreakpointLines.GetValue(panel) as List<int>;
            bool breakpointsChanged = nativeBreakpoints != null && !breakpoints.SetEquals(nativeBreakpoints);
            if (breakpointsChanged)
            {
                breakpoints.Clear();
                breakpoints.UnionWith(nativeBreakpoints!);
            }
            var size = input.rectTransform.rect.size;
            if (size != textSize)
            {
                textSize = size;
                meshDirty = forceMesh = true;
            }
            Color nextColor;
            if (!ColorUtility.TryParseHtmlString(ScalaSyntaxPalette.LineNumberColor, out nextColor)) nextColor = Color.gray;
            bool colorChanged = labelColor != nextColor;
            labelColor = nextColor;
            bool rebuild = meshDirty;
            if (rebuild)
            {
                if (forceMesh) input.ForceMeshUpdate(true, false);
                BuildRows();
                meshDirty = forceMesh = false;
            }
            var viewport = window.scrollViewRect;
            float top = input.rectTransform.InverseTransformPoint(viewport.TransformPoint(new Vector3(0, viewport.rect.yMax, 0))).y;
            float bottom = input.rectTransform.InverseTransformPoint(viewport.TransformPoint(new Vector3(0, viewport.rect.yMin, 0))).y;
            if (!rebuild && !colorChanged && !breakpointsChanged && top == viewTop && bottom == viewBottom) return;
            viewTop = top; viewBottom = bottom;
            DrawVisible(top, bottom);
        }
        catch (Exception error)
        {
            ScalaLineNumbers.Report(error);
            Stop();
        }
    }

    private void SetTypography(TextMeshProUGUI label)
    {
        label.font = font!;
        if (material != null) label.fontSharedMaterial = material;
        label.fontSize = numberSize;
        label.fontStyle = FontStyles.Normal;
        label.color = labelColor;
    }

    private void BuildRows()
    {
        rows.Clear();
        var info = input!.textInfo;
        int previous = 0;
        float height = fontSize * 1.35f;
        for (int line = 0; line < info.lineCount && line < info.lineInfo.Length; line++)
        {
            var visual = info.lineInfo[line];
            // Empty trailing TMP rows can retain uninitialized/stale metrics.
            // Newline glyphs still have characterCount > 0; synthesize only the
            // final empty source line below using the previous line's spacing.
            if (info.characterCount == 0 || visual.characterCount == 0) continue;
            int glyph = visual.firstCharacterIndex;
            int offset = glyph >= 0 && glyph < info.characterCount && glyph < info.characterInfo.Length && visual.characterCount > 0
                ? info.characterInfo[glyph].index : source!.Length;
            int number = ScalaLineNumberLayout.LineAt(starts, offset);
            float baseline = visual.baseline, top = visual.ascender, bottom = visual.descender;
            if (float.IsNaN(baseline) || float.IsInfinity(baseline) || Math.Abs(baseline) > 100000000f) continue;
            if (visual.lineHeight > 0) height = visual.lineHeight;
            rows.Add(new ScalaNumberRow(number == previous ? 0 : number, baseline, top, bottom));
            previous = number;
        }
        // TMP may omit the last empty visual line or all rows of an empty file.
        if (rows.Count == 0)
        {
            float scale = font != null && font.faceInfo.pointSize > 0 ? fontSize / font.faceInfo.pointSize * font.faceInfo.scale : 1f;
            float ascent = font != null ? font.faceInfo.ascentLine * scale : fontSize;
            float baseline = input.rectTransform.rect.yMax - input.margin.y - ascent;
            rows.Add(new ScalaNumberRow(1, baseline, baseline + fontSize, baseline - fontSize * .3f));
            previous = 1;
        }
        if (previous < starts.Length)
        {
            var last = rows[rows.Count - 1];
            if (rows.Count > 1) height = rows[rows.Count - 2].Baseline - last.Baseline;
            rows.Add(new ScalaNumberRow(starts.Length, last.Baseline - height, last.Top - height, last.Bottom - height));
        }
    }

    private void DrawVisible(float top, float bottom)
    {
        int used = 0;
        int first = ScalaLineNumberLayout.FirstVisible(rows, top);
        for (int i = first; i < rows.Count && rows[i].Top >= bottom && used < 512; i++)
        {
            var row = rows[i];
            if (!ScalaLineNumberLayout.ShowNumber(row.Number, breakpoints)) continue;
            var label = Label(used++);
            SetTypography(label);
            label.text = row.Number.ToString(CultureInfo.InvariantCulture);
            var baseline = root!.InverseTransformPoint(input!.rectTransform.TransformPoint(new Vector3(0, row.Baseline, 0)));
            label.rectTransform.sizeDelta = new Vector2(Math.Max(1, width - SidePadding * 2), fontSize * 1.5f);
            label.rectTransform.anchoredPosition = new Vector2(-SidePadding, baseline.y - root.rect.yMax);
            label.gameObject.SetActive(true);
        }
        for (int i = used; i < labels.Count; i++) labels[i].gameObject.SetActive(false);
    }

    private static void SetLeftMargin(TMP_Text text, float left)
    {
        var margin = text.margin;
        text.margin = new Vector4(left, margin.y, margin.z, margin.w);
    }

    internal void Stop()
    {
        if (!active) return;
        active = false;
        if (input != null)
        {
            input.OnPreRenderText -= MeshChanged;
            if (Math.Abs(input.margin.x - originalInputLeft - reservedWidth) < .01f)
                SetLeftMargin(input, originalInputLeft);
        }
        if (overlay != null && Math.Abs(overlay.margin.x - originalOverlayLeft - reservedWidth) < .01f)
            SetLeftMargin(overlay, originalOverlayLeft);
        if (panel != null && panel.transform is RectTransform strip && Math.Abs(strip.sizeDelta.x - originalPanelWidth - reservedWidth) < .01f)
            strip.sizeDelta = new Vector2(originalPanelWidth, strip.sizeDelta.y);
        if (window != null)
        {
            var extra = (Vector2)ExtraSize.GetValue(window);
            if (Math.Abs(extra.x - originalExtraX - reservedWidth) < .01f)
                ExtraSize.SetValue(window, new Vector2(originalExtraX, extra.y));
        }
        if (root != null) { root.gameObject.SetActive(false); UnityEngine.Object.Destroy(root.gameObject); }
        root = null; panel = null; labels.Clear(); rows.Clear(); breakpoints.Clear();
        if (window != null && window.workspace != null && window.CodeInput != null &&
            window.GetComponent<Window>() != null && window.gameObject.activeInHierarchy) UpdateSize.Invoke(window, null);
    }
    private void OnDisable() => Stop();
    private void OnDestroy() { Stop(); ScalaLineNumbers.Forget(this); }
}
