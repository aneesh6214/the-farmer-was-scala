using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FarmerScala.Editor;

/// <summary>Stores the same window fields as the native saver, plus order/view.</summary>
internal static class NativeWindowLayout
{
    private static readonly System.Reflection.FieldInfo SpawnOffset = AccessTools.Field(typeof(Workspace), "spawnWindowOffset");
    private static readonly System.Reflection.FieldInfo TransitionTarget = AccessTools.Field(typeof(Workspace), "transitionTarget");
    private static readonly System.Reflection.FieldInfo TransitionStart = AccessTools.Field(typeof(Workspace), "transitionStartTime");
    private static readonly System.Reflection.FieldInfo TransitionEnd = AccessTools.Field(typeof(Workspace), "transitionEndTime");
    private static readonly System.Reflection.FieldInfo ScrollRect = AccessTools.Field(typeof(Workspace), "scrollRect");
    private static readonly System.Reflection.MethodInfo UpdateContainerSize = AccessTools.Method(typeof(Workspace), "UpdateContainerSizeInternal");
    private static readonly System.Reflection.MethodInfo LerpCamera = AccessTools.Method(typeof(Workspace), "LerpCameraToTarget");

    private static Vector2 FarmOrigin(Workspace workspace) => workspace.container.childCount > 0
        ? ((RectTransform)workspace.container.GetChild(0)).anchoredPosition : Vector2.zero;

    public static Vector2 NewWindowPosition(Workspace workspace, int cascade)
        => -workspace.container.anchoredPosition + (Vector2)SpawnOffset.GetValue(workspace)! + new Vector2(cascade, -cascade);

    public static WindowLayout? Find(WorkspaceLayout? layout, string name)
        => layout?.Windows?.FirstOrDefault(x => x.IsCode && StringComparer.OrdinalIgnoreCase.Equals(x.Name, name));

    public static Vector2 Position(Workspace workspace, WindowLayout item) => FarmOrigin(workspace) + new Vector2(item.X, item.Y);

    public static WorkspaceLayout Capture(Workspace workspace)
    {
        Vector2 origin = FarmOrigin(workspace);
        var farm = workspace.container.childCount > 0 ? workspace.container.GetChild(0) : null;
        var view = farm != null ? workspace.zoomContainer.InverseTransformPoint(farm.position) : (Vector3?)null;
        return new WorkspaceLayout
        {
            PanX = workspace.container.anchoredPosition.x,
            PanY = workspace.container.anchoredPosition.y,
            Zoom = workspace.cameraController.zoom,
            FarmWorldX = farm != null ? farm.position.x : (float?)null,
            FarmWorldY = farm != null ? farm.position.y : (float?)null,
            FarmViewX = view?.x,
            FarmViewY = view?.y,
            ActiveWindow = workspace.activeWindow != null && !ReadOnlyLibraryWindows.IsReadOnly(workspace.activeWindow.GetComponent<CodeWindow>()) ? workspace.activeWindow.windowName : null,
            Windows = workspace.OpenWindows.Where(w => !ReadOnlyLibraryWindows.IsReadOnly(w.GetComponent<CodeWindow>()) &&
                    (w.GetComponent<CodeWindow>() != null || w.GetComponent<DocsWindow>() != null))
                .Select(w =>
                {
                    var code = w.GetComponent<CodeWindow>();
                    var docs = w.GetComponent<DocsWindow>();
                    Vector2 position = w.WindowRect.anchoredPosition - origin;
                    return new WindowLayout
                    {
                        Name = w.windowName,
                        IsCode = code != null,
                        DocPage = docs != null ? docs.openDoc : null,
                        X = position.x,
                        Y = position.y,
                        Width = w.playerSetSize.x,
                        Height = w.playerSetSize.y,
                        ScrollY = code != null ? ((RectTransform)code.CodeInput.transform).anchoredPosition.y : 0f,
                        Minimized = w.isMinimized,
                        DockedParent = w.dockedParent != null && !ReadOnlyLibraryWindows.IsReadOnly(w.dockedParent.GetComponent<CodeWindow>()) ? w.dockedParent.windowName : null,
                        Order = w.HighestDockedParent.transform.GetSiblingIndex()
                    };
                }).ToList()
        };
    }

    public static void UndockAll(Workspace workspace)
    {
        foreach (var window in workspace.OpenWindows.ToArray())
            window.Undock();
    }

    public static void Restore(Workspace workspace, WorkspaceLayout layout, bool restoreViewport = true)
    {
        if (!SourceStore.IsUsableLayout(layout))
            throw new InvalidOperationException("Unsupported or invalid Scala workspace layout.");
        StopCamera(workspace);
        foreach (var window in workspace.OpenWindows.ToArray())
            if (window.GetComponent<DocsWindow>() != null && !layout.Windows.Any(x => !x.IsCode && StringComparer.OrdinalIgnoreCase.Equals(x.Name, window.windowName)))
                window.Close();
        foreach (var item in layout.Windows.Where(x => !x.IsCode && !string.IsNullOrEmpty(x.DocPage)))
        {
            if (workspace.codeWindows.ContainsKey(item.Name))
                continue; // Incoming source owns its name.
            if (!workspace.TryGetOpenWindow(item.Name, out var existing))
                workspace.OpenDocsWindow(item.Name, item.DocPage!, Position(workspace, item), new Vector2(item.Width, item.Height));
            else
            {
                var docs = existing.GetComponent<DocsWindow>();
                if (docs != null && docs.openDoc != item.DocPage)
                    docs.LoadDoc(item.DocPage!);
            }
        }

        foreach (var item in layout.Windows)
        {
            if (!workspace.TryGetOpenWindow(item.Name, out var window) || !Finite(item) || (window.GetComponent<CodeWindow>() != null) != item.IsCode)
                continue;
            window.playerSetSize = new Vector2(item.Width, item.Height);
            window.UpdateSize();
            window.WindowRect.anchoredPosition = Position(workspace, item);
            window.SetMinmized(item.Minimized);
            var code = window.GetComponent<CodeWindow>();
            if (code != null)
                ((RectTransform)code.CodeInput.transform).anchoredPosition = new Vector2(0f, item.ScrollY);
        }
        foreach (var item in layout.Windows.Where(x => !string.IsNullOrEmpty(x.DockedParent)))
            if (workspace.TryGetOpenWindow(item.Name, out var child) && workspace.TryGetOpenWindow(item.DockedParent!, out var parent) && child != parent)
                child.DockOnto(parent);
        foreach (var item in layout.Windows.OrderBy(x => x.Order))
            if (workspace.TryGetOpenWindow(item.Name, out var window))
                window.MoveToFront();
        if (layout.ActiveWindow != null && workspace.TryGetOpenWindow(layout.ActiveWindow, out var active))
            workspace.activeWindow = active;
        if (restoreViewport)
            RestoreViewport(workspace, layout);
    }

    public static Vector3 ViewParentPosition(Workspace workspace) => workspace.zoomContainer.parent != null
        ? workspace.zoomContainer.parent.position : Vector3.zero;

    public static Vector3? FarmWorldPosition(Workspace workspace) => workspace.container.childCount > 0
        ? workspace.container.GetChild(0).position : (Vector3?)null;

    public static void RestoreViewport(Workspace workspace, WorkspaceLayout layout)
    {
        if (!SourceStore.IsUsableLayout(layout))
            throw new InvalidOperationException("Unsupported or invalid Scala workspace layout.");
        StopCamera(workspace);
        if (Finite(layout.Zoom) && layout.Zoom > 0f)
        {
            workspace.cameraController.zoom = layout.Zoom;
            workspace.zoomContainer.localScale = Vector3.one * layout.Zoom;
            workspace.container.GetComponent<ContainerScaler>().UpdateMarginSize();
        }
        // Native bounds recomputation rebases the container and all of its
        // child anchors while preserving their world positions. Flush it
        // before restoring the viewport; raw anchored pan is not stable.
        UpdateContainerSize.Invoke(workspace, null);
        if (layout.FarmViewX.HasValue && layout.FarmViewY.HasValue && workspace.container.childCount > 0)
        {
            Vector3 current = workspace.zoomContainer.InverseTransformPoint(workspace.container.GetChild(0).position);
            workspace.container.position += workspace.zoomContainer.TransformVector(new Vector3(layout.FarmViewX.Value - current.x,
                layout.FarmViewY.Value - current.y, 0f));
        }
        else
            FocusCodeWindow(workspace);
        StopCamera(workspace);
    }

    public static void FocusCodeWindow(Workspace workspace)
    {
        var available = workspace.codeWindows.Values
            .OrderBy(code => code.fileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(code => code.fileName, StringComparer.Ordinal).ToArray();
        var active = workspace.activeWindow;
        var code = available.FirstOrDefault(item => active != null && item.GetComponent<Window>() == active && !active.isMinimized)
            ?? available.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.fileName, "main") && !item.GetComponent<Window>().isMinimized)
            ?? available.FirstOrDefault(item => !item.GetComponent<Window>().isMinimized)
            ?? available.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.fileName, "main"))
            ?? available.FirstOrDefault();
        if (code == null)
            return;
        var window = code.GetComponent<Window>();
        StopCamera(workspace);
        if (window.isMinimized)
            window.SetMinmized(false);
        workspace.activeWindow = window;
        window.MoveToFront();
        UpdateContainerSize.Invoke(workspace, null);
        // Center the first code character rather than a very wide window's
        // midpoint, which can leave its title and controls off screen.
        code.CodeInput.ForceLabelUpdate();
        code.CodeInput.textComponent.ForceMeshUpdate(true, true);
        if (code.CodeInput.textComponent.textInfo.characterCount > 0)
            workspace.MoveCameraTo(window, alwaysMoveWindow: true, charIndex: 0, inputField: code.CodeInput);
        else
            workspace.MoveCameraTo(window, alwaysMoveWindow: true);
        // Complete the game's own camera calculation now so the following
        // capture records the focused view, rather than an unfinished tween.
        TransitionStart.SetValue(workspace, Time.time - 1f);
        TransitionEnd.SetValue(workspace, Time.time);
        LerpCamera.Invoke(workspace, null);
        StopCamera(workspace);
    }

    public static void StopCamera(Workspace workspace)
    {
        TransitionTarget.SetValue(workspace, null);
        if (ScrollRect.GetValue(workspace) is UnityEngine.UI.ScrollRect scroll)
            scroll.StopMovement();
    }

    // Save native world progress while retaining its own code/docs layout.
    public static void ApplyToNativeSave(SaveGame save, WorkspaceLayout layout)
    {
        if (!SourceStore.IsUsableLayout(layout))
            throw new InvalidOperationException("Unsupported or invalid Scala workspace layout.");
        save.openFilePositions = layout.Windows.Select(x => new Pair<string, Vector2>(x.Name, new Vector2(x.X, x.Y))).ToList();
        save.openFileSizes = layout.Windows.Select(x => new Pair<string, Vector2>(x.Name, new Vector2(x.Width, x.Height))).ToList();
        save.openFileScrollPositions = layout.Windows.Where(x => x.IsCode).Select(x => new Pair<string, float>(x.Name, x.ScrollY)).ToList();
        save.minimizedFiles = layout.Windows.Where(x => x.Minimized).Select(x => x.Name).ToList();
        save.dockedFiles = layout.Windows.Where(x => !string.IsNullOrEmpty(x.DockedParent)).Select(x => new Pair<string, string>(x.Name, x.DockedParent!)).ToList();
        save.openDocPages = layout.Windows.Where(x => !x.IsCode && x.DocPage != null).Select(x => new Pair<string, string>(x.Name, x.DocPage!)).ToList();
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(WindowLayout item) => Finite(item.X) && Finite(item.Y) && Finite(item.Width) && Finite(item.Height) && Finite(item.ScrollY);
}
