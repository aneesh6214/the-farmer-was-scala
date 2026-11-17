using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace FarmerScala.Editor;

/// <summary>Transient library sources reuse native draggable code windows, outside the project registry.</summary>
internal sealed class ReadOnlyLibrarySource : MonoBehaviour
{
    internal string Id = "";
}

internal static class ReadOnlyLibraryWindows
{
    private static readonly FieldInfo Prefab = AccessTools.Field(typeof(Workspace), "codeWinPrefab");
    private static readonly FieldInfo NameInput = AccessTools.Field(typeof(CodeWindow), "fileNameText");
    private static readonly FieldInfo Play = AccessTools.Field(typeof(CodeWindow), "executeButton");
    private static readonly FieldInfo Step = AccessTools.Field(typeof(CodeWindow), "stepByStepButton");
    private static readonly FieldInfo PreviousText = AccessTools.Field(typeof(CodeWindow), "prevText");
    private static readonly FieldInfo CodeText = AccessTools.Field(typeof(CodeWindow), "codeText");
    internal static bool IsReadOnly(CodeWindow? window) => window != null && window.GetComponent<ReadOnlyLibrarySource>() != null;

    internal static bool CanOpen(AnalysisDefinition target) => target.IsLibrary && !string.IsNullOrWhiteSpace(target.LibraryId)
        && target.Source != null && target.Source.Length <= 512 * 1024 && target.Offset >= 0 && target.Offset <= target.Source.Length;

    internal static CodeWindow? Find(Workspace workspace, AnalysisDefinition target) => workspace.OpenWindows
        .Select(w => w.GetComponent<CodeWindow>()).FirstOrDefault(w => w != null && w.GetComponent<ReadOnlyLibrarySource>()?.Id == target.LibraryId);

    internal static CodeWindow Open(Workspace workspace, AnalysisDefinition target)
    {
        var found = Find(workspace, target);
        if (found != null)
            return found;
        if (!CanOpen(target))
            throw new InvalidOperationException("Library source is unavailable or exceeds the viewer limit.");
        // The reserved name cannot collide with a valid player module name. Do
        // not use OpenCodeWindow: its save/parse hooks belong to player files.
        string name = workspace.GenerateFileName("__farmer_library_");
        var window = workspace.OpenWindow((CodeWindow)Prefab.GetValue(workspace), name,
            NativeWindowLayout.NewWindowPosition(workspace, 40), new Vector2(850, 560));
        window.workspace = workspace;
        var marker = window.gameObject.AddComponent<ReadOnlyLibrarySource>();
        marker.Id = target.LibraryId!;
        workspace.codeWindows.TryRemove(name, out _);
        window.CodeInput.readOnly = true;
        if (NameInput.GetValue(window) is TMP_InputField title)
            title.readOnly = true;
        if (Play.GetValue(window) is Component play)
            play.gameObject.SetActive(false);
        if (Step.GetValue(window) is Component step)
            step.gameObject.SetActive(false);
        window.Load(target.Source!, readOnly: true);
        // The registry identity is private; show only the actual source name.
        if (NameInput.GetValue(window) is TMP_InputField caption)
            caption.text = target.File + ".scala (read only)";
        ScalaHighlighting.Refresh(window);
        return window;
    }

    internal static void CloseAll(Workspace workspace)
    {
        foreach (var window in workspace.OpenWindows.Where(w => IsReadOnly(w.GetComponent<CodeWindow>())).ToArray())
            window.Close();
    }

    [HarmonyPatch(typeof(CodeWindow), nameof(CodeWindow.Rename))]
    private static class RenameGuard
    {
        private static bool Prefix(CodeWindow __instance) => !IsReadOnly(__instance);
    }

    [HarmonyPatch(typeof(CodeWindow), "UpdateTokens")]
    private static class ReadOnlyRendering
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(CodeWindow __instance, ref bool __result)
        {
            if (!IsReadOnly(__instance))
                return true;
            // TMP readOnly blocks user keystrokes, not native normalization
            // during Load. Bypass both normalization and undo recording so a
            // library archive's exact source offsets remain intact.
            string source = __instance.CodeInput.text;
            PreviousText.SetValue(__instance, source);
            if (CodeText.GetValue(__instance) is TMP_Text text)
                text.text = ScalaSyntaxHighlighter.Colorize(source, ScalaHighlighting.CreatePalette(ThemeManager.Inst.Theme),
                    escapeMarkup: !__instance.CodeInput.richText);
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(CodeWindow), nameof(CodeWindow.PromptDelete))]
    private static class CloseSource
    {
        private static bool Prefix(CodeWindow __instance)
        {
            if (!IsReadOnly(__instance))
                return true;
            __instance.GetComponent<Window>().Close();
            return false;
        }
    }
}
