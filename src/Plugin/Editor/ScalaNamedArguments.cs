using System.Collections.Generic;

namespace FarmerScala.Editor;

/// <summary>Immediate argument-label coloring, including incomplete calls. Does not resolve or execute code.</summary>
internal static class ScalaNamedArguments
{
    private sealed class Frame
    {
        internal string Open = "";
        internal int Index;
        internal bool Call, Control;
    }

    internal static HashSet<int> Starts(IReadOnlyList<ScalaToken> input)
    {
        var tokens = new List<ScalaToken>();
        foreach (var token in input)
            if (token.Kind != ScalaTokenKind.Newline && token.Kind != ScalaTokenKind.Comment)
                tokens.Add(token);
        var result = new HashSet<int>();
        var stack = new Stack<Frame>();
        var closed = new Dictionary<int, Frame>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            string text = token.Text;
            if (text == "(" || text == "[" || text == "{")
            {
                int callee = i - 1;
                if (callee >= 0 && tokens[callee].Text == "]" && closed.TryGetValue(callee, out var types))
                    callee = types.Index - 1;
                bool control = callee >= 0 && (tokens[callee].Text == "if" || tokens[callee].Text == "while" ||
                    tokens[callee].Text == "for" || tokens[callee].Text == "catch");
                bool call = callee >= 0 && (tokens[callee].Kind == ScalaTokenKind.Identifier ||
                    tokens[callee].Text == ")" || tokens[callee].Text == "this" || tokens[callee].Text == "super");
                if (callee > 0 && (tokens[callee - 1].Text == "def" || tokens[callee - 1].Text == "class" ||
                    tokens[callee - 1].Text == "trait" || tokens[callee - 1].Text == "enum"))
                    call = false;
                if (callee >= 0 && closed.TryGetValue(callee, out var previous) && previous.Control)
                    call = false;
                stack.Push(new Frame { Open = text, Index = i, Call = text == "(" && call, Control = control });
                continue;
            }
            if (text == ")" || text == "]" || text == "}")
            {
                string open = text == ")" ? "(" : text == "]" ? "[" : "{";
                if (stack.Count > 0 && stack.Peek().Open == open)
                    closed[i] = stack.Pop();
                continue;
            }
            if (token.Kind != ScalaTokenKind.Identifier || stack.Count == 0 || !stack.Peek().Call ||
                i + 1 >= tokens.Count || tokens[i + 1].Text != "=")
                continue;
            string before = i > 0 ? tokens[i - 1].Text : "";
            if (before == "(" || before == "," || before == "using")
                result.Add(token.Start);
        }
        return result;
    }
}
