using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FarmerScala.Worker;

public sealed record EditorLocation(string File, int Line, int Column, int EndLine, int EndColumn);

/// <summary>Standard v3 source-map VLQ decoder plus the compiler's raw-editor wrapper offsets.</summary>
public sealed class SourceMapping
{
    private sealed record Segment(int GeneratedColumn, int Source, int Line, int Column);
    private readonly List<List<Segment>> lines = new();
    private readonly Dictionary<string, (string File, int LineOffset, int ColumnOffset)> editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> text = new(StringComparer.OrdinalIgnoreCase);
    private string[] sources = [];

    public SourceMapping(string artifact)
    {
        string metadataPath = artifact + ".editor-map.json";
        if (File.Exists(metadataPath) && JsonNode.Parse(File.ReadAllText(metadataPath)) is JsonObject metadata)
            foreach (var node in metadata["sources"] as JsonArray ?? new())
            {
                if (node is not JsonObject item)
                    throw new IOException("Invalid editor source map entry.");
                string generated = (string?)item["generated"] ?? "";
                string file = (string?)item["file"] ?? BaseName(generated);
                editors[BaseName(generated)] = (file, (int?)item["lineOffset"] ?? 0, (int?)item["columnOffset"] ?? 0);
            }
        string sourcePath = Path.Combine(Path.GetDirectoryName(artifact)!, "sources.json");
        if (File.Exists(sourcePath) && JsonNode.Parse(File.ReadAllText(sourcePath)) is JsonObject raw)
        {
            int offset = (int?)raw["prefixLines"] ?? 0;
            foreach (var item in raw["sources"] as JsonObject ?? new())
            {
                text[item.Key] = ((string?)item.Value ?? "").Replace("\r\n", "\n").Split('\n');
                if (!editors.ContainsKey(BaseName(item.Key)))
                    editors[BaseName(item.Key)] = (item.Key, offset, 0);
            }
        }
        string mapPath = artifact + ".map";
        if (!File.Exists(mapPath))
            return;
        var map = JsonNode.Parse(File.ReadAllText(mapPath)) as JsonObject ?? throw new IOException("Invalid Scala.js source map.");
        if ((int?)map["version"] != 3)
            throw new IOException("Unsupported Scala.js source map version.");
        sources = (map["sources"] as JsonArray ?? new()).Select(n => (string?)n ?? "").ToArray();
        string mappings = (string?)map["mappings"] ?? "";
        int source = 0, originalLine = 0, originalColumn = 0, name = 0;
        foreach (string line in mappings.Split(';'))
        {
            var decoded = new List<Segment>();
            int generatedColumn = 0;
            foreach (string segment in line.Split(','))
            {
                if (segment.Length == 0)
                    continue;
                int index = 0;
                generatedColumn += Vlq(segment, ref index);
                if (index == segment.Length)
                    continue;
                source += Vlq(segment, ref index);
                originalLine += Vlq(segment, ref index);
                originalColumn += Vlq(segment, ref index);
                if (index < segment.Length)
                    name += Vlq(segment, ref index);
                if (source < 0 || source >= sources.Length || originalLine < 0 || originalColumn < 0)
                    throw new IOException("Out-of-range Scala.js source map segment.");
                decoded.Add(new(generatedColumn, source, originalLine, originalColumn));
            }
            lines.Add(decoded);
        }
    }

    public EditorLocation? Map(int javascriptLine, int javascriptColumn)
    {
        if (javascriptLine < 1 || javascriptLine > lines.Count)
            return null;
        var segments = lines[javascriptLine - 1];
        Segment? selected = null;
        foreach (var segment in segments)
        {
            if (segment.GeneratedColumn > javascriptColumn)
                break;
            selected = segment;
        }
        if (selected is null || !editors.TryGetValue(BaseName(sources[selected.Source]), out var editor))
            return null;
        int line = selected.Line + 1 - editor.LineOffset;
        int column = selected.Column + 1 - editor.ColumnOffset;
        if (line < 1 || column < 1)
            return null;
        if (text.TryGetValue(editor.File, out var content))
        {
            if (line > content.Length)
                return null;
            string source = content[line - 1];
            int start = Math.Min(column - 1, source.Length);
            int end = start;
            while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] == '_' || source[end] == '$'))
                end++;
            return new(editor.File, line, column, line, Math.Max(column + 1, end + 1));
        }
        return new(editor.File, line, column, line, column + 1);
    }

    public EditorLocation? MapStack(string? stack)
    {
        if (stack is null)
            return null;
        foreach (Match match in Regex.Matches(stack, @"main\.js:(\d+):(\d+)"))
            if (Map(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value) - 1) is { } location)
                return location;
        return null;
    }

    private static string BaseName(string path) => Path.GetFileName(Uri.UnescapeDataString(path).Replace('/', Path.DirectorySeparatorChar));
    private static int Vlq(string input, ref int index)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        int value = 0, shift = 0, digit;
        do
        {
            if (index >= input.Length || shift > 30)
                throw new IOException("Invalid source-map VLQ.");
            digit = alphabet.IndexOf(input[index++]);
            if (digit < 0)
                throw new IOException("Invalid source-map base64.");
            value |= (digit & 31) << shift;
            shift += 5;
        } while ((digit & 32) != 0);
        return (value & 1) == 0 ? value >> 1 : -(value >> 1);
    }
}
