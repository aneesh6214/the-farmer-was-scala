using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using FarmerScala.Editor;
using UnityEngine;

public sealed partial class IntegrationPlugin
{
    private static string barrierDirectory = "";
    private static ManualResetEventSlim? backupEntered, releaseBackup;
    private static void HoldBackupCopy(string sourcePath)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(sourcePath), barrierDirectory)) return;
        backupEntered!.Set();
        if (!releaseBackup!.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test backup barrier");
    }
    private IEnumerator ConcurrentBackup()
    {
        const string oldCode = "val revision = 1\n";
        const string newCode = "val revision = 2\n";
        yield return Load("ConcurrentBackup", new Dictionary<string, string> { ["main"] = oldCode });
        string save = Path.Combine(root, "data/Saves/ConcurrentBackup");
        Type storeType = Mod("Editor.SourceStore");
        object store = Activator.CreateInstance(storeType, new object[] { Path.Combine(root, "store-cache") })!;
        var copy = typeof(Saver).GetMethod("CopyDirectory", All)!;
        var harmony = new Harmony("dev.farmerscala.integration.backup-barrier");
        backupEntered = new ManualResetEventSlim(); releaseBackup = new ManualResetEventSlim();
        barrierDirectory = Path.GetFullPath(Path.Combine(save, ".farmer-scala"));
        Task? backup = null, writer = null;
        Thread? releaser = null;
        try
        {
            // Pause only the copying thread, inside the real native backup and
            // production lock. A concurrent real SourceStore writer must wait.
            harmony.Patch(copy, prefix: new HarmonyMethod(typeof(IntegrationPlugin).GetMethod(nameof(HoldBackupCopy), All)));
            using var attempted = new ManualResetEventSlim();
            bool waitedForBackup = false, writerPersisted = false;
            backup = Task.Run(() => Call(typeof(Saver), "CreateBackupSnapshot", null, "ConcurrentBackup", Path.Combine(root, "data")));
            writer = Task.Run(() => {
                if (!backupEntered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("backup did not reach sidecar");
                attempted.Set();
                using var access = (IDisposable)Call(storeType, "HoldSaveFiles", null, save)!;
                Call(storeType, "SaveSources", store, save, "ConcurrentBackup", true, new Dictionary<string, string> { ["main"] = newCode });
                writerPersisted = File.ReadAllText(Path.Combine(save, ".farmer-scala/src/main.scala")) == newCode;
            });
            // Release independently of Unity's thread: a real UI read can also
            // wait briefly on this same gate during an ordinary backup.
            releaser = new Thread(() => {
                attempted.Wait(TimeSpan.FromSeconds(10));
                Thread.Sleep(50);
                waitedForBackup = !writer.IsCompleted;
                releaseBackup.Set();
            }) { IsBackground = true };
            releaser.Start();
            yield return Wait(() => backup.IsCompleted && writer.IsCompleted, "backup and queued editor write");
            releaser.Join();
            Check(backupEntered.IsSet, "actual native backup reaches the Scala sidecar");
            Check(waitedForBackup, "Scala edits wait while the native backup copies their files");
            backup.GetAwaiter().GetResult(); writer.GetAwaiter().GetResult();
            var copies = Directory.GetDirectories(Path.Combine(root, "data/Backup"), "ConcurrentBackup_backup*");
            Check(copies.Length == 1 && File.ReadAllText(Path.Combine(copies[0], ".farmer-scala/src/main.scala")) == oldCode, "native backup contains the complete pre-edit source revision");
            Check(writerPersisted, "editor write persists after the native backup releases its files");
        }
        finally
        {
            releaseBackup.Set();
            releaser?.Join(TimeSpan.FromSeconds(11));
            try { backup?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
            try { writer?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
            harmony.UnpatchSelf(); barrierDirectory = "";
            backupEntered.Dispose(); releaseBackup.Dispose();
        }
        // A failed backup must also release the production lock and retain native errors.
        var failed = Task.Run(() => {
            using var access = (IDisposable)Call(storeType, "HoldSaveFiles", null, save)!;
            using var locked = File.Open(Path.Combine(save, ".farmer-scala/project.json"), FileMode.Open, FileAccess.Read, FileShare.None);
            Call(typeof(Saver), "CreateBackupSnapshot", null, "ConcurrentBackup", Path.Combine(root, "data"));
        });
        yield return Wait(() => failed.IsCompleted, "deliberately locked backup");
        Check(failed.IsFaulted && failed.Exception!.ToString().Contains("IOException"), "genuine backup failures remain visible instead of being swallowed");
        var probe = Task.Run(() => { using var access = (IDisposable)Call(storeType, "HoldSaveFiles", null, save)!; });
        yield return Wait(() => probe.IsCompleted, "save access after backup failure"); probe.GetAwaiter().GetResult();
        Check(!Directory.GetDirectories(Path.Combine(root, "data/Backup"), ".*.tmp").Any(), "native failed-backup cleanup leaves no partial snapshot");
        // Exercise the normal SaveAsync queue, not only its backup leaf, repeatedly.
        for (int i = 0; i < 12; i++)
        {
            Edit(Main, "val revision = " + i + "\n");
            var task = Saver.Save(MainSim.Inst);
            Edit(Main, "val revision = " + (i + 100) + "\n");
            Call(typeof(EditorIntegration), "SaveCurrent", null);
            yield return Wait(() => task.IsCompleted, "real SaveAsync racing an editor update");
            Check(task.GetAwaiter().GetResult(), "native save succeeds during continued editing, iteration " + i);
        }
        Check(File.ReadAllText(Path.Combine(save, ".farmer-scala/src/main.scala")) == Main.CodeInput.text, "continued editing retains the latest source after all queued saves finish");
    }
    private IEnumerator CompletionRelevance()
    {
        yield return Load("CompletionRelevance", new Dictionary<string, string> { ["main"] = "val entity = Entities.\n" });
        List<string>? names = null;
        Call(Mod("Editor.ScalaAnalysis"), "Complete", null, Main, Main.CodeInput.text.IndexOf("Entities.", StringComparison.Ordinal) + 9,
            new Action<List<string>>(value => names = value));
        yield return Wait(() => names != null && names.Contains("Grass") && names.Contains("values"), "ranked enum compiler completion");
        Check(names!.IndexOf("Grass") < names.IndexOf("values"), "compiler enum cases precede enum utilities");
        var popup = MainSim.Inst.workspace.codeCompleter;
        int start = Main.CodeInput.text.IndexOf("Entities.", StringComparison.Ordinal) + 9;
        Main.CodeInput.stringPosition = start;
        popup.Open(Vector3.zero, names!, "", start, Main);
        var shown = (List<string>)Field(popup, "optionStrings")!;
        Check(shown.IndexOf("Grass") < shown.IndexOf("values"), "native popup preserves compiler relevance instead of alphabetically re-sorting it");
        if (shown.Contains("->")) Check(shown.IndexOf("Grass") < shown.IndexOf("->"), "native popup keeps enum cases above extension operators");
        popup.SetTypedString("va");
        Check(popup.Selection == "values" || popup.Selection == "valueOf", "typed-prefix matches still take priority over unrelated enum cases");
        popup.Close();
    }
}
