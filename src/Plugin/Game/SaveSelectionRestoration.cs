using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace FarmerScala;

/// <summary>Keep the native active-save setting a string across game restarts.</summary>
internal static class SaveSelectionRestoration
{
    private static readonly FieldInfo Options = AccessTools.Field(typeof(OptionHolder), "options");

    internal static string? ReadSaveName(string text)
    {
        string? name = null;
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (line.StartsWith("activeSave = ", StringComparison.Ordinal))
                name = line.Substring("activeSave = ".Length);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    // activeSave has no OptionSO declaring a value type. Native ValueOfString
    // interprets names ending in numbers ("main farm 2") or key names as keyboard
    // shortcuts, so GetString subsequently falls back to Save0. Repair only this
    // in-memory entry after parsing; do not rewrite options or load another world.
    [HarmonyPatch(typeof(OptionHolder), "LoadOptions")]
    private static class LoadedOptions
    {
        private static void Postfix()
        {
            try
            {
                string path = Path.Combine(Helper.persistentDataPath, "options.txt");
                if (!File.Exists(path)) return;
                string? name = ReadSaveName(File.ReadAllText(path));
                if (name != null && Options.GetValue(null) is Dictionary<string, object> options)
                    options["activeSave"] = name;
            }
            catch (Exception error)
            {
                Plugin.Instance?.LogError("Could not restore the selected save setting: " + error.Message);
            }
        }
    }
}
