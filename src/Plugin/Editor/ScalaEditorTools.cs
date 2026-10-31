using System.Linq;
using UnityEngine;

namespace FarmerScala.Editor;

/// <summary>Explicit formatting is an asynchronous editor action, never part of Play.</summary>
internal static class ScalaEditorTools
{
    private static CodeWindow? pending;
    private static string source = "";
    private static long revision;
    private static double expires;
    internal static void RequestFormat(CodeWindow window)
    {
        if (window == null || window.CodeInput.readOnly || !EditorIntegration.IsScalaWindow(window) || !ScalaAnalysis.CaptureRevision(window))
            return;
        if (pending == window && source == window.CodeInput.text && revision == ScalaAnalysis.SourceVersion)
            return;
        pending = window;
        source = window.CodeInput.text;
        revision = ScalaAnalysis.SourceVersion;
        expires = Time.unscaledTime + 30;
        Plugin.Instance?.LogInfo("Formatting Scala…");
    }
    internal static void Tick()
    {
        if (Application.isFocused && Input.GetKeyDown(KeyCode.L) &&
            (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
            (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
        {
            var window = MainSim.Inst?.workspace?.codeWindows.Values.FirstOrDefault(w => w.CodeInput.isFocused && !w.CodeInput.readOnly && EditorIntegration.IsScalaWindow(w));
            if (window != null)
                RequestFormat(window);
        }
        if (pending == null)
            return;
        var target = pending;
        if (!EditorIntegration.IsScalaWindow(target) || target.CodeInput.text != source || ScalaAnalysis.SourceVersion != revision)
        {
            pending = null;
            return;
        }
        if (Time.unscaledTime > expires)
        {
            pending = null;
            Plugin.Instance?.LogInfo("Scala formatting timed out. Try again once analysis is ready.");
            return;
        }
        if (!ScalaAnalysis.TryFormat(target, out var result))
            return;
        pending = null;
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            Plugin.Instance?.LogInfo("Scala formatting: " + result.Error);
            return;
        }
        if (result.Source == null)
        {
            Plugin.Instance?.LogInfo("Scala formatter returned no source. Please retry.");
            return;
        }
        string formatted = ScalaWhitespace.ExpandTabs(result.Source, 0, 0).Source;
        if (formatted == source)
        {
            Plugin.Instance?.LogInfo("Scala formatting complete; file is already formatted.");
            return;
        }
        ScalaEditing.ApplyAtomic(target, formatted, DiagnosticRanges.MapCaret(source, formatted, target.CodeInput.stringPosition));
        Plugin.Instance?.LogInfo("Scala formatting complete.");
    }
    internal static void Clear()
    {
        pending = null;
    }
}
