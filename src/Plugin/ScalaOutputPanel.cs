using System;
using System.Linq;
using System.Reflection;
using FarmerScala.Editor;
using FarmerScala.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace FarmerScala;

/// <summary>A permanent toolbar entry to the native live-output document.</summary>
internal static class ScalaOutputPanel
{
    internal const string Page = "docs/output.md";
    internal static readonly OutputAttention Attention = new OutputAttention();
    private static volatile bool available;
    internal static bool Active => available && EditorIntegration.Enabled;
    private static bool compilationRequested;
    internal static bool ShowCompilation => EditorIntegration.Enabled && compilationRequested;
    internal static readonly FieldInfo ButtonField = AccessTools.Field(typeof(ColoredButton), "button");
    private static readonly FieldInfo IconField = AccessTools.Field(typeof(ColoredButton), "icon");
    private static readonly FieldInfo DocsPrefab = AccessTools.Field(typeof(Workspace), "docWinPrefab");
    private static Sprite? terminal;

    [HarmonyPatch(typeof(Menu), "Start")]
    private static class AddButton
    {
        private static void Postfix()
        {
            GameObject? copy = null;
            try
            {
                var workspace = MainSim.Inst.workspace;
                // Resolve the actual New File listener rather than guessing
                // object names or matching translated tooltip strings.
                var original = Resources.FindObjectsOfTypeAll<ColoredButton>().FirstOrDefault(colored =>
                {
                    if (!colored.gameObject.scene.IsValid())
                        return false;
                    var button = ButtonField.GetValue(colored) as Button;
                    if (button == null)
                        return false;
                    for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                        if (button.onClick.GetPersistentTarget(i) == workspace && button.onClick.GetPersistentMethodName(i) == "AddNewWindow")
                            return true;
                    return false;
                });
                if (original == null)
                    throw new InvalidOperationException("Could not locate the native New File button.");
                if (original.transform.parent.Find("ScalaOutputButton") != null)
                    return;
                copy = UnityEngine.Object.Instantiate(original.gameObject, original.transform.parent);
                copy.name = "ScalaOutputButton";
                var coloredCopy = copy.GetComponent<ColoredButton>();
                var buttonCopy = (Button)ButtonField.GetValue(coloredCopy);
                buttonCopy.onClick = new Button.ButtonClickedEvent();
                buttonCopy.onClick.AddListener(Open);
                coloredCopy.tooltipName = "Output";
                coloredCopy.tooltipDescription = "Open program output. Each Play clears the previous run.";
                var icon = (Image)IconField.GetValue(coloredCopy);
                var nativeIcon = (Image)IconField.GetValue(original);
                ToolbarIcons.Assign(icon, nativeIcon, TerminalIcon(nativeIcon));
                var controller = copy.AddComponent<ScalaOutputButton>();
                controller.Initialize(original, coloredCopy);
                available = true;
                foreach (var window in workspace.OpenWindows.ToArray())
                {
                    var docs = window.GetComponent<DocsWindow>();
                    if (docs != null && docs.openDoc == Page)
                        docs.LoadDoc(Page);
                }
            }
            catch (Exception error)
            {
                available = false;
                if (copy != null)
                    UnityEngine.Object.Destroy(copy);
                Plugin.Instance?.LogError("Scala output toolbar: " + error);
            }
        }
    }

    private static Sprite TerminalIcon(Image native)
    {
        if (terminal != null)
            return terminal;
        terminal = ToolbarIcons.Load(native, "FarmerScala.Terminal.png", "ScalaTerminal");
        return terminal;
    }

    internal static void Open()
    {
        try
        {
            var workspace = MainSim.Inst.workspace;
            var window = workspace.OpenWindows.FirstOrDefault(w => w.GetComponent<DocsWindow>()?.openDoc == Page);
            if (window == null)
            {
                // Info uses this exact prefab, spawn offset and default size.
                workspace.AddNewDocsWindow(Page);
                window = workspace.OpenWindows.First(w => w.GetComponent<DocsWindow>()?.openDoc == Page);
            }
            window.SetMinmized(false);
            window.MoveToFront();
            workspace.activeWindow = window;
            workspace.MoveCameraTo(window);
            Attention.Acknowledge();
        }
        catch (Exception error) { Plugin.Instance?.LogError("Scala output window: " + error); }
    }

    internal static void Retire()
    {
        available = false;
        compilationRequested = false;
        Attention.Acknowledge();
    }

    [HarmonyPatch(typeof(Localizer), nameof(Localizer.LoadDoc))]
    private static class CompilationHeading
    {
        private static void Postfix(string __0, ref string __result)
        {
            if (__0 != Page || !ShowCompilation)
                return;
            string gray = ColorUtility.ToHtmlStringRGB(ThemeManager.Inst.Theme.code.CommentColor);
            __result = WithCompilationHeading(__result, gray);
        }
    }

    internal static string WithCompilationHeading(string document, string gray)
    {
        // Keep the native link, truncated heading and live-output section.
        // This per-run status stays visible even for a cached, instant build.
        int end = document.IndexOf('\n');
        string first = end < 0 ? document : document.Substring(0, end).TrimEnd('\r');
        string rest = end < 0 ? "" : document.Substring(end + 1);
        return first + "\n<color=#" + gray + ">Compiling...</color>\n" + rest;
    }

    [HarmonyPatch(typeof(EditorIntegration), "BeforeLoad")]
    private static class ResetCompilation
    {
        private static void Prefix() => compilationRequested = false;
    }

    [HarmonyPatch(typeof(DocsWindow), nameof(DocsWindow.LoadDoc))]
    private static class OutputToolbar
    {
        private static void Postfix(DocsWindow __instance)
        {
            if (EditorIntegration.Enabled && __instance.openDoc == Page)
            {
                var status = __instance.GetComponent<ScalaOutputStatus>() ?? __instance.gameObject.AddComponent<ScalaOutputStatus>();
                status.Initialize(__instance);
            }
            if (EditorIntegration.Enabled && __instance.openDoc == Page && __instance.GetComponent<Window>() is Window window
                && window.playerSetSize == new Vector2(800, 450))
            {
                // Migrate only our old undersized default, preserving user resizes.
                var prefab = (DocsWindow)DocsPrefab.GetValue(MainSim.Inst.workspace);
                window.playerSetSize = prefab.GetComponent<RectTransform>().rect.size;
                window.UpdateSize();
            }
            // Keep output as a panel rather than a documentation browser.
            foreach (var button in __instance.GetComponentsInChildren<Button>(true))
                for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                    if (button.onClick.GetPersistentTarget(i) == __instance && button.onClick.GetPersistentMethodName(i) == "GotoHome")
                        button.gameObject.SetActive(!EditorIntegration.Enabled || __instance.openDoc != Page);
        }
    }

    [HarmonyPatch(typeof(RuntimeHost), nameof(RuntimeHost.Run))]
    private static class BeginPlay
    {
        private static void Prefix(RuntimeHost __instance)
        {
            if (!Active || __instance.IsRunning || MainSim.Inst.IsExecuting())
                return;
            Plugin.Instance?.ClearQueuedOutput();
            global::Logger.Close();
            global::Logger.Clear();
        }

        private static void Postfix(RuntimeHost __instance)
        {
            // Run leaves a pending request even when the build task is cached;
            // its completion is applied by the next main-thread queue tick.
            if (Active && __instance.IsCompiling)
                compilationRequested = true;
        }
    }

    [HarmonyPatch(typeof(global::Logger), nameof(global::Logger.Clear))]
    private static class ResetAttention
    {
        private static void Postfix()
        {
            if (Active)
                Attention.Acknowledge();
        }
    }

    [HarmonyPatch(typeof(global::Logger), nameof(global::Logger.LogError))]
    private static class NativeError
    {
        private static void Postfix()
        {
            if (Active)
                Attention.Notify(OutputSeverity.Error);
        }
    }

    [HarmonyPatch(typeof(EditorIntegration), nameof(EditorIntegration.SetError))]
    private static class ScalaError
    {
        private static void Prefix()
        {
            if (Active)
                Attention.Notify(OutputSeverity.Error);
        }
    }

    [HarmonyPatch(typeof(WarningIcon), nameof(WarningIcon.PopUp))]
    private static class ReplaceWarning
    {
        private static bool Prefix()
        {
            if (!Active)
                return true;
            Attention.Notify(OutputSeverity.Warning);
            return false;
        }
    }
}

internal sealed class ScalaOutputStatus : MonoBehaviour
{
    private DocsWindow window = null!;
    private bool headingShown;
    internal void Initialize(DocsWindow docs)
    {
        window = docs;
        headingShown = ScalaOutputPanel.ShowCompilation;
    }
    private void LateUpdate()
    {
        if (window == null || !EditorIntegration.Enabled || window.openDoc != ScalaOutputPanel.Page)
            return;
        if (headingShown == ScalaOutputPanel.ShowCompilation)
            return;
        headingShown = ScalaOutputPanel.ShowCompilation;
        window.LoadDoc(ScalaOutputPanel.Page);
    }
}

internal sealed class ScalaOutputButton : MonoBehaviour
{
    private RectTransform original = null!;
    private RectTransform info = null!, upgrades = null!;
    private ColoredButton colored = null!;
    private Vector2 nativePosition;
    private Vector2 infoPosition;
    private float step;
    private bool layoutDriven, shifted;
    private bool shown;
    private OutputSeverity painted = (OutputSeverity)(-1);
    private ColorTheme? paintedTheme;
    private WarningIcon? warning;

    internal void Initialize(ColoredButton source, ColoredButton target)
    {
        original = source.GetComponent<RectTransform>();
        colored = target;
        warning = MainSim.Inst.warningIcon;
        nativePosition = original.anchoredPosition;
        var research = MainSim.Inst.researchMenu;
        info = ((GameObject)AccessTools.Field(typeof(ResearchMenu), "infoButton").GetValue(research)).GetComponent<RectTransform>();
        upgrades = ((ColoredButton)AccessTools.Field(typeof(ResearchMenu), "openCloseButton").GetValue(research)).GetComponent<RectTransform>();
        infoPosition = info.anchoredPosition;
        var anchor = upgrades.parent;
        step = Math.Abs(anchor.InverseTransformPoint(upgrades.TransformPoint(upgrades.rect.center)).x
            - anchor.InverseTransformPoint(info.TransformPoint(info.rect.center)).x);
        if (step <= 0)
            step = upgrades.rect.width * Math.Abs(upgrades.localScale.x) * 1.2f;
        var group = original.parent.GetComponent<HorizontalOrVerticalLayoutGroup>();
        layoutDriven = group != null && group.enabled;
        if (layoutDriven)
        {
            transform.SetSiblingIndex(original.GetSiblingIndex() + 1);
            if (GetComponent<LayoutElement>() == null)
                gameObject.AddComponent<LayoutElement>();
        }
        // This controller's GameObject stays active; only its visual children
        // and Button component are hidden in default-language saves.
        Apply();
    }

    private void LateUpdate()
    {
        if (original != null)
            Apply();
    }

    private void Apply()
    {
        bool scala = EditorIntegration.Enabled;
        bool enabled = scala && !MainSim.Inst.researchMenu.IsOpen;
        var rect = (RectTransform)transform;
        var button = (Button)ScalaOutputPanel.ButtonField.GetValue(colored);
        if (shown != enabled || button.enabled != enabled)
        {
            shown = enabled;
            button.enabled = enabled;
            foreach (var graphic in GetComponentsInChildren<Graphic>(true))
                graphic.enabled = enabled;
            if (GetComponent<LayoutElement>() is LayoutElement element)
                element.ignoreLayout = !enabled;
        }
        if (!layoutDriven)
        {
            if (scala && !shifted)
            {
                nativePosition = original.anchoredPosition;
                infoPosition = info.anchoredPosition;
                shifted = true;
            }
            if (!scala && shifted)
            {
                original.anchoredPosition = nativePosition;
                info.anchoredPosition = infoPosition;
                shifted = false;
            }
            rect.anchorMin = original.anchorMin;
            rect.anchorMax = original.anchorMax;
            rect.pivot = original.pivot;
            rect.sizeDelta = original.sizeDelta;
            rect.localScale = original.localScale;
            if (scala)
            {
                // One row, measured from the native Info/Upgrades spacing.
                // Use world centers so different anchors/parents cannot skew it.
                Vector3 right = upgrades.TransformPoint(upgrades.rect.center);
                Vector3 stride = upgrades.parent.TransformVector(Vector3.right * step);
                Center(info, right - stride);
                Center(rect, right - stride * 2f);
                Center(original, right - stride * (enabled ? 3f : 2f));
            }
        }
        // Hiding the toolbar in the tree must not restore the warning icon or
        // discard output attention accumulated while the tree is open.
        if (warning != null)
            warning.gameObject.SetActive(!scala);
        if (!enabled)
            return;
        var theme = ThemeManager.Inst.Theme;
        var severity = ScalaOutputPanel.Attention.Severity;
        if (severity == painted && theme == paintedTheme)
            return;
        painted = severity;
        paintedTheme = theme;
        colored.OverrideColors = severity == OutputSeverity.None ? (ColorBlock?)null :
            OutputAttention.Tint(theme.ui.button.ToColorBlock(), severity == OutputSeverity.Error ? theme.code.ErrorBorderColor : theme.ui.WarningBackgroundColor);
        colored.tooltipName = severity == OutputSeverity.None ? "Output" : severity == OutputSeverity.Error ? "Output — error" : "Output — warning";
    }

    private void OnDestroy()
    {
        if (shifted && original != null)
            original.anchoredPosition = nativePosition;
        if (shifted && info != null)
            info.anchoredPosition = infoPosition;
        if (warning != null)
            warning.gameObject.SetActive(true);
        ScalaOutputPanel.Retire();
    }

    private static void Center(RectTransform rect, Vector3 position)
        => rect.position += position - rect.TransformPoint(rect.rect.center);
}
