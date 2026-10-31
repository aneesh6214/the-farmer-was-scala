using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;

namespace FarmerScala.Editor;

/// <summary>Window names are module paths; the on-disk .scala suffix is optional user input.</summary>
internal static class ScalaModuleNames
{
    private static readonly FieldInfo NameInput = AccessTools.Field(typeof(CodeWindow), "fileNameText");

    [HarmonyPatch(typeof(CodeWindow), nameof(CodeWindow.Rename))]
    private static class Rename
    {
        private static bool Prefix(CodeWindow __instance, string __0)
        {
            if (!EditorIntegration.IsScalaWindow(__instance))
                return true;
            if (ReadOnlyLibraryWindows.IsReadOnly(__instance))
                return false;
            // Canonical module paths may themselves end in a segment named
            // scala. Re-blurring an unchanged title must never strip it again.
            if (__0 == __instance.fileName)
                return false;
            try
            {
                string name = SourceStore.NormalizeFileNameInput(__0);
                SourceStore.ValidateModuleSet(__instance.workspace.codeWindows.Values.Where(w => w != __instance).Select(w => w.fileName).Concat(new[] { name }));
                var native = __instance.GetComponent<Window>();
                var conflict = __instance.workspace.OpenWindows.FirstOrDefault(w => w != native && StringComparer.OrdinalIgnoreCase.Equals(w.windowName, name));
                if (conflict != null)
                    throw new InvalidOperationException("A window named '" + name + "' already exists.");
                if (__instance.fileName != name)
                {
                    __instance.workspace.RenameWindow(native, name);
                    __instance.fileName = name;
                }
                if (NameInput.GetValue(__instance) is TMP_InputField title)
                    title.text = name;
            }
            catch (Exception error)
            {
                if (NameInput.GetValue(__instance) is TMP_InputField title)
                    title.text = __instance.fileName;
                Plugin.Instance?.LogInfo("Scala file name: " + error.Message);
                var popup = MainSim.Inst?.warningPopup;
                if (popup != null)
                    popup.ShowPopup(error.Message, new List<WarningPopup.ButtonData> { new WarningPopup.ButtonData("ok", popup.Close) });
            }
            // Existing outer Rename postfix persists the fully updated identity.
            return false;
        }
    }
}
