using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FarmerScala;

/// <summary>Scoped native popup with a title, native close icon and two choices.</summary>
internal static class SaveLanguagePopupLayout
{
    private static readonly FieldInfo PopupWindow = AccessTools.Field(typeof(WarningPopup), "window");
    private static readonly FieldInfo PopupText = AccessTools.Field(typeof(WarningPopup), "text");
    private static readonly FieldInfo PopupButtons = AccessTools.Field(typeof(WarningPopup), "buttons");
    private static readonly FieldInfo ButtonText = AccessTools.Field(typeof(ColoredButton), "nameText");
    private static readonly FieldInfo ButtonPanel = AccessTools.Field(typeof(ColoredButton), "buttonPanel");
    private static readonly FieldInfo ButtonIcon = AccessTools.Field(typeof(ColoredButton), "icon");
    private static readonly FieldInfo DocsPrefab = AccessTools.Field(typeof(Workspace), "docWinPrefab");
    private static readonly FieldInfo DocsScroll = AccessTools.Field(typeof(DocsWindow), "scrollView");
    private static readonly FieldInfo WindowTitle = AccessTools.Field(typeof(TitleWindow), "title");
    private static Scope? active;

    internal static void Apply(WarningPopup popup)
    {
        if (active != null)
            Restore(active.Popup);
        var scope = new Scope(popup);
        active = scope;
        try
        {
            scope.Apply();
        }
        catch { Restore(popup); throw; }
    }

    internal static void Restore(WarningPopup popup)
    {
        var scope = active;
        if (scope == null || !ReferenceEquals(scope.Popup, popup))
            return;
        active = null;
        scope.Restore();
    }

    private sealed class RectState
    {
        private readonly RectTransform rect;
        private readonly Transform parent;
        private readonly int sibling;
        private readonly Vector2 min, max, pivot, size;
        private readonly Vector3 position, scale;
        public RectState(RectTransform rect)
        {
            this.rect = rect;
            parent = rect.parent;
            sibling = rect.GetSiblingIndex();
            min = rect.anchorMin;
            max = rect.anchorMax;
            pivot = rect.pivot;
            size = rect.sizeDelta;
            position = rect.anchoredPosition3D;
            scale = rect.localScale;
        }
        public void Restore()
        {
            if (rect == null || parent == null)
                return;
            rect.SetParent(parent, false);
            rect.SetSiblingIndex(sibling);
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.pivot = pivot;
            rect.sizeDelta = size;
            rect.anchoredPosition3D = position;
            rect.localScale = scale;
        }
    }

    private sealed class ImageStyle
    {
        private readonly Sprite sprite, overrideSprite;
        private readonly Material material;
        private readonly Image.Type type;
        private readonly bool fillCenter, preserveAspect;
        private readonly float pixelsPerUnit;
        internal ImageStyle(Image image)
        {
            sprite = image.sprite;
            overrideSprite = image.overrideSprite;
            material = image.material;
            type = image.type;
            fillCenter = image.fillCenter;
            preserveAspect = image.preserveAspect;
            pixelsPerUnit = image.pixelsPerUnitMultiplier;
        }
        internal void Apply(Image image)
        {
            image.sprite = sprite;
            image.overrideSprite = overrideSprite;
            image.material = material;
            image.type = type;
            image.fillCenter = fillCenter;
            image.preserveAspect = preserveAspect;
            image.pixelsPerUnitMultiplier = pixelsPerUnit;
        }
    }

    internal sealed class Scope
    {
        internal readonly WarningPopup Popup;
        private readonly RectTransform window;
        private readonly TextMeshProUGUI text;
        private readonly RectTransform[] rows, panels;
        private readonly TextMeshProUGUI[] labels;
        private readonly List<RectState> rects = new List<RectState>();
        private readonly List<(TextMeshProUGUI Text, TextAlignmentOptions Alignment)> alignments;
        private readonly List<(Behaviour Component, bool Enabled)> layoutComponents;
        private readonly float optionWidth, rowHeight, backWidth, padding, gap, labelPadX, labelPadY;
        private readonly Vector3 nativeWindowScale;
        private Vector2 lastSize;
        private Vector2 lastCanvasSize;
        private SaveLanguagePopupResize? resize;
        private TextMeshProUGUI? header;
        private Image? body;
        private ColoredButton? closeButton;
        private readonly DocsWindow nativeDocs;
        private readonly ColoredButton nativeClose;
        private readonly ImageStyle originalFrame;
        private readonly float frameInset, titleHeight, closeWidth, closeHeight, closeRight, closeTop, titleLeft;

        internal Scope(WarningPopup popup)
        {
            Popup = popup;
            window = ((Image)PopupWindow.GetValue(popup)!).rectTransform;
            originalFrame = new ImageStyle(window.GetComponent<Image>());
            nativeDocs = (DocsWindow)DocsPrefab.GetValue(MainSim.Inst.workspace);
            var nativeRoot = nativeDocs.GetComponent<RectTransform>();
            var nativeBody = ((ScrollRect)DocsScroll.GetValue(nativeDocs)).GetComponent<RectTransform>();
            var bodyBounds = RelativeRect(nativeRoot, nativeBody);
            frameInset = Math.Max(1f, bodyBounds.xMin - nativeRoot.rect.xMin);
            titleHeight = nativeRoot.rect.yMax - bodyBounds.yMax;
            nativeClose = nativeDocs.GetComponentsInChildren<ColoredButton>(true).First(candidate =>
            {
                var button = (Button)ScalaOutputPanel.ButtonField.GetValue(candidate);
                for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                    if (button.onClick.GetPersistentTarget(i) is Window && button.onClick.GetPersistentMethodName(i) == "Close")
                        return true;
                return false;
            });
            var closeBounds = RelativeRect(nativeRoot, nativeClose.GetComponent<RectTransform>());
            closeWidth = closeBounds.width;
            closeHeight = closeBounds.height;
            closeRight = nativeRoot.rect.xMax - closeBounds.xMax;
            closeTop = nativeRoot.rect.yMax - closeBounds.yMax;
            titleLeft = frameInset + 8f;
            text = (TextMeshProUGUI)PopupText.GetValue(popup)!;
            var buttons = (RectTransform)PopupButtons.GetValue(popup)!;
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(window);
            // Native ShowPopup destroys previous controls at the frame boundary.
            rows = buttons.Cast<Transform>().Reverse().Take(3).Reverse().Select(x => (RectTransform)x).ToArray();
            if (rows.Length != 3 || rows.Any(row => row.GetComponent<ColoredButton>() == null))
                throw new InvalidOperationException("The native language buttons were not created.");
            labels = rows.Select(row => (TextMeshProUGUI)ButtonText.GetValue(row.GetComponent<ColoredButton>())!).ToArray();
            panels = rows.Select(row => ((Image)ButtonPanel.GetValue(row.GetComponent<ColoredButton>())!).rectTransform).ToArray();
            nativeWindowScale = window.localScale;
            labelPadX = Math.Max(12f, labels.Max(label => label.fontSize) * 0.45f);
            labelPadY = Math.Max(8f, labels.Max(label => label.fontSize) * 0.18f);
            for (int index = 0; index < rows.Length; index++)
            {
                Vector2 preferred = labels[index].GetPreferredValues(labels[index].text, float.PositiveInfinity, float.PositiveInfinity);
                // The shipped panel is 14 units larger than the root. Measure
                // its visible bounds, then place that panel INSIDE the new root.
                rowHeight = Math.Max(rowHeight, Math.Max(RelativeRect(rows[index], panels[index]).height,
                    Math.Max(rows[index].rect.height, preferred.y + 2f * labelPadY)));
                if (index < 2)
                    optionWidth = Math.Max(optionWidth, Math.Max(RelativeRect(rows[index], panels[index]).width,
                        preferred.x + 2f * labelPadX));
                else
                    backWidth = preferred.x + 2f * labelPadX;
            }
            backWidth = rowHeight;
            // Never inherit zero spacing or negative panel/label insets.
            padding = Math.Max(24f, rowHeight * 0.45f);
            gap = Math.Max(16f, rowHeight * 0.35f);
            var captured = new HashSet<RectTransform>();
            void Capture(RectTransform rect)
            {
                if (captured.Add(rect))
                    rects.Add(new RectState(rect));
            }
            Capture(window);
            Capture(text.rectTransform);
            foreach (var row in rows)
                Capture(row);
            foreach (var panel in panels)
                Capture(panel);
            foreach (var label in labels)
                Capture(label.rectTransform);
            alignments = labels.Concat(new[] { text }).Select(label => (label, label.alignment)).ToList();
            layoutComponents = window.GetComponentsInChildren<Behaviour>(true)
                .Where(component => component is LayoutGroup || component is ContentSizeFitter || component is AspectRatioFitter)
                .Select(component => (component, component.enabled)).ToList();
        }

        internal void Apply()
        {
            foreach (var item in layoutComponents)
                item.Component.enabled = false;
            Vector3 center = window.TransformPoint(window.rect.center);
            window.pivot = new Vector2(0.5f, 0.5f);
            window.position = center;
            text.rectTransform.SetParent(window, false);
            text.alignment = TextAlignmentOptions.Center;
            // Copy actual Info-window image assets, including slicing scale.
            // The warning-popup sprite has a large radius/shadow and must not
            // be stretched into the content panel.
            body = new GameObject("SaveLanguageBody", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<Image>();
            body.transform.SetParent(window, false);
            body.transform.SetAsFirstSibling();
            var frame = window.GetComponent<Image>();
            new ImageStyle(nativeDocs.GetComponent<Image>()).Apply(frame);
            new ImageStyle(((ScrollRect)DocsScroll.GetValue(nativeDocs)).GetComponent<Image>()).Apply(body);
            body.raycastTarget = false;
            var menu = Resources.FindObjectsOfTypeAll<Menu>().First(item => item.gameObject.scene.IsValid());
            var titleTemplate = WindowTitle.GetValue(menu.titleWorkspace.tilteWinPrefab) as TextMeshProUGUI;
            header = UnityEngine.Object.Instantiate(titleTemplate != null ? titleTemplate : text, window);
            header.name = "SaveLanguageTitle";
            header.gameObject.SetActive(true);
            header.text = "New Save";
            header.alignment = TextAlignmentOptions.MidlineLeft;
            header.raycastTarget = false;
            for (int index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                row.SetParent(window, false);
                if (panels[index] != row)
                {
                    panels[index].SetParent(row, false);
                    Stretch(panels[index], 0f, 0f);
                }
                if (labels[index].rectTransform != row)
                {
                    labels[index].rectTransform.SetParent(row, false);
                    Stretch(labels[index].rectTransform, labelPadX, labelPadY);
                }
                labels[index].alignment = TextAlignmentOptions.Center;
            }
            // Reuse the WHOLE native close control, keeping the exact glyph,
            // visible panel, padding and proportions instead of scaling an icon
            // inside a warning button. Only the callback belongs to this modal.
            closeButton = UnityEngine.Object.Instantiate(nativeClose, window);
            closeButton.name = "SaveLanguageClose";
            closeButton.gameObject.SetActive(true);
            closeButton.Interactable = true;
            var closeClick = (Button)ScalaOutputPanel.ButtonField.GetValue(closeButton);
            closeClick.onClick = new Button.ButtonClickedEvent();
            closeClick.onClick.AddListener(() => ((Button)ScalaOutputPanel.ButtonField.GetValue(rows[2].GetComponent<ColoredButton>())).onClick.Invoke());
            closeButton.onHeld = new UnityEngine.Events.UnityEvent();
            rows[2].gameObject.SetActive(false);
            Refresh(force: true);
            resize = Popup.GetComponent<SaveLanguagePopupResize>() ?? Popup.gameObject.AddComponent<SaveLanguagePopupResize>();
            resize.Session = this;
            resize.enabled = true;
        }

        internal void Refresh(bool force = false)
        {
            if (window == null)
                return;
            var theme = ThemeManager.Inst.Theme;
            window.GetComponent<Image>().color = theme.ui.WindowFrameColor;
            if (body != null)
                body.color = theme.docs.BackgroundColor;
            if (header != null)
                theme.ui.text.ApplyTo(header);
            if (closeButton != null)
            {
                closeButton.UpdateColor();
                if (ButtonIcon.GetValue(closeButton) is Image icon)
                    icon.color = theme.ui.ButtonTextColor;
            }
            var canvas = window.GetComponentInParent<Canvas>();
            Rect? available = canvas != null && window.parent != null
                ? RelativeRect(window.parent, canvas.rootCanvas.GetComponent<RectTransform>()) : (Rect?)null;
            Vector2 canvasSize = available?.size ?? Vector2.zero;
            if (!force && window.rect.size == lastSize && canvasSize == lastCanvasSize)
                return;
            float width = Math.Max(480f, 2f * optionWidth + gap + 2f * padding);
            float textHeight = text.GetPreferredValues(text.text, width - 2f * padding, float.PositiveInfinity).y;
            var layout = new SaveLanguagePopupGeometry(width, window.rect.height, textHeight,
                optionWidth, rowHeight, backWidth, padding, gap, frameInset, titleHeight,
                closeWidth, closeHeight, closeRight, closeTop, titleLeft);
            // Give the controls room instead of compressing their text. Only a
            // window larger than the canvas is uniformly fitted to the screen.
            window.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, layout.Width);
            window.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, layout.Height);
            window.localScale = nativeWindowScale;
            if (available.HasValue)
            {
                float scale = Math.Min(1f, Math.Min(
                    Math.Max(1f, available.Value.width - 2f * padding) / (layout.Width * Math.Abs(nativeWindowScale.x)),
                    Math.Max(1f, available.Value.height - 2f * padding) / (layout.Height * Math.Abs(nativeWindowScale.y))));
                window.localScale = new Vector3(nativeWindowScale.x * scale, nativeWindowScale.y * scale, nativeWindowScale.z);
            }
            lastSize = window.rect.size;
            lastCanvasSize = canvasSize;
            SetRect(text.rectTransform, layout.Message);
            SetRect(rows[0], layout.Default);
            SetRect(rows[1], layout.Native);
            if (closeButton != null)
                SetRect(closeButton.GetComponent<RectTransform>(), layout.Back);
            if (body != null)
                SetRect(body.rectTransform, layout.Body);
            if (header != null)
            {
                SetRect(header.rectTransform, layout.Header);
            }
        }

        internal void Restore()
        {
            if (resize != null)
            {
                resize.Session = null;
                resize.enabled = false;
            }
            if (header != null)
                UnityEngine.Object.Destroy(header.gameObject);
            if (body != null)
                UnityEngine.Object.Destroy(body.gameObject);
            if (closeButton != null)
                UnityEngine.Object.Destroy(closeButton.gameObject);
            if (rows[2] != null)
                rows[2].gameObject.SetActive(true);
            if (window != null)
            {
                originalFrame.Apply(window.GetComponent<Image>());
                window.GetComponent<Image>().color = ThemeManager.Inst.Theme.ui.WarningBackgroundColor;
            }
            foreach (var state in rects)
                state.Restore();
            foreach (var item in alignments)
                if (item.Text != null)
                    item.Text.alignment = item.Alignment;
            foreach (var item in layoutComponents)
                if (item.Component != null)
                    item.Component.enabled = item.Enabled;
        }

        private static void SetRect(RectTransform rect, SaveLanguagePopupGeometry.Box box)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localScale = Vector3.one;
            rect.sizeDelta = new Vector2(box.Width, box.Height);
            rect.anchoredPosition = new Vector2(box.X, box.Y);
        }

        private static void Stretch(RectTransform rect, float horizontal, float vertical)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localScale = Vector3.one;
            rect.offsetMin = new Vector2(horizontal, vertical);
            rect.offsetMax = new Vector2(-horizontal, -vertical);
        }

        private static Rect RelativeRect(Transform parent, RectTransform child)
        {
            var corners = new Vector3[4];
            child.GetWorldCorners(corners);
            Vector3 first = parent.InverseTransformPoint(corners[0]);
            float minX = first.x, maxX = first.x, minY = first.y, maxY = first.y;
            for (int index = 1; index < corners.Length; index++)
            {
                Vector3 point = parent.InverseTransformPoint(corners[index]);
                minX = Math.Min(minX, point.x);
                maxX = Math.Max(maxX, point.x);
                minY = Math.Min(minY, point.y);
                maxY = Math.Max(maxY, point.y);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }
    }
}

internal sealed class SaveLanguagePopupResize : MonoBehaviour
{
    internal SaveLanguagePopupLayout.Scope? Session;
    private void LateUpdate()
    {
        try
        {
            Session?.Refresh();
        }
        catch (Exception error)
        {
            Plugin.Instance?.LogError("Save language popup resize: " + error);
            enabled = false;
        }
    }
}
