using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using FarmerScala.Editor;
using HarmonyLib;
using TMPro;

namespace FarmerScala;

/// <summary>Native New Game choices and Scala badges in the save list.</summary>
internal static class SaveLanguageMenu
{
    private sealed class Creation
    {
        public SaveChooser Chooser = null!;
        public SaveLanguage Language;
        public string? Name;
        public bool LanguageWritten;
    }

    [ThreadStatic] private static Creation? creating;
    private static SaveChooser? pendingChooser;
    private static WarningPopup? choicePopup;
    private static long choiceGeneration;
    private static readonly FieldInfo SaveName = AccessTools.Field(typeof(SaveOption), "fileName");
    private static readonly FieldInfo SaveLabel = AccessTools.Field(typeof(SaveOption), "nameText");

    private static SourceStore Store() => new SourceStore(Path.Combine(Paths.CachePath, "FarmerScala"));

    private static void ShowChoice(SaveChooser chooser)
    {
        var main = MainSim.Inst;
        if (main == null)
            return;
        RetirePopup(main.warningPopup);
        long generation = ++choiceGeneration;
        main.warningPopup.ShowPopup("Choose the language for this save.\nThis choice is permanent.\n\n<size=80%>Steam achievements and leaderboards are disabled in Scala 3.</size>",
            new List<WarningPopup.ButtonData>
            {
                new WarningPopup.ButtonData("Default", () => Choose(chooser, generation, SaveLanguage.Default)),
                new WarningPopup.ButtonData("Scala 3", () => Choose(chooser, generation, SaveLanguage.NativeScala)),
                new WarningPopup.ButtonData("×", () => Cancel(generation))
            });
        pendingChooser = chooser;
        choicePopup = main.warningPopup;
        try
        {
            SaveLanguagePopupLayout.Apply(choicePopup);
        }
        catch (Exception error)
        {
            Plugin.Instance?.LogError("Save language popup layout: " + error);
            Cancel(generation);
            ShowLoadError("Could not open the language choices. See the BepInEx log for details.");
        }
    }

    private static void RetirePopup(WarningPopup popup)
    {
        if (ReferenceEquals(choicePopup, popup))
        {
            pendingChooser = null;
            choiceGeneration++;
            choicePopup = null;
        }
        SaveLanguagePopupLayout.Restore(popup);
    }

    private static void Cancel(long generation)
    {
        if (generation != choiceGeneration)
            return;
        pendingChooser = null;
        choiceGeneration++;
        if (MainSim.Inst != null)
            MainSim.Inst.warningPopup.Close();
    }

    private static void Choose(SaveChooser chooser, long generation, SaveLanguage language)
    {
        if (generation != choiceGeneration || pendingChooser != chooser || chooser == null)
            return;
        pendingChooser = null;
        choiceGeneration++; // Retire both choice callbacks before invoking native creation.
        if (MainSim.Inst != null)
            MainSim.Inst.warningPopup.Close();

        var previous = creating;
        var request = new Creation { Chooser = chooser, Language = language };
        try
        {
            if (previous != null)
                throw new InvalidOperationException("Another save creation is already in progress.");
            creating = request;
            // The original chooser still adds the native row and opens its
            // rename field. The scoped hooks only choose a fresh name and
            // persist the language before its native save-file creation.
            chooser.CreateNewSave();
            if (!request.LanguageWritten || request.Name == null || !File.Exists(Saver.GetPathOfSaveFile(request.Name)))
                throw new IOException("The native save creation did not complete.");
        }
        catch (Exception error)
        {
            Plugin.Instance?.LogError("New save language: " + error);
            ShowCreationError();
        }
        finally { creating = previous; }
    }

    private static void ShowCreationError()
    {
        var main = MainSim.Inst;
        if (main == null)
            return;
        main.warningPopup.ShowPopup("Could not finish creating the save with that language.\n\n"
            + "Any unfinished save data has been kept. Choose New Game again to retry; existing save folders will not be reused.",
            new List<WarningPopup.ButtonData>
            {
                new WarningPopup.ButtonData("OK", main.warningPopup.Close)
            });
    }

    internal static void ShowLoadError(string message)
    {
        var main = MainSim.Inst;
        if (main == null)
        {
            Plugin.Instance?.ReportFailure(message);
            return;
        }
        main.warningPopup.ShowPopup(message, new List<WarningPopup.ButtonData>
        {
            new WarningPopup.ButtonData("OK", main.warningPopup.Close)
        });
    }

    private static string FreshSaveName()
    {
        for (int number = 0; number < int.MaxValue; number++)
        {
            string name = "Save" + number;
            if (!Directory.Exists(Saver.GetPathOfSaveDirectory(name)) && !File.Exists(Saver.GetPathOfSaveFile(name)))
                return name;
        }
        throw new IOException("No unused save name is available.");
    }

    private static void WriteLanguageBeforeNativeCreation(string saveName)
    {
        var request = creating;
        if (request == null)
            return; // Native first-launch/bootstrap creation keeps the default language.
        if (request.Name == null || request.Name != saveName || request.LanguageWritten)
            throw new IOException("Unexpected or repeated native save creation.");
        string directory = Saver.GetPathOfSaveDirectory(saveName);
        if (Directory.Exists(directory) || File.Exists(Saver.GetPathOfSaveFile(saveName)))
            throw new IOException("The selected save name is already in use.");

        var store = Store();
        store.InitializeLanguage(directory, saveName, request.Language);
        if (request.Language == SaveLanguage.NativeScala)
            store.SaveSources(directory, saveName, true, new Dictionary<string, string> { ["main"] = "" });
        request.LanguageWritten = true;
    }

    private static void RenderSaveBadge(SaveOption option)
    {
        var label = SaveLabel.GetValue(option) as TextMeshProUGUI;
        string? name = SaveName.GetValue(option) as string;
        if (label == null || string.IsNullOrEmpty(name))
            return;
        bool scala = false;
        try
        {
            scala = Store().ReadLanguage(Saver.GetPathOfSaveDirectory(name!)) == SaveLanguage.NativeScala;
        }
        catch (Exception error)
        {
            Plugin.Instance?.LogInfo("Save language label unavailable: " + error.Message);
        }
        label.text = name;
        var badge = option.GetComponent<ScalaSaveBadge>() ?? option.gameObject.AddComponent<ScalaSaveBadge>();
        badge.Configure(label, scala);
    }

    [HarmonyPatch(typeof(SaveChooser), nameof(SaveChooser.CreateNewSave))]
    private static class NewGameChoice
    {
        private static bool Prefix(SaveChooser __instance)
        {
            if (creating != null)
            {
                if (creating.Chooser != __instance)
                    throw new InvalidOperationException("Nested save creation is not supported.");
                return true;
            }
            ShowChoice(__instance);
            return false;
        }
    }

    // Keep the native popup and its InputBoss/modal lifecycle. A new warning,
    // ordinary Close, or scene/menu disable ends the temporary layout first.
    [HarmonyPatch(typeof(WarningPopup), nameof(WarningPopup.ShowPopup))]
    private static class PopupReused
    {
        private static void Prefix(WarningPopup __instance) => RetirePopup(__instance);
    }

    [HarmonyPatch(typeof(WarningPopup), nameof(WarningPopup.Close))]
    private static class PopupClosed
    {
        private static void Prefix(WarningPopup __instance) => RetirePopup(__instance);
    }

    [HarmonyPatch(typeof(WarningPopup), "OnDisable")]
    private static class PopupDisabled
    {
        private static void Prefix(WarningPopup __instance) => RetirePopup(__instance);
    }

    [HarmonyPatch(typeof(SaveChooser), nameof(SaveChooser.GenerateUnusedSaveName))]
    private static class FreshNameGuard
    {
        private static bool Prefix(ref string __result)
        {
            if (creating == null)
                return true;
            if (creating.Name == null)
                creating.Name = FreshSaveName();
            __result = creating.Name;
            return false;
        }
    }

    [HarmonyPatch(typeof(Saver), nameof(Saver.CreateNewSaveGame))]
    private static class NewSaveLanguage
    {
        private static void Prefix(string saveName) => WriteLanguageBeforeNativeCreation(saveName);
    }

    [HarmonyPatch(typeof(SaveOption), nameof(SaveOption.Setup))]
    private static class SaveListLanguage
    {
        private static void Postfix(SaveOption __instance) => RenderSaveBadge(__instance);
    }

    [HarmonyPatch(typeof(SaveOption), nameof(SaveOption.EditFinished))]
    private static class RenamedSaveLanguage
    {
        private static void Postfix(SaveOption __instance) => RenderSaveBadge(__instance);
    }

    [HarmonyPatch(typeof(Saver), nameof(Saver.RenameSave))]
    private static class RenameLanguageIdentity
    {
        private static void Postfix(string saveName, string newSaveName, bool __result)
        {
            if (__result)
                EditorIntegration.SaveRenamed(saveName, newSaveName);
        }
    }
}
