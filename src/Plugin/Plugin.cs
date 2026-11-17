using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Bootstrap;
using FarmerScala.Editor;
using FarmerScala.Runtime;
using HarmonyLib;

namespace FarmerScala;

[BepInPlugin("dev.farmerscala", "Farmer Scala", "1.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public static Plugin? Instance
    {
        get; private set;
    }
    internal RuntimeHost Host { get; private set; } = null!;
    private Harmony? harmony;
    private readonly ConcurrentQueue<string> output = new ConcurrentQueue<string>();
    private bool ready;

    private void Awake()
    {
        Instance = this;
        string root = Path.GetDirectoryName(Info.Location)!;
        try
        {
            if (Chainloader.PluginInfos.ContainsKey("dev.farmerpython"))
                throw new InvalidOperationException("Farmer Python is also installed. Disable it before enabling the standalone Scala mod.");
            string core = Path.Combine(Paths.ManagedPath, "Core.dll");
            string digest;
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(core))
                digest = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
            const string supported = "F0EFDA7C138DE8028B1769C121A4B5C17B6BB538AEAE5A67D1881E7C4EBD7D25";
            if (digest != supported)
                throw new NotSupportedException("This game build has not been verified. Core.dll SHA256=" + digest);
            Host = new RuntimeHost(this, root);
            EditorIntegration.Initialize(() => Host.IsRunning, window => Host.Run(window),
                () => Host.Stop("Stopped by editor"), LogInfo, Path.Combine(Paths.CachePath, "FarmerScala"));
            EditorIntegration.ParseOverride = Host.ParseSimulationWindow;
            EditorIntegration.PlayWhileRunning = Host.PlayWhileRunning;
            EditorIntegration.StepRequested = Host.Step;
            EditorIntegration.IsInvolvedInRun = Host.IsInvolved;
            EditorIntegration.LoadFailure = SaveLanguageMenu.ShowLoadError;
            EditorIntegration.ProjectChanged += Host.ProjectChanged;
            harmony = new Harmony("dev.farmerscala");
            harmony.PatchAll(typeof(Plugin).Assembly);
            ScalaAnalysis.Initialize(root);
            ready = true;
            LogInfo("Farmer Scala loaded; verified game Core. Language is fixed per save and selected when loading.");
        }
        catch (Exception e)
        {
            harmony?.UnpatchSelf();
            LogError("Farmer Scala disabled: " + e);
        }
    }
    private void Start()
    {
        if (!ready) return;
        try { Game.MiningSupport.Initialize(Path.GetDirectoryName(Info.Location)!); }
        catch (Exception error) { ready = false; harmony?.UnpatchSelf(); Host.Dispose(); LogError("Scala SDK could not initialize: " + error); }
    }
    private void Update()
    {
        if (!ready)
            return;
        try
        {
            if (!Game.MiningSupport.Ready)
            {
                Game.MiningSupport.Initialize(Path.GetDirectoryName(Info.Location)!);
                if (!Game.MiningSupport.Ready) return;
            }
            EditorIntegration.Tick();
            ScalaAnalysis.Tick();
            ScalaDiagnostics.Tick();
            ScalaEditorTools.Tick();
            ScalaParentheses.Tick();
            Host.Tick();
            bool wroteNativeOutput = false;
            try
            {
                for (int i = 0; i < 20 && output.TryDequeue(out string line); i++)
                {
                    LogInfo(line);
                    if (MainSim.Inst != null)
                    {
                        wroteNativeOutput = true;
                        MainSim.Inst.Logger.Log(line);
                    }
                }
            }
            finally
            {
                // Late Scala stdout can reopen output.txt after native Stop
                // closed it. Release only a writer touched by this flush while
                // both execution paths are idle; the next native Clear drops
                // its writer reference without disposing an existing handle.
                var main = MainSim.Inst;
                if (wroteNativeOutput && !Host.IsRunning && (main == null || !main.IsExecuting()))
                    MainSim.Inst?.Logger.Close();
            }
        }
        catch (Exception e) { ReportFailure("Scala mod stopped after an internal error: " + e.Message); LogError(e.ToString()); Host.Stop("Internal error"); }
    }
    internal void QueueOutput(string text)
    {
        if (output.Count > 200)
            return;
        output.Enqueue(text.Length > 2000 ? text.Substring(0, 2000) + "…" : text);
    }
    internal void ClearQueuedOutput()
    {
        while (output.TryDequeue(out _))
        {
        }
    }
    internal void ReportFailure(string text)
    {
        LogError(text);
        QueueOutput("Scala mod warning: " + text);
        ScalaOutputPanel.Attention.Notify(OutputSeverity.Error);
    }
    internal void LogInfo(string text) => Logger.LogInfo(text);
    internal void LogError(string text) => Logger.LogError(text);
    private void OnApplicationFocus(bool focused)
    {
        if (!focused)
            ScalaNavigation.FocusLost();
    }
    private void OnDestroy()
    {
        ready = false;
        ScalaDiagnostics.Clear();
        ScalaEditorTools.Clear();
        ScalaLineNumbers.Clear();
        ScalaFontRendering.Clear();
        ScalaAnalysis.Dispose();
        if (Host != null)
            EditorIntegration.ProjectChanged -= Host.ProjectChanged;
        Host?.Dispose();
        harmony?.UnpatchSelf();
        Instance = null;
    }
}

[HarmonyPatch(typeof(MainSim), nameof(MainSim.StopMainExecution))]
internal static class NativeStopGuard
{
    private static void Prefix() => Plugin.Instance?.Host?.PrepareNativeStop();
}
