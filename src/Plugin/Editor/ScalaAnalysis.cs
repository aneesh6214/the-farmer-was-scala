using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace FarmerScala.Editor;

/// <summary>Exact-source, immediate local index with a nonblocking compiler-analysis extension.</summary>
internal static class ScalaAnalysis
{
    internal static IScalaAnalysisProvider? Provider
    {
        get; set;
    }
    internal static long SourceVersion => version;
    private static Workspace? workspace;
    private static string project = "";
    private static Dictionary<string, string> sources = new Dictionary<string, string>(StringComparer.Ordinal);
    private static ScalaCompletionModel? model;
    private static readonly Dictionary<string, IReadOnlyList<SemanticSpan>> spans = new Dictionary<string, IReadOnlyList<SemanticSpan>>(StringComparer.Ordinal);
    private static readonly Dictionary<string, SemanticColorSnapshot> displayColors = new Dictionary<string, SemanticColorSnapshot>(StringComparer.Ordinal);
    private static long version, paintedVersion;
    private static bool warned;
    private static string installRoot = "";
    private sealed class Pending
    {
        public IReadOnlyDictionary<string, string> Sources = null!;
        public CodeWindow Window = null!;
        public int Offset;
        public DateTime Expires;
        public Action<AnalysisResolution>? Resolve;
        public Action<List<string>>? Complete;
    }
    private static readonly List<Pending> callbacks = new List<Pending>();

    public static void Initialize(string root)
    {
        Dispose();
        installRoot = root;
        ScalaCompletionUnlocks.IsUnlocked = key => MainSim.Inst != null && MainSim.Inst.IsUnlocked(key);
        // Formatting needs only the bundled parser, not a successful project
        // build. The owned helper process still starts lazily on first request.
        if (File.Exists(Path.Combine(root, "tools", "analysis", "classpath.txt")))
            Provider = new CompilerAnalysisProvider(root);
    }
    internal static void CompilerReady(string artifact)
    {
        if (installRoot.Length == 0 || Provider != null && !(Provider is CompilerAnalysisProvider))
            return;
        if (!File.Exists(Path.Combine(installRoot, "tools", "analysis", "classpath.txt")))
            return;
        if (!(Provider is CompilerAnalysisProvider compiler))
            Provider = compiler = new CompilerAnalysisProvider(installRoot);
        compiler.Configure(artifact);
    }
    private static bool Capture(CodeWindow window)
    {
        if (window == null || !EditorIntegration.IsScalaWindow(window))
            return false;
        var next = window.workspace.codeWindows.Values.ToDictionary(w => w.fileName, w => w.CodeInput.text, StringComparer.Ordinal);
        // Large pasted projects remain editable; compiler analysis can handle them asynchronously.
        if (next.Count > 128 || next.Values.Sum(s => (long)s.Length) > 250_000)
        {
            model = null;
            return false;
        }
        string directory = EditorIntegration.ProjectDirectory;
        if (!ReferenceEquals(workspace, window.workspace) || directory != project || sources.Count != next.Count ||
            next.Any(p => !sources.TryGetValue(p.Key, out var old) || old != p.Value))
        {
            if (!ReferenceEquals(workspace, window.workspace) || directory != project)
                displayColors.Clear();
            else
            {
                foreach (string removed in displayColors.Keys.Where(name => !next.ContainsKey(name)).ToArray())
                    displayColors.Remove(removed);
                foreach (string name in displayColors.Keys.ToArray())
                    displayColors[name] = displayColors[name].Rebase(next[name]);
            }
            model = new ScalaCompletionModel(next);
            workspace = window.workspace;
            project = directory;
            sources = next;
            spans.Clear();
            version++;
        }
        return model != null && sources.ContainsKey(window.fileName);
    }
    public static bool TryResolve(CodeWindow window, int offset, out AnalysisResolution result)
    {
        result = new AnalysisResolution();
        try
        {
            if (!Capture(window))
                return false;
            var local = model!.Resolve(window.fileName, offset);
            if (Provider != null && Provider.TryResolve(sources, window.fileName, offset, out result))
            {
                result = AnalysisResolutionMerger.Merge(local, result);
                return true;
            }
            result = local;
            return Provider == null || result.Definition != null || result.IsGameBuiltin || !string.IsNullOrWhiteSpace(result.HoverText);
        }
        catch (Exception error) { Report(error); return false; }
    }
    public static void RequestResolve(CodeWindow window, int offset, Action<AnalysisResolution> callback)
    {
        if (TryResolve(window, offset, out var result))
        {
            callback(result);
            return;
        }
        if (Provider != null && Capture(window) && callbacks.Count < 32)
            callbacks.Add(new Pending { Window = window, Sources = sources, Offset = offset, Resolve = callback, Expires = DateTime.UtcNow.AddSeconds(20) });
    }
    internal static void RequestCompilerResolve(CodeWindow window, int offset, Action<AnalysisResolution> callback)
    {
        if (Provider == null || !Capture(window))
            return;
        if (Provider.TryResolve(sources, window.fileName, offset, out var result))
        {
            callback(AnalysisResolutionMerger.Merge(model!.Resolve(window.fileName, offset), result));
            return;
        }
        if (callbacks.Count < 32)
            callbacks.Add(new Pending
            {
                Window = window,
                Sources = sources,
                Offset = offset,
                Resolve = callback,
                Expires = DateTime.UtcNow.AddSeconds(20)
            });
    }
    internal static List<string> NativeSuggestions(CodeWindow window, int cursor, List<string> names)
    {
        if (!Capture(window))
            return ScalaCompletionUnlocks.Native(names);
        var local = new HashSet<string>(model!.Complete(window.fileName, cursor), StringComparer.Ordinal);
        var allowed = new HashSet<string>(ScalaCompletionUnlocks.Native(names), StringComparer.Ordinal);
        return names.Where(n => local.Contains(n) || allowed.Contains(n)).ToList();
    }
    public static void Complete(CodeWindow window, int offset, Action<List<string>> callback)
    {
        try
        {
            if (!Capture(window))
                return;
            if (ScalaEditingContext.IsDocumentationTag(sources[window.fileName], offset))
            {
                callback(new List<string> { "param", "tparam", "return", "throws", "exception", "note", "example", "see", "deprecated", "since" });
                return;
            }
            if (Provider != null && Provider.TryComplete(sources, window.fileName, offset, out var provided))
                callback(provided);
            else
            {
                callback(model!.Complete(window.fileName, offset));
                if (Provider != null && callbacks.Count < 32)
                    callbacks.Add(new Pending
                    {
                        Window = window,
                        Sources = sources,
                        Offset = offset,
                        Complete = callback,
                        Expires = DateTime.UtcNow.AddSeconds(20)
                    });
            }
        }
        catch (Exception error) { Report(error); }
    }
    internal static bool TryMembers(CodeWindow window, int offset, out List<string> result)
    {
        result = new List<string>();
        try
        {
            if (!Capture(window))
                return false;
            if (Provider != null && Provider.TryComplete(sources, window.fileName, offset, out result))
                return true;
            return model!.TryMembers(window.fileName, offset, out result);
        }
        catch (Exception error) { Report(error); return false; }
    }
    // Used only to retain native descriptions of individual game values.
    // Enum completion and enum-type hovers always use Scala analysis.
    internal static bool IsGameCatalogValueReference(CodeWindow window, int cursor)
    {
        if (!Capture(window))
            return false;
        string text = window.CodeInput.text;
        int at = Math.Min(text.Length, Math.Max(0, cursor));
        while (at > 0 && ScalaLexer.NamePart(text[at - 1]))
            at--;
        if (at == 0 || text[at - 1] != '.')
            return false;
        int end = at - 1, start = end;
        while (start > 0 && ScalaLexer.NamePart(text[start - 1]))
            start--;
        string receiver = text.Substring(start, end - start);
        return new[] { "Items", "Entities", "Grounds", "Hats", "Unlocks" }.Contains(receiver) &&
            model!.Resolve(window.fileName, end).IsGameBuiltin;
    }
    public static IReadOnlyList<SemanticSpan>? Spans(CodeWindow window)
    {
        try
        {
            if (!Capture(window))
                return null;
            if (!spans.TryGetValue(window.fileName, out var value))
                spans[window.fileName] = value = model!.Spans(window.fileName);
            if (Provider != null && Provider.TrySpans(sources, window.fileName, out var provided))
            {
                var current = SemanticSpanMerger.Merge(value, provided);
                displayColors[window.fileName] = new SemanticColorSnapshot(sources[window.fileName], current);
                return current; // Exact current results can remove an obsolete color too.
            }
            if (displayColors.TryGetValue(window.fileName, out var previous))
                // Retained compiler classifications outrank the lightweight
                // index for unchanged tokens, including uncolored values.
                // Rebase discards edited identifiers and changed receivers.
                return SemanticSpanMerger.Merge(value, previous.Spans, authoritative: false);
            return value;
        }
        catch (Exception error) { Report(error); return null; }
    }
    internal static bool TryDiagnostics(CodeWindow window, out IReadOnlyList<AnalysisDiagnostic> result)
    {
        result = Array.Empty<AnalysisDiagnostic>();
        try
        {
            return Capture(window) && Provider is IScalaEditorToolsProvider tools && tools.TryDiagnostics(sources, out result);
        }
        catch (Exception error) { Report(error); return false; }
    }
    internal static bool TryFormat(CodeWindow window, out AnalysisFormatResult result)
    {
        result = new AnalysisFormatResult();
        try
        {
            return Capture(window) && Provider is IScalaEditorToolsProvider tools && tools.TryFormat(sources, window.fileName, out result);
        }
        catch (Exception error) { Report(error); return false; }
    }
    internal static bool CaptureRevision(CodeWindow window) => Capture(window);
    public static void Tick()
    {
        try
        {
            var first = MainSim.Inst?.workspace?.codeWindows.Values.FirstOrDefault();
            if (first == null || !EditorIntegration.IsScalaWindow(first))
            {
                if (workspace != null)
                    ClearSnapshot();
                return;
            }
            if (!Capture(first))
                return;
            for (int i = callbacks.Count - 1; i >= 0; i--)
            {
                var pending = callbacks[i];
                if (!ReferenceEquals(pending.Sources, sources) || pending.Window == null || pending.Expires < DateTime.UtcNow)
                {
                    callbacks.RemoveAt(i);
                    continue;
                }
                if (pending.Resolve != null && Provider != null && Provider.TryResolve(sources, pending.Window.fileName, pending.Offset, out var resolution))
                {
                    callbacks.RemoveAt(i);
                    pending.Resolve(AnalysisResolutionMerger.Merge(model!.Resolve(pending.Window.fileName, pending.Offset), resolution));
                }
                else if (pending.Complete != null && Provider != null && Provider.TryComplete(sources, pending.Window.fileName, pending.Offset, out var completion))
                {
                    callbacks.RemoveAt(i);
                    pending.Complete(completion);
                }
            }
            bool compilerUpdated = Provider is CompilerAnalysisProvider compiler && compiler.TakeUpdates();
            if (paintedVersion != version || compilerUpdated)
            {
                paintedVersion = version;
                foreach (var window in first.workspace.codeWindows.Values.ToArray())
                    ScalaHighlighting.Refresh(window);
            }
        }
        catch (Exception error) { Report(error); }
    }
    private static void Report(Exception error)
    {
        if (!warned)
        {
            warned = true;
            Plugin.Instance?.LogInfo("Scala local editor index unavailable: " + error.Message);
        }
    }
    private static void ClearSnapshot()
    {
        workspace = null;
        project = "";
        model = null;
        sources = new Dictionary<string, string>(StringComparer.Ordinal);
        spans.Clear();
        displayColors.Clear();
        callbacks.Clear();
        version++;
        paintedVersion = version;
    }
    public static void Dispose()
    {
        (Provider as IDisposable)?.Dispose();
        Provider = null;
        ClearSnapshot();
    }
}
