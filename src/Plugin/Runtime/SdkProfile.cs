using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FarmerScala.Runtime;

/// <summary>A launch-selected SDK. No DLC declarations enter a base-only compiler.</summary>
internal static class SdkProfile
{
    internal static string ActiveDirectory { get; private set; } = "";
    internal static bool MiningEnabled { get; private set; }
    internal static IReadOnlyDictionary<string, string[]>? AvailableConstants { get; private set; }
    internal static readonly string[] MiningActions = { "dig", "place", "get_pos_z", "get_hardness", "get_stability",
        "prospect_iron", "prospect_quartz", "push", "can_push", "jump", "hover", "get_hover" };
    internal static string Resolve(string root) => ActiveDirectory.Length == 0 ? Path.Combine(root, "sdk") : ActiveDirectory;

    internal static Dictionary<string, string[]> SelectCatalog(IEnumerable<(string Family, string Name, bool Mining)> values, bool mining)
        => values.Where(value => mining || !value.Mining).GroupBy(value => value.Family)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Name).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

    internal static string Build(string bundledSdk, string cache, bool mining, IReadOnlyDictionary<string, string[]> catalog)
    {
        var files = Directory.GetFiles(Path.Combine(bundledSdk, "game"), "*.scala", SearchOption.AllDirectories)
            .ToDictionary(f => Path.Combine("game", Path.GetRelativePath(Path.Combine(bundledSdk, "game"), f)), File.ReadAllText);
        string enums = files[Path.Combine("game", "GameEnums.scala")];
        foreach (var family in catalog)
        {
            string head = "enum " + family.Key + " extends GameValue:";
            int start = enums.IndexOf(head, StringComparison.Ordinal);
            if (start < 0) throw new InvalidDataException("Unknown SDK enum " + family.Key);
            start += head.Length;
            int end = enums.IndexOf("  def family:", start, StringComparison.Ordinal);
            if (end < 0) throw new InvalidDataException("SDK enum has no bridge family.");
            string cases = string.Join("\n", family.Value.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => "  case `" + n.Replace("`", "") + "`"));
            enums = enums.Substring(0, start) + "\n" + cases + "\n" + enums.Substring(end);
        }
        files[Path.Combine("game", "GameEnums.scala")] = enums;
        if (mining)
        {
            files[Path.Combine("game", "MiningApi.scala")] = File.ReadAllText(Path.Combine(bundledSdk, "mining", "MiningApi.scala.template"));
            string api = files[Path.Combine("game", "api.scala")];
            api = api.Replace("object api:", "object api:\n  export MiningApi.*")
                .Replace("Option[Double | (Int, Int)]", "Option[Double | (Int, Int) | String]")
                .Replace("else Some(summon[WireCodec[(Int, Int)]].decode(raw))", "else if js.typeOf(raw) == \"string\" then Some(raw.asInstanceOf[String])\n    else Some(summon[WireCodec[(Int, Int)]].decode(raw))");
            files[Path.Combine("game", "api.scala")] = api;
        }
        string content = string.Join("\0", files.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "\0" + p.Value));
        string digest;
        using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-", "");
        string directory = Path.Combine(cache, mining ? "mining" : "base", digest);
        foreach (var file in files)
        {
            string target = Path.Combine(directory, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.WriteAllText(target, file.Value, new UTF8Encoding(false));
        }
        return directory;
    }
    internal static void Activate(string directory, bool mining, IReadOnlyDictionary<string, string[]>? catalog = null)
    { ActiveDirectory = directory; MiningEnabled = mining; AvailableConstants = catalog; }
}
