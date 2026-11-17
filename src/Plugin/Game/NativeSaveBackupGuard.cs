using System;
using System.IO;
using FarmerScala.Editor;
using HarmonyLib;

namespace FarmerScala.Game;

/// <summary>Keep the native background backup coherent with Scala sidecar writes.</summary>
[HarmonyPatch(typeof(Saver), "CreateBackupSnapshot")]
internal static class NativeSaveBackupGuard
{
    private static void Prefix(string fileName, string dataPath, out IDisposable __state)
        => __state = SourceStore.HoldSaveFiles(Path.Combine(dataPath, "Saves", fileName));

    private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception; // Preserve native save failure reporting; never hide a failed backup.
    }
}
