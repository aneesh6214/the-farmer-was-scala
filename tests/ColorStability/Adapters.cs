using FarmerScala.Editor;
public sealed class CodeInput
{
    public string text = "";
}
public sealed class CodeWindow
{
    public Workspace workspace = null!;
    public string fileName = "";
    public CodeInput CodeInput = new();
}
public sealed class Workspace
{
    public Dictionary<string, CodeWindow> codeWindows = new();
}
public sealed class MainSim
{
    public static MainSim? Inst; public Workspace workspace = new();
}
namespace FarmerScala
{
    public sealed class Plugin
    {
        public static Plugin? Instance; public void LogInfo(string message) => throw new Exception(message);
    }
}
namespace FarmerScala.Editor
{
    internal static class EditorIntegration
    {
        public static string ProjectDirectory = "save-a"; public static bool IsScalaWindow(CodeWindow window) => true;
    }
    internal static class ScalaHighlighting
    {
        public static void Refresh(CodeWindow window)
        {
        }
    }
    internal sealed class CompilerAnalysisProvider : IScalaAnalysisProvider, IDisposable
    {
        public CompilerAnalysisProvider(string root)
        {
        }
        public void Configure(string artifact)
        {
        }
        public bool TakeUpdates() => false;
        public bool TryResolve(IReadOnlyDictionary<string, string> sources, string file, int offset, out AnalysisResolution value)
        {
            value = new();
            return false;
        }
        public bool TryComplete(IReadOnlyDictionary<string, string> sources, string file, int offset, out List<string> value)
        {
            value = new();
            return false;
        }
        public bool TrySpans(IReadOnlyDictionary<string, string> sources, string file, out IReadOnlyList<SemanticSpan> value)
        {
            value = Array.Empty<SemanticSpan>();
            return false;
        }
        public void Dispose()
        {
        }
    }
    internal sealed class DelayedProvider : IScalaAnalysisProvider
    {
        public bool Ready;
        public string Kind = "class";
        public bool TryResolve(IReadOnlyDictionary<string, string> sources, string file, int offset, out AnalysisResolution value)
        {
            value = new();
            return false;
        }
        public bool TryComplete(IReadOnlyDictionary<string, string> sources, string file, int offset, out List<string> value)
        {
            value = new();
            return false;
        }
        public bool TrySpans(IReadOnlyDictionary<string, string> sources, string file, out IReadOnlyList<SemanticSpan> value)
        {
            value = ScalaLexer.Lex(sources[file]).Where(t => t.Text == "ForeignType").Select(t => new SemanticSpan { Start = t.Start, Length = t.Length, Kind = Kind }).ToArray();
            return Ready;
        }
    }
}
