using System.Collections.Generic;

namespace FarmerScala.Editor;

internal sealed class AnalysisDefinition
{
    public string File = "";
    public int Offset;
    public bool IsLibrary;
    public string? Source;
    public string? LibraryId;
}
internal sealed class AnalysisDiagnostic
{
    public string File = "", Message = "", Severity = "error";
    public int Start, Length;
}
internal sealed class AnalysisFormatResult
{
    public string? Source, Error;
}

/// <summary>Optional editor operations. Callers debounce diagnostics and explicitly request formatting.</summary>
internal interface IScalaEditorToolsProvider
{
    bool TryDiagnostics(IReadOnlyDictionary<string, string> sources, out IReadOnlyList<AnalysisDiagnostic> result);
    bool TryFormat(IReadOnlyDictionary<string, string> sources, string file, out AnalysisFormatResult result);
}
internal sealed class AnalysisResolution
{
    public AnalysisDefinition? Definition;
    public string? HoverText;
    public string? SymbolKind;
    public bool IsGameBuiltin;
    public string? GameFunctionName;
}

internal static class AnalysisResolutionMerger
{
    internal static AnalysisResolution Merge(AnalysisResolution local, AnalysisResolution compiler)
    {
        if (compiler.IsGameBuiltin)
            return compiler;
        if (string.IsNullOrEmpty(compiler.SymbolKind))
            compiler.SymbolKind = local.SymbolKind;
        if (compiler.Definition == null && local.Definition != null)
            compiler.Definition = local.Definition;
        string Kind(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            foreach (var token in ScalaLexer.Lex(text!))
                if (token.Text == "def")
                    return "function";
                else if (token.Text == "class" || token.Text == "trait" || token.Text == "object" || token.Text == "enum" || token.Text == "type")
                    return "class";
                else if (token.Text == "val" || token.Text == "var")
                    return "value";
            return "";
        }
        // Scala PC can expose a synthetic result type at an unapplied method.
        // Retain an obvious declared signature instead of showing `class Int`.
        if (local.Definition != null && Kind(local.HoverText) is string localKind && localKind.Length > 0 &&
            (string.IsNullOrWhiteSpace(compiler.HoverText) || Kind(compiler.HoverText) != localKind))
            compiler.HoverText = local.HoverText;
        return compiler;
    }
}

/// <summary>Cached exact-source compiler answers may supersede the tolerant local index.
/// Implementations must return immediately and never block Unity on compiler/LSP I/O.</summary>
internal interface IScalaAnalysisProvider
{
    bool TryResolve(IReadOnlyDictionary<string, string> sources, string file, int offset, out AnalysisResolution result);
    bool TryComplete(IReadOnlyDictionary<string, string> sources, string file, int offset, out List<string> result);
    bool TrySpans(IReadOnlyDictionary<string, string> sources, string file, out IReadOnlyList<SemanticSpan> result);
}
