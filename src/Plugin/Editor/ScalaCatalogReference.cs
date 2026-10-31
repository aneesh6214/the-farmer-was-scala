
namespace FarmerScala.Editor;

/// <summary>Recover the exact catalog expression rather than native cached hover word bounds.</summary>
internal static class ScalaCatalogReference
{
    internal static bool IsCatalog(string name) => name is "Items" or "Entities" or "Grounds" or "Hats" or "Unlocks";
    internal static bool TryFind(string source, int start, int end, out string name, out int anchor)
    {
        name = "";
        anchor = start;
        if (start < 0 || end <= start || end > source.Length)
            return false;
        string word = source.Substring(start, end - start).Trim('`');
        if (IsCatalog(word))
        {
            name = word;
            return true;
        }
        var tokens = ScalaLexer.Lex(source);
        int index = tokens.FindIndex(t => t.Start == start && t.End == end && t.Identifier);
        if (index < 2 || tokens[index - 1].Text != "." || !IsCatalog(tokens[index - 2].Text))
            return false;
        anchor = tokens[index - 2].Start;
        name = tokens[index - 2].Text + "." + word;
        return true;
    }
}
