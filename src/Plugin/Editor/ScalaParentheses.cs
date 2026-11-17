using System;
using System.Linq;
using System.Runtime.CompilerServices;

namespace FarmerScala.Editor;

internal static class ScalaParentheses
{
    private sealed class State { public string Source = ""; public int Caret = -2; public bool Focused; }
    private static readonly ConditionalWeakTable<CodeWindow, State> states = new ConditionalWeakTable<CodeWindow, State>();
    internal static void Tick()
    {
        var main = MainSim.Inst;
        if (main == null || !EditorIntegration.Enabled) return;
        foreach (var window in main.workspace.GetComponentsInChildren<CodeWindow>())
        {
            if (!EditorIntegration.IsScalaWindow(window)) continue;
            var input = window.CodeInput;
            bool focused = input.isFocused && input.selectionStringAnchorPosition == input.selectionStringFocusPosition;
            var state = states.GetOrCreateValue(window);
            if (state.Source == input.text && state.Caret == input.stringPosition && state.Focused == focused) continue;
            state.Source = input.text; state.Caret = input.stringPosition; state.Focused = focused;
            ScalaHighlighting.Refresh(window);
        }
    }
}
