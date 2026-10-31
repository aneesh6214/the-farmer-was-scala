using System;
using System.Linq;
using System.Text;

namespace FarmerScala.Editor;

/// <summary>Expand indentation/code tabs without changing literal contents.</summary>
internal static class ScalaWhitespace
{
    internal static ScalaEditingResult ExpandTabs(string source, int anchor, int focus)
    {
        var result = new ScalaEditingResult { Source = source, Anchor = anchor, Focus = focus };
        if (source.IndexOf('\t') < 0)
            return result;
        var literals = ScalaLexer.Lex(source).Where(token => token.Kind == ScalaTokenKind.String ||
            token.Kind == ScalaTokenKind.Identifier && token.Text.StartsWith("`", StringComparison.Ordinal)).ToArray();
        var output = new StringBuilder(source.Length);
        int tokenIndex = 0, beforeAnchor = 0, beforeFocus = 0;
        for (int at = 0; at < source.Length; at++)
        {
            while (tokenIndex < literals.Length && literals[tokenIndex].End <= at)
                tokenIndex++;
            bool literal = tokenIndex < literals.Length && literals[tokenIndex].Start <= at;
            if (source[at] == '\t' && !literal)
            {
                output.Append("    ");
                if (at < anchor)
                    beforeAnchor += 3;
                if (at < focus)
                    beforeFocus += 3;
            }
            else
                output.Append(source[at]);
        }
        result.Source = output.ToString();
        result.Anchor = anchor + beforeAnchor;
        result.Focus = focus + beforeFocus;
        return result;
    }
}
