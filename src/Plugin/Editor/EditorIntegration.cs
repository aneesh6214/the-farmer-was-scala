using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FarmerScala.Editor;

/// <summary>Owns Scala buffers in native game windows and preserves vanilla source.</summary>
public static class EditorIntegration
{
    private static Func<bool> running = () => false;
    private static Action<CodeWindow> run = _ => { };
    private static Action stop = () => { };
    private static Action<string> log = _ => { };
    private static SourceStore? store;
    private static bool enabled;
    private static bool loading;
    private static bool synchronizing;
    private static bool stopping;
    private static bool sourceWriteBlocked;
    private static bool languageResolved;
    private static SaveLanguage? loadedLanguage;
    private static string? loadedSaveId;
    [ThreadStatic] private static int scalaTokenUpdateDepth;
    private static string saveName = "";
    private static string saveDirectory = "";
    private static MainSim? currentMainSim;
    private static float nextFilePoll;
    private static Dictionary<string, string>? watchedSources;
    private static PendingViewport? pendingViewport;
    private static readonly MethodInfo CloseError = AccessTools.Method(typeof(CodeWindow), "CloseError");
    private static readonly MethodInfo UpdateTokens = AccessTools.Method(typeof(CodeWindow), "UpdateTokens");
    private static readonly FieldInfo NativeSimulation = AccessTools.Field(typeof(MainSim), "sim");

    private sealed class PendingViewport
    {
        public Workspace Workspace = null!;
        public WorkspaceLayout? Layout;
        public string SaveDirectory = "";
        public bool Scala;
        public int QueuedFrame;
        public int DueFrame;
        public int Failures;
        public Vector3 ParentPosition;
    }

    public static bool Enabled => enabled;
    public static bool BlocksNativeExecution => enabled || sourceWriteBlocked;
    public static Action<string>? LoadFailure
    {
        get; set;
    }
    // Unity-thread snapshots only. Consumers queue compilation off the game thread.
    internal static event Action<ProjectSnapshot>? ProjectChanged;
    private static Dictionary<string, string>? compileBuffers;
    private static DateTime compileAfter;
    private static string compileEntry = "";
    private static bool compilePending;
    // SetupSim runs inside Saver.Load before code windows are reconstructed.
    // Failed/unknown language metadata must never authorize unlock grants.
    private static bool ResolvedScalaSaveIdentity(MainSim main) => enabled && languageResolved &&
        loadedLanguage == SaveLanguage.NativeScala && !sourceWriteBlocked && currentMainSim != null &&
        ReferenceEquals(currentMainSim, main) && ReferenceEquals(main, MainSim.Inst) &&
        StringComparer.Ordinal.Equals(saveName, ActiveSaveName());
    internal static bool IsLoadingScalaSave(MainSim main) => loading && ResolvedScalaSaveIdentity(main);
    internal static bool IsResolvedScalaSave(MainSim main) => !loading && ResolvedScalaSaveIdentity(main);
    internal static bool IsScalaWindow(CodeWindow window) => enabled && languageResolved && !loading && currentMainSim != null
        && ReferenceEquals(window.workspace, currentMainSim.workspace) && window.GetComponentInParent<CodeExample>() == null;
    public static string ProjectDirectory => string.IsNullOrEmpty(saveDirectory) ? "" : SourceStore.ProjectRoot(saveDirectory);
    public static Action<CodeWindow>? StepRequested
    {
        get; set;
    }
    public static Func<CodeWindow, bool>? IsInvolvedInRun { get; set; }
    public static Func<CodeWindow, bool>? PlayWhileRunning
    {
        get; set;
    }
    private static Node? pausedMarker;

    public static void BeginStep(CodeWindow window)
    {
        window.StartStepByStepMode();
    }
    public static void EndStep()
    {
        pausedMarker = null;
        if (currentMainSim == null)
            return;
        foreach (var window in currentMainSim.workspace.codeWindows.Values)
            if (window.isExecuting)
                window.StartExecutionMode(false);
    }

    public static void Highlight(string file, int line)
    {
        if (currentMainSim == null)
            return;
        string module = Path.GetFileNameWithoutExtension(file);
        if (!currentMainSim.workspace.codeWindows.TryGetValue(module, out var window))
        {
            log(file + ":" + line);
            return;
        }
        string text = window.CodeInput.text;
        if (text.Length == 0)
            return;
        int start = 0;
        for (int i = 1; i < line; i++)
        {
            int newline = text.IndexOf('\n', start);
            if (newline < 0)
                break;
            start = newline + 1;
        }
        int end = text.IndexOf('\n', start);
        if (end < 0)
            end = text.Length;
        pausedMarker = new LineMarker(window, start, Math.Max(start, end - 1));
        BeginStep(window);
        window.GetComponent<Window>().MoveToFront();
        currentMainSim.workspace.MoveCameraTo(window.GetComponent<Window>(), charIndex: start, inputField: window.CodeInput);
        MainSim.Inst.BlinkEffect(pausedMarker);
    }

    private sealed class LineMarker : Node
    {
        public LineMarker(CodeWindow window, int start, int end) : base(window, start, end) { }
        public override IEnumerable<double> Execute(ProgramState state, Execution execution, int depth)
        {
            yield break;
        }
        public override Node DeepCopy(Dictionary<object, object> copies) => this;
    }
    public static Func<CodeWindow, Node?>? ParseOverride
    {
        get; set;
    }

    public static void Initialize(Func<bool> isRunning, Action<CodeWindow> startRun,
        Action stopRun, Action<string> logger, string stateRoot)
    {
        // The per-save manifest determines the language of the loaded world.
        running = isRunning;
        run = startRun;
        stop = stopRun;
        log = logger;
        store = new SourceStore(stateRoot);
        currentMainSim = null;
        saveName = saveDirectory = "";
        enabled = loading = sourceWriteBlocked = false;
        languageResolved = false;
        loadedLanguage = null;
        loadedSaveId = null;
        pendingViewport = null;
        watchedSources = null;
        compileBuffers = null;
        compilePending = false;
    }

    public static void Tick()
    {
        if (store == null || MainSim.Inst == null || currentMainSim == null || loading)
            return;
        if (pausedMarker != null && running())
            MainSim.Inst.BlinkEffect(pausedMarker);
        // Only a real Saver.Load can bind a language to a world. An option or
        // metadata edit must never replace this running world's interpreter.
        if (!SessionIdentityMatches())
        {
            BlockSession("Load the selected save before running or saving code.");
            return;
        }
        ApplyPendingViewport();
        PollCompilation();
        if (!enabled || sourceWriteBlocked || synchronizing || running() || Time.unscaledTime < nextFilePoll)
            return;
        nextFilePoll = Time.unscaledTime + 1f;
        if (OptionHolder.GetString("file watcher") != "enabled")
            return;
        try
        {
            EnsureCurrentSave();
            var files = store.ReadSources(saveDirectory);
            if (watchedSources == null)
            {
                watchedSources = files;
                return;
            }
            // Editor typing differs from disk until commit. Only an actual disk change
            // may reload a buffer, and concurrent changes must preserve both versions.
            if (SourceMerge.Equal(files, watchedSources))
                return;
            var buffers = ReadBuffers(currentMainSim!.workspace);
            var merged = SourceMerge.Reconcile(watchedSources, files, buffers);
            string? history = SourceMerge.ArchiveConflicts(merged, ProjectDirectory);
            if (history != null)
                log("Scala file conflict: kept game editor changes to " + string.Join(", ", merged.Conflicts.Keys) + "; external versions saved in " + history);
            if (!SourceMerge.Equal(merged.Sources, buffers))
                ReplaceBuffers(currentMainSim.workspace, merged.Sources);
            SaveCurrent();
        }
        catch (Exception error) { log("Scala file watcher: " + error.Message); }
    }

    private static void PollCompilation()
    {
        if (ProjectChanged == null || !enabled || sourceWriteBlocked || synchronizing || currentMainSim == null)
            return;
        var windows = currentMainSim.workspace.codeWindows.Values;
        var entry = windows.FirstOrDefault(w => w.CodeInput.isFocused) ?? windows.FirstOrDefault();
        if (entry == null)
            return;
        var buffers = ReadBuffers(currentMainSim.workspace);
        if (buffers.Count > 128 || buffers.Values.Sum(s => (long)s.Length) > 1_000_000)
            return;
        if (compileBuffers == null || !SourceMerge.Equal(compileBuffers, buffers) || compileEntry != entry.fileName)
        {
            compileBuffers = buffers;
            compileEntry = entry.fileName;
            compileAfter = DateTime.UtcNow.AddMilliseconds(150);
            compilePending = true;
        }
        if (!compilePending || DateTime.UtcNow < compileAfter)
            return;
        compilePending = false;
        try
        {
            ProjectChanged?.Invoke(CaptureProject(entry));
        }
        catch (Exception error) { log("Scala background build snapshot: " + error.Message); }
    }

    public static ProjectSnapshot CaptureProject(CodeWindow entry)
    {
        if (ReadOnlyLibraryWindows.IsReadOnly(entry))
            throw new InvalidOperationException("Scala library sources are read-only and cannot be run.");
        if (store == null || !enabled)
            throw new InvalidOperationException("Scala mode is not enabled for this save.");
        EnsureCurrentSave();
        if (entry.workspace != currentMainSim!.workspace)
            throw new InvalidOperationException("Code window belongs to another save.");
        foreach (CodeWindow window in entry.workspace.codeWindows.Values)
            UpdateTokens.Invoke(window, null);
        var buffers = ReadBuffers(entry.workspace);
        store.SaveSources(saveDirectory, saveName, true, buffers);
        store.SaveLayout(saveDirectory, true, CaptureCurrentLayout(entry.workspace, true));
        watchedSources = new Dictionary<string, string>(buffers, StringComparer.OrdinalIgnoreCase);
        return store.Snapshot(saveDirectory, entry.fileName, buffers);
    }

    internal static bool MatchesRun(IReadOnlyDictionary<string, string> revision, IEnumerable<string> involved)
    {
        if (currentMainSim == null || !SessionIdentityMatches()) return false;
        foreach (string name in involved)
            if (!revision.TryGetValue(name, out string source) ||
                !currentMainSim.workspace.codeWindows.TryGetValue(name, out var window) || window.CodeInput.text != source)
                return false;
        return true;
    }

    public static void BeginRun(CodeWindow entry)
    {
        foreach (var code in entry.workspace.codeWindows.Values)
            CloseError.Invoke(code, null);
        entry.workspace.activeWindow = entry.GetComponent<Window>();
        entry.GetComponent<Window>().MoveToFront();
        entry.StartExecutionMode();
        entry.SetExecutionColor();
    }

    public static void EndRun()
    {
        pausedMarker = null;
        if (currentMainSim == null || currentMainSim.workspace == null)
            return;
        foreach (CodeWindow window in currentMainSim.workspace.codeWindows.Values)
            window.StopExecutionMode();
    }

    public static void SetError(string file, int line, int column, string message, string? fallbackFile = null, bool sourceHasNativeWindow = true,
        int? endLine = null, int? endColumn = null)
    {
        if (currentMainSim == null)
        {
            log(message);
            return;
        }
        string module = Path.GetFileNameWithoutExtension(file);
        if (!sourceHasNativeWindow || !currentMainSim.workspace.codeWindows.TryGetValue(module, out var window))
        {
            log(file + ":" + line + ": " + message);
            if (fallbackFile == null || !currentMainSim.workspace.codeWindows.TryGetValue(Path.GetFileNameWithoutExtension(fallbackFile), out window))
                return;
            // Package sources can be outside the native top-level windows.
            // Keep the error visible at the entry point with its actual location.
            message = Path.GetFileName(file) + ":" + line + ": " + message;
            line = column = 1;
            endLine = endColumn = null;
        }
        string text = window.CodeInput.text;
        if (text.Length == 0)
        {
            log(message);
            return;
        }
        int lineStart = 0;
        for (int currentLine = 1; currentLine < Math.Max(1, line); currentLine++)
        {
            int newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
                break;
            lineStart = newline + 1;
        }
        int lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0)
            lineEnd = text.Length;
        if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
            lineEnd--;
        int lastOnLine = Math.Min(text.Length - 1, Math.Max(lineStart, lineEnd - 1));
        int index = (int)Math.Min(lastOnLine, (long)lineStart + Math.Max(0L, (long)column - 1));
        int endIndex = index;
        if (endColumn.HasValue && endColumn.Value > 0 && (endLine ?? line) >= line)
        {
            // Compiler diagnostic endpoints are exclusive. The native popup draws one
            // rectangle, so a multiline expression highlights its first line.
            long exclusiveEnd = (endLine ?? line) > line ? (long)lastOnLine + 1 : (long)lineStart + endColumn.Value - 1;
            endIndex = (int)Math.Max(index, Math.Min(lastOnLine, exclusiveEnd - 1));
        }
        var nativeWindow = window.GetComponent<Window>();
        nativeWindow.SetMinmized(false);
        nativeWindow.MoveToFront();
        currentMainSim.workspace.activeWindow = nativeWindow;
        window.CodeInput.ForceLabelUpdate();
        window.CodeInput.textComponent.ForceMeshUpdate(true, true);
        var textInfo = window.CodeInput.textComponent.textInfo;
        int count = Math.Min(textInfo.characterCount, textInfo.characterInfo.Length);
        if (count == 0)
        {
            log(message);
            return;
        }
        int CharacterAt(int sourceIndex)
        {
            // TMP represents a surrogate pair as one character. Its index
            // maps back to the original UTF-16 source used by the protocol.
            int character = 0;
            while (character + 1 < count && textInfo.characterInfo[character + 1].index <= sourceIndex)
                character++;
            return character;
        }
        int startCharacter = CharacterAt(index);
        window.SetErrorMessage(message, startCharacter, CharacterAt(endIndex));
        if (OptionHolder.GetString("focus errors", "enabled") == "enabled")
            currentMainSim.workspace.MoveCameraTo(nativeWindow, alwaysMoveWindow: true, charIndex: startCharacter, inputField: window.CodeInput);
    }

    private static string ActiveSaveName() => OptionHolder.GetString("activeSave", "Save0");

    private static bool SessionIdentityMatches() => !loading && currentMainSim != null &&
        ReferenceEquals(currentMainSim, MainSim.Inst) && StringComparer.Ordinal.Equals(saveName, ActiveSaveName());

    private static void BlockSession(string message)
    {
        bool first = !sourceWriteBlocked;
        sourceWriteBlocked = true;
        pendingViewport = null;
        try
        {
            StopRun();
        }
        catch (Exception error) { log("Blocked-session Scala cleanup: " + error.Message); }
        try
        {
            if (currentMainSim != null && ReferenceEquals(currentMainSim, MainSim.Inst) && NativeSimulation.GetValue(currentMainSim) != null && currentMainSim.IsExecuting())
                currentMainSim.StopMainExecution();
        }
        catch (Exception error) { log("Blocked-session native cleanup: " + error.Message); }
        if (first)
            log("Save language/session blocked: " + message);
    }

    private static void EnsureCurrentSave()
    {
        if (store == null)
            throw new InvalidOperationException("Scala editor is not initialized.");
        try
        {
            if (!SessionIdentityMatches())
                throw new InvalidOperationException("Load the selected save before running or saving code.");
            if (!languageResolved || sourceWriteBlocked)
                throw new InvalidOperationException("Save loading is blocked. Repair the reported project error and reload this save; its language cannot be changed.");
            if (store.ReadLanguage(saveDirectory) != loadedLanguage || store.ReadManifest(saveDirectory).SaveId != loadedSaveId)
                throw new InvalidOperationException("This save's language or identity changed on disk. Restore its metadata and reload the save.");
        }
        catch (Exception error)
        {
            BlockSession(error.Message);
            throw;
        }
    }

    internal static void SaveRenamed(string oldName, string newName)
    {
        if (store == null || currentMainSim == null || !StringComparer.Ordinal.Equals(saveName, oldName))
            return;
        try
        {
            if (loading || !languageResolved || !ReferenceEquals(currentMainSim, MainSim.Inst))
                throw new InvalidOperationException("Cannot rebind an unloaded save.");
            string renamedDirectory = Saver.GetPathOfSaveDirectory(newName);
            if (store.ReadLanguage(renamedDirectory) != loadedLanguage || store.ReadManifest(renamedDirectory).SaveId != loadedSaveId)
                throw new InvalidOperationException("Renamed save does not match the loaded save's language and identity.");
            saveName = newName;
            saveDirectory = renamedDirectory;
            // The chooser updates activeSave after this native Saver postfix.
            // Do not save or run any code during that brief interval.
            pendingViewport = null;
        }
        catch (Exception error) { BlockSession("Save rename: " + error.Message); }
    }

    internal static void BeforeLoad(MainSim main)
    {
        if (store == null)
            return;
        StopRun();
        // Menu.Start calls Saver.Load before SetupSim has created the first
        // simulation. Native IsExecuting dereferences that simulation.
        if (NativeSimulation.GetValue(main) != null && main.IsExecuting())
            main.StopMainExecution();
        loading = true;
        pendingViewport = null;
        sourceWriteBlocked = false;
        watchedSources = null;
        compileBuffers = null;
        compilePending = false;
        languageResolved = false;
        loadedLanguage = null;
        loadedSaveId = null;
        currentMainSim = main;
        saveName = ActiveSaveName();
        saveDirectory = Saver.GetPathOfSaveDirectory(saveName);
        try
        {
            loadedLanguage = store.FreezeLanguage(saveDirectory, saveName);
            loadedSaveId = store.ReadManifest(saveDirectory).SaveId;
            enabled = loadedLanguage == SaveLanguage.NativeScala;
            languageResolved = true;
        }
        catch (Exception error)
        {
            enabled = true; // Unknown language must not execute or save as Default.
            sourceWriteBlocked = true;
            log("Save language could not be loaded: " + error.Message);
        }
    }

    internal static void AfterLoad(MainSim main)
    {
        if (store == null)
            return;
        currentMainSim = main;
        if (!languageResolved)
        {
            loading = false;
            Saver.StopFileWatcher();
            ReportLoadFailure("The save's language metadata could not be read. Execution and source saving are blocked. Restore the metadata and reload the save.");
            return;
        }
        if (!enabled)
        {
            loading = false;
            return;
        }
        Dictionary<string, string>? vanillaBuffers = null;
        WorkspaceLayout? vanillaLayout = null;
        bool replacing = false;
        try
        {
            vanillaBuffers = ReadBuffers(main.workspace);
            vanillaLayout = NativeWindowLayout.Capture(main.workspace);
            var manifest = store.ReadManifest(saveDirectory);
            // Canvas Start/layout may not have placed the UI parent yet.
            // Retain a previously saved view, or migrate after initialization.
            vanillaLayout.FarmWorldX = manifest.VanillaLayout?.FarmWorldX;
            vanillaLayout.FarmWorldY = manifest.VanillaLayout?.FarmWorldY;
            vanillaLayout.FarmViewX = manifest.VanillaLayout?.FarmViewX;
            vanillaLayout.FarmViewY = manifest.VanillaLayout?.FarmViewY;
            if (manifest.VanillaLayout != null)
                vanillaLayout.Zoom = manifest.VanillaLayout.Zoom;
            else
                store.SaveLayout(saveDirectory, false, vanillaLayout);
            var sources = store.ReadSources(saveDirectory);
            Saver.StopFileWatcher();
            sourceWriteBlocked = true;
            replacing = true;
            ReplaceBuffers(main.workspace, sources, manifest.ScalaLayout);
            sourceWriteBlocked = false;
            loading = false;
            QueueViewport(main.workspace, manifest.ScalaLayout, true);
            SaveCurrent();
            log("Loaded Scala project for " + saveName);
        }
        catch (Exception error)
        {
            enabled = true;
            sourceWriteBlocked = true;
            pendingViewport = null;
            try
            {
                if (replacing)
                    ReplaceBuffers(main.workspace, vanillaBuffers!, vanillaLayout);
                if (replacing && vanillaLayout != null)
                    NativeWindowLayout.Restore(main.workspace, vanillaLayout);
            }
            catch (Exception recovery)
            {
                enabled = true;
                sourceWriteBlocked = true;
                pendingViewport = null;
                Saver.StopFileWatcher();
                log("Scala display recovery failed: " + recovery.Message);
            }
            Saver.StopFileWatcher();
            loading = false;
            ReportLoadFailure("Native Scala project could not load: " + error.Message + ". Execution and source saving are blocked; saved Scala files are retained. Repair the project and reload this save.");
        }
    }

    private static void ReportLoadFailure(string message)
    {
        log(message);
        Plugin.Instance?.ReportFailure(message);
        try
        {
            LoadFailure?.Invoke(message);
        }
        catch (Exception error) { log("Could not display save load error: " + error.Message); }
    }

    internal static bool SaveCode(MainSim main)
    {
        if (store == null)
            return true;
        try
        {
            EnsureCurrentSave();
            if (!ReferenceEquals(main, currentMainSim))
                throw new InvalidOperationException("Source saver belongs to another save.");
            if (!enabled)
                return true;
            SaveCurrent();
        }
        catch (Exception error) { ReportSaveFailure("Scala source was not saved: " + error.Message); }
        return false; // Never fall back to putting Scala text in vanilla .py files.
    }

    internal static bool SaveProgress(MainSim main)
    {
        if (store == null)
            return true;
        try
        {
            EnsureCurrentSave();
            if (!ReferenceEquals(main, currentMainSim))
                throw new InvalidOperationException("World saver belongs to another save.");
            return true;
        }
        catch (Exception error)
        {
            ReportSaveFailure("World progress was not saved: " + error.Message);
            return false; // Skip serialization, backups and native dirty=false.
        }
    }

    internal static void SaveCurrent()
    {
        if (!enabled || store == null || loading || synchronizing || currentMainSim == null)
            return;
        EnsureCurrentSave();
        var buffers = ReadBuffers(currentMainSim.workspace);
        store.SaveSources(saveDirectory, saveName, true, buffers);
        store.SaveLayout(saveDirectory, true, CaptureCurrentLayout(currentMainSim.workspace, true));
        watchedSources = new Dictionary<string, string>(buffers, StringComparer.OrdinalIgnoreCase);
    }

    private static void SafeSaveCurrent()
    {
        try
        {
            SaveCurrent();
        }
        catch (Exception error) { ReportSaveFailure("Scala source was not saved: " + error.Message); }
    }

    private static void ReportSaveFailure(string message)
    {
        log(message);
        // Switching the selected save can retire the old world before its
        // scene disappears. That blocked old-session write is expected.
        if (SessionIdentityMatches())
            Plugin.Instance?.ReportFailure(message);
    }

    private static Dictionary<string, string> ReadBuffers(Workspace workspace)
        => workspace.codeWindows.Values.ToDictionary(x => x.fileName, x => x.CodeInput.text.Replace("\r", ""), StringComparer.OrdinalIgnoreCase);

    private static void ReplaceBuffers(Workspace workspace, IDictionary<string, string> sources, WorkspaceLayout? layout = null)
    {
        // Validate and snapshot all inputs before the first native window call.
        // A failed replacement must never be mistaken for a user deletion.
        if (layout != null && !SourceStore.IsUsableLayout(layout))
            throw new IOException("Unsupported or invalid Scala workspace layout.");
        sources = sources.Count == 0 ? new Dictionary<string, string> { ["main"] = "" }
            : new Dictionary<string, string>(sources, StringComparer.OrdinalIgnoreCase);
        foreach (string name in sources.Keys)
            SourceStore.ValidateFileName(name);
        synchronizing = true;
        try
        {
            ReadOnlyLibraryWindows.CloseAll(workspace);
            if (layout != null)
                NativeWindowLayout.UndockAll(workspace);
            // Native OpenCodeWindow refuses any existing window with this
            // name, including docs windows belonging to the previous mode.
            foreach (var window in workspace.OpenWindows.ToArray())
                if (window.GetComponent<CodeWindow>() == null && sources.ContainsKey(window.windowName))
                    window.Close();
            foreach (CodeWindow window in workspace.codeWindows.Values.ToArray())
            {
                if (!sources.ContainsKey(window.fileName))
                    window.GetComponent<Window>().Close();
                else
                {
                    CloseError.Invoke(window, null);
                    if (window.CodeInput.text != sources[window.fileName])
                        window.CodeInput.text = sources[window.fileName];
                }
            }
            int offset = 0;
            foreach (var source in sources)
                if (!workspace.codeWindows.Values.Any(x => StringComparer.OrdinalIgnoreCase.Equals(x.fileName, source.Key)))
                {
                    var saved = NativeWindowLayout.Find(layout, source.Key);
                    Vector2 position = saved != null ? NativeWindowLayout.Position(workspace, saved) : NativeWindowLayout.NewWindowPosition(workspace, offset);
                    Vector2 size = saved != null ? new Vector2(saved.Width, saved.Height) : default;
                    workspace.OpenCodeWindow(source.Key, source.Value, position, size);
                    offset += 35;
                }
            if (!SourceMerge.Equal(sources, ReadBuffers(workspace)))
                throw new IOException("Scala workspace replacement did not restore every source buffer; source saving is blocked. " + DescribeBufferDifferences(sources, ReadBuffers(workspace)));
            if (layout != null)
                NativeWindowLayout.Restore(workspace, layout, restoreViewport: false);
            if (!SourceMerge.Equal(sources, ReadBuffers(workspace)))
                throw new IOException("Scala workspace layout changed the source buffer set; source saving is blocked. " + DescribeBufferDifferences(sources, ReadBuffers(workspace)));
        }
        catch { sourceWriteBlocked = true; throw; }
        finally { synchronizing = false; }
    }

    private static string DescribeBufferDifferences(IDictionary<string, string> expected, IDictionary<string, string> actual)
    {
        return string.Join("; ", expected.Keys.Union(actual.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(name => !expected.TryGetValue(name, out var wanted) || !actual.TryGetValue(name, out var found) || wanted != found)
            .Take(10).Select(name =>
            {
                bool wanted = expected.TryGetValue(name, out var original);
                bool found = actual.TryGetValue(name, out var current);
                if (!wanted)
                    return name + ".scala: unexpected buffer (" + current!.Length + " chars)";
                if (!found)
                    return name + ".scala: missing buffer (expected " + original!.Length + " chars)";
                int first = 0;
                while (first < original!.Length && first < current!.Length && original[first] == current[first])
                    first++;
                return name + ".scala: expected " + original.Length + " chars, actual " + current!.Length + " chars, first difference at " + first;
            }));
    }

    private static void QueueViewport(Workspace workspace, WorkspaceLayout? layout, bool scala)
    {
        NativeWindowLayout.StopCamera(workspace);
        pendingViewport = new PendingViewport
        {
            Workspace = workspace,
            Layout = layout,
            Scala = scala,
            SaveDirectory = saveDirectory,
            QueuedFrame = Time.frameCount,
            DueFrame = Time.frameCount + 3,
            ParentPosition = NativeWindowLayout.ViewParentPosition(workspace)
        };
    }

    private static WorkspaceLayout CaptureCurrentLayout(Workspace workspace, bool scala)
    {
        var captured = NativeWindowLayout.Capture(workspace);
        var pending = pendingViewport;
        if (pending != null && pending.Workspace == workspace && pending.Scala == scala)
        {
            // A save during Canvas initialization must not replace the desired
            // anchor with the UI parent's temporary world position.
            captured.FarmWorldX = pending.Layout?.FarmWorldX;
            captured.FarmWorldY = pending.Layout?.FarmWorldY;
            captured.FarmViewX = pending.Layout?.FarmViewX;
            captured.FarmViewY = pending.Layout?.FarmViewY;
            if (pending.Layout != null)
            {
                captured.Zoom = pending.Layout.Zoom;
                captured.PanX = pending.Layout.PanX;
                captured.PanY = pending.Layout.PanY;
            }
        }
        return captured;
    }

    private static void ApplyPendingViewport()
    {
        var pending = pendingViewport;
        if (pending == null || pending.Failures >= 3 || Time.frameCount < pending.DueFrame)
            return;
        if (currentMainSim == null || pending.Workspace != currentMainSim.workspace ||
            pending.SaveDirectory != saveDirectory || pending.Scala != enabled || sourceWriteBlocked)
        {
            pendingViewport = null;
            return;
        }
        try
        {
            Canvas.ForceUpdateCanvases();
            Vector3 parent = NativeWindowLayout.ViewParentPosition(pending.Workspace);
            if (Math.Abs(parent.x - pending.ParentPosition.x) > 0.01f || Math.Abs(parent.y - pending.ParentPosition.y) > 0.01f ||
                Math.Abs(parent.z - pending.ParentPosition.z) > 0.01f)
            {
                pending.ParentPosition = parent;
                pending.DueFrame = Time.frameCount + 1;
                return;
            }
            Vector3? before = NativeWindowLayout.FarmWorldPosition(pending.Workspace);
            Vector3? beforeView = before.HasValue ? pending.Workspace.zoomContainer.InverseTransformPoint(before.Value) : (Vector3?)null;
            if (pending.Layout != null)
                NativeWindowLayout.RestoreViewport(pending.Workspace, pending.Layout);
            else
                NativeWindowLayout.FocusCodeWindow(pending.Workspace);
            Vector3? after = NativeWindowLayout.FarmWorldPosition(pending.Workspace);
            Vector3? afterView = after.HasValue ? pending.Workspace.zoomContainer.InverseTransformPoint(after.Value) : (Vector3?)null;
            pendingViewport = null;
            log((pending.Scala ? "Scala" : "Vanilla") + " viewport applied: queued frame " + pending.QueuedFrame +
                ", applied frame " + Time.frameCount + ", UI-local (" + beforeView?.x + ", " + beforeView?.y + ") -> (" + afterView?.x + ", " + afterView?.y +
                "), world (" + before?.x + ", " + before?.y + ") -> (" + after?.x + ", " + after?.y + ").");
        }
        catch (Exception error)
        {
            pending.Failures++;
            pending.DueFrame = Time.frameCount + 3;
            log("Workspace viewport restore failed (" + pending.Failures + "/3); saved target retained: " + error.Message);
            return;
        }
        try
        {
            if (pending.Scala)
                SaveCurrent();
            else
                store!.SaveLayout(saveDirectory, false, NativeWindowLayout.Capture(pending.Workspace));
        }
        catch (Exception error) { log("Workspace viewport applied, but layout was not saved: " + error.Message); }
    }

    internal static void StopRun()
    {
        if (stopping || !running())
            return;
        stopping = true;
        try
        {
            stop();
            EndRun();
        }
        finally { stopping = false; }
    }

    internal static bool RunPressed(CodeWindow window)
    {
        if (!CheckExecutionSession(window))
            return false;
        if (!enabled)
            return true;
        InvokeExecutionControl(window, "Run", () =>
        {
            if (running())
            {
                if (PlayWhileRunning?.Invoke(window) != true)
                    StopRun();
            }
            else
                run(window);
        });
        return false;
    }

    internal static bool StepPressed(CodeWindow window)
    {
        if (!CheckExecutionSession(window))
            return false;
        if (!enabled)
            return true;
        InvokeExecutionControl(window, "Step", () =>
        {
            if (StepRequested != null)
                StepRequested(window);
            else
                log("Scala stepping is not available in this build yet.");
        });
        return false;
    }

    private static bool CheckExecutionSession(CodeWindow window)
    {
        if (ReadOnlyLibraryWindows.IsReadOnly(window))
            return false;
        try
        {
            EnsureCurrentSave();
            if (!ReferenceEquals(window.workspace, currentMainSim!.workspace))
                throw new InvalidOperationException("Code window belongs to another save.");
            return true;
        }
        catch (Exception error)
        {
            log("Code execution blocked: " + error.Message);
            SetError(window.fileName, 1, 1, error.Message);
            return false;
        }
    }

    private static void InvokeExecutionControl(CodeWindow window, string action, Action invoke)
    {
        try
        {
            window.workspace.activeWindow = window.GetComponent<Window>();
            invoke();
        }
        catch (Exception error)
        {
            log("Scala " + action + ": " + error);
            try
            {
                StopRun();
            }
            catch (Exception cleanup) { log("Scala execution cleanup: " + cleanup); }
            // Native cleanup can clear diagnostics. Finish it before showing
            // the original failure, including when the host already stopped.
            try
            {
                EndRun();
                SetError(window.fileName, 1, 1, error.Message);
            }
            catch (Exception display) { log("Scala execution diagnostic: " + display); }
        }
    }

    [HarmonyPatch(typeof(CodeWindow), nameof(CodeWindow.PressExecuteOrStop))]
    private static class PlayPatch
    {
        private static bool Prefix(CodeWindow __instance) => RunPressed(__instance);
    }

    [HarmonyPatch(typeof(CodeWindow), nameof(CodeWindow.Parse))]
    private static class ParsePatch
    {
        private static bool Prefix(CodeWindow __instance, ref Node __result)
        {
            // Embedded documentation examples remain native Python, even in a Scala save.
            if (__instance.GetComponentInParent<CodeExample>() != null) return true;
            if (ReadOnlyLibraryWindows.IsReadOnly(__instance))
            {
                __result = null!;
                return false;
            }
            if (loading)
            {
                if (languageResolved && !enabled)
                    return true;
                __result = null!;
                return false;
            }
            if (!CheckExecutionSession(__instance))
            {
                __result = null!;
                return false;
            }
            if (!enabled)
                return true;
            UpdateTokens.Invoke(__instance, null);
            CloseError.Invoke(__instance, null);
            if (MainSim.Inst != null)
                MainSim.Inst.dirty = true;
            __result = ParseOverride?.Invoke(__instance)!;
            return false;
        }
    }

    [HarmonyPatch(typeof(CodeWindow), nameof(CodeWindow.PressStepByStepButton))]
    private static class StepPatch
    {
        private static bool Prefix(CodeWindow __instance) => StepPressed(__instance);
    }

    // SaveAsync captures code and progress on Unity's thread before queuing IO.
    // Scala sources stay in their sidecar project; the native writer must never
    // copy the displayed Scala buffers into vanilla Python files.
    [HarmonyPatch(typeof(Saver), "SaveAsync")]
    private static class SavePatch
    {
        private static bool Prefix(MainSim mainSim, bool saveProgress, ref bool saveCode,
            ref System.Threading.Tasks.Task<bool> __result, out int __state)
        {
            __state = -1;
            if (store == null)
                return true;
            try
            {
                EnsureCurrentSave();
                if (!ReferenceEquals(mainSim, currentMainSim))
                    throw new InvalidOperationException("Save belongs to another session.");
                if (enabled && saveCode)
                {
                    SaveCurrent();
                    if (saveProgress) __state = mainSim.SaveRevision;
                    saveCode = false;
                }
                return true;
            }
            catch (Exception error)
            {
                ReportSaveFailure("Save was not written: " + error.Message);
                __result = System.Threading.Tasks.Task.FromResult(false);
                return false;
            }
        }
        private static void Postfix(MainSim mainSim, int __state, ref System.Threading.Tasks.Task<bool> __result)
        {
            if (__state >= 0) __result = Finish(__result, mainSim, __state);
        }
        private static async System.Threading.Tasks.Task<bool> Finish(System.Threading.Tasks.Task<bool> write, MainSim main, int revision)
        {
            bool saved = await write;
            if (saved && ReferenceEquals(main, currentMainSim) && SessionIdentityMatches()) main.MarkSaved(revision);
            return saved;
        }
    }

    [HarmonyPatch(typeof(Saver), "CreateSaveGame")]
    private static class SaveLayoutPatch
    {
        private static void Postfix(MainSim mainSim, SaveGame __result)
        {
            if (!enabled || store == null || loading || synchronizing ||
                !ReferenceEquals(mainSim, currentMainSim))
                return;
            EnsureCurrentSave();
            var vanilla = store.ReadManifest(saveDirectory).VanillaLayout;
            if (vanilla != null)
                NativeWindowLayout.ApplyToNativeSave(__result, vanilla);
            if (!sourceWriteBlocked)
                store.SaveLayout(saveDirectory, true, CaptureCurrentLayout(mainSim.workspace, true));
        }
    }

    [HarmonyPatch(typeof(Saver), nameof(Saver.Load))]
    private static class LoadPatch
    {
        private static void Prefix(MainSim mainSim) => BeforeLoad(mainSim);
        private static void Postfix(MainSim mainSim) => AfterLoad(mainSim);
    }

    [HarmonyPatch(typeof(Saver), nameof(Saver.StartFileWatcher))]
    private static class WatcherPatch
    {
        private static bool Prefix() => !BlocksNativeExecution;
    }

    [HarmonyPatch(typeof(Saver), nameof(Saver.ApplyCodeChanges))]
    private static class WatcherApplyPatch
    {
        private static bool Prefix() => !BlocksNativeExecution;
    }

    [HarmonyPatch(typeof(Saver), "EnableDisableFileWatcher")]
    private static class WatcherOptionPatch
    {
        private static bool Prefix() => !BlocksNativeExecution;
    }

    [HarmonyPatch(typeof(MainSim), nameof(MainSim.StopMainExecution))]
    private static class StopPatch
    {
        private static void Prefix()
        {
            if (enabled)
                StopRun();
        }
    }

    [HarmonyPatch(typeof(CodeWindow), "UpdateTokens")]
    private static class EditingPatch
    {
        private static readonly FieldInfo PreviousText = AccessTools.Field(typeof(CodeWindow), "prevText");
        private static void Prefix(CodeWindow __instance, out bool __state)
        {
            bool scala = enabled && __instance.GetComponentInParent<CodeExample>() == null && !ReadOnlyLibraryWindows.IsReadOnly(__instance);
            __state = false;
            if (scala && !__instance.CodeInput.readOnly && OptionHolder.GetString("tabs to spaces") == "enabled")
            {
                var input = __instance.CodeInput;
                var edit = ScalaWhitespace.ExpandTabs(input.text, input.selectionStringAnchorPosition, input.selectionStringFocusPosition);
                if (edit.Source != input.text)
                {
                    input.SetTextWithoutNotify(edit.Source);
                    input.stringPosition = edit.Focus;
                    input.selectionStringAnchorPosition = edit.Anchor;
                    input.selectionStringFocusPosition = edit.Focus;
                }
            }
            __state = scala;
            if (__state)
                scalaTokenUpdateDepth++;
            if (__state && !synchronizing && running() && IsInvolvedInRun?.Invoke(__instance) != false && (string?)PreviousText.GetValue(__instance) != __instance.CodeInput.text)
                StopRun();
        }
        private static void Postfix(bool __result)
        {
            if (enabled && __result && MainSim.Inst != null)
                MainSim.Inst.dirty = true;
        }
        private static Exception? Finalizer(Exception? __exception, bool __state)
        {
            if (__state)
                scalaTokenUpdateDepth--;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(OptionHolder), nameof(OptionHolder.GetString))]
    private static class IndentationOptionPatch
    {
        private static bool Prefix(string __0, ref string __result)
        {
            if (scalaTokenUpdateDepth == 0 || __0 != "tabs to spaces")
                return true;
            // Native UpdateTokens otherwise rewrites spaces or tabs, including
            // literal content. Preserve Scala source while retaining all
            // other tokenization and the user's actual indentation preference.
            __result = "preserve-scala-source";
            return false;
        }
    }

    [HarmonyPatch(typeof(OptionHolder), nameof(OptionHolder.SetOption))]
    private static class IndentationChanged
    {
        private static void Postfix(string __0)
        {
            if (!enabled || currentMainSim == null || __0 != "tabs to spaces" || OptionHolder.GetString(__0) != "enabled")
                return;
            foreach (var window in currentMainSim.workspace.codeWindows.Values)
                if (!window.CodeInput.readOnly)
                    UpdateTokens.Invoke(window, null);
        }
    }

    [HarmonyPatch(typeof(CodeWindow), nameof(CodeWindow.OnCodeCommit))]
    private static class CommitPatch
    {
        private static void Postfix()
        {
            if (enabled)
                SafeSaveCurrent();
        }
    }

    [HarmonyPatch(typeof(Workspace), nameof(Workspace.OpenCodeWindow))]
    private static class NewWindowPatch
    {
        private static void Postfix()
        {
            if (enabled)
                SafeSaveCurrent();
        }
    }

    // CodeWindow.Rename updates fileName after Workspace.RenameWindow returns.
    // Persist only after that outer method has committed both identities.
    [HarmonyPatch(typeof(CodeWindow), nameof(CodeWindow.Rename))]
    private static class RenameWindowPatch
    {
        private static void Postfix()
        {
            if (enabled)
                SafeSaveCurrent();
        }
    }

    [HarmonyPatch(typeof(Workspace), nameof(Workspace.RemoveWindow))]
    private static class RemoveWindowPatch
    {
        private static void Postfix()
        {
            if (enabled)
                SafeSaveCurrent();
        }
    }
}
