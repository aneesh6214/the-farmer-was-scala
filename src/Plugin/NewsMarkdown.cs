using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FarmerScala;

/// <summary>Small Markdown additions for mod news; native rendering still owns headings and links.</summary>
internal static class NewsMarkdown
{
    internal static string Prepare(string text)
    {
        // Protect code, link destinations, existing TMP tags and escaped punctuation
        // before recognizing emphasis. Link labels may still contain emphasis.
        var protectedText = new List<string>();
        text = Regex.Replace(text, @"(?s)(`+).*?\1|\]\((?:\\.|[^)\\])*\)|<[^>]+>|\\[\\`*_{}\[\]()#+\-.!]", match =>
        {
            string value = match.Value;
            if (value[0] == '\\')
                value = value.Substring(1);
            string key = "\u0001" + protectedText.Count + "\u0002";
            protectedText.Add(value);
            return key;
        });
        text = Regex.Replace(text, @"(?m)^[ \t]*---[ \t]*$", "––––––––––––––––––––––––––––––––––––––––");
        text = Regex.Replace(text, @"(?<!\*)\*\*\*(?=\S)(.+?)(?<=\S)\*\*\*(?!\*)", "<b><i>$1</i></b>");
        text = Regex.Replace(text, @"(?<!\*)\*\*(?=\S)(.+?)(?<=\S)\*\*(?!\*)", "<b>$1</b>");
        text = Regex.Replace(text, @"(?<![\w_])__(?=\S)(.+?)(?<=\S)__(?![\w_])", "<b>$1</b>");
        text = Regex.Replace(text, @"(?<!\*)\*(?=\S)(.+?)(?<=\S)\*(?!\*)", "<i>$1</i>");
        text = Regex.Replace(text, @"(?<![\w_])_(?=\S)(.+?)(?<=\S)_(?![\w_])", "<i>$1</i>");
        return Regex.Replace(text, "\u0001([0-9]+)\u0002", match => protectedText[int.Parse(match.Groups[1].Value)]);
    }
}
