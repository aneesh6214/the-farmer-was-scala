using System.Collections.Generic;

namespace FarmerScala.Editor;

internal static class ScalaParenthesisMatch
{
    internal static (int First, int Second) Find(string source, int caret)
    {
        var stack = new Stack<int>();
        var pairs = new Dictionary<int, int>();
        foreach (var token in ScalaLexer.Lex(source))
        {
            if (token.Kind != ScalaTokenKind.Punctuation) continue;
            if (token.Text == "(") stack.Push(token.Start);
            else if (token.Text == ")" && stack.Count != 0)
            {
                int open = stack.Pop();
                pairs[open] = token.Start;
                pairs[token.Start] = open;
            }
        }
        if (pairs.TryGetValue(caret, out int next)) return (caret, next);
        if (pairs.TryGetValue(caret - 1, out int previous)) return (caret - 1, previous);
        return (-1, -1);
    }
}
