using Mono.Cecil;
using System.Text.Json;
using System.Security.Cryptography;

if (args.Length < 2) throw new ArgumentException("Expected isolated game and run directories.");
string game = Path.GetFullPath(args[0]), run = Path.GetFullPath(args[1]);
string sandbox = Directory.GetParent(Directory.GetParent(run)!.FullName)!.FullName + Path.DirectorySeparatorChar;
if (!game.StartsWith(sandbox, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(game).StartsWith("game-", StringComparison.Ordinal))
    throw new IOException("Refusing to patch a game outside the integration sandbox.");
Patcher.ConfigureForPreparation(run);
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.Combine(game, "TheFarmerWasReplaced_Data", "Managed"));
var changes = new List<object>();
foreach (string file in new[] { "Utils.dll", "com.rlabrecque.steamworks.net.dll" })
{
    string path = Path.Combine(game, "TheFarmerWasReplaced_Data", "Managed", file);
    string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    using (var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true, AssemblyResolver = resolver }))
    {
        Patcher.Patch(assembly);
        string temp = path + ".integration-new";
        assembly.Write(temp);
        File.Move(temp, path, true);
    }
    changes.Add(new { file, before, after = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
}
// Diagnostic probes change only the cloned plug-in's exact version guard.
// Normal runs retain the shipping guard and reject mismatched payloads.
string plugin = Path.Combine(game, "BepInEx/plugins/FarmerScala/FarmerScala.dll");
resolver.AddSearchDirectory(Path.Combine(game, "BepInEx/core"));
resolver.AddSearchDirectory(Path.GetDirectoryName(plugin)!);
string coreHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(game, "TheFarmerWasReplaced_Data/Managed/Core.dll"))));
using (var assembly = AssemblyDefinition.ReadAssembly(plugin, new ReaderParameters { InMemory = true, AssemblyResolver = resolver }))
{
    var awake = assembly.MainModule.GetType("FarmerScala.Plugin").Methods.Single(m => m.Name == "Awake");
    var guards = awake.Body.Instructions.Where(i => i.Operand is string text && System.Text.RegularExpressions.Regex.IsMatch(text, "^[A-F0-9]{64}$")).ToList();
    if (guards.Count != 1) throw new IOException("Cannot identify the plug-in's exact supported-game guard.");
    string expected = (string)guards[0].Operand;
    if (expected != coreHash)
    {
        if (!args.Contains("--allow-unverified")) throw new IOException("The selected payload does not support this game build.");
        guards[0].Operand = coreHash;
        string temp = plugin + ".integration-new"; assembly.Write(temp); File.Move(temp, plugin, true);
        changes.Add(new { file = "FarmerScala.dll", purpose = "Explicit diagnostic guard override in test copy only", expected, actual = coreHash });
    }
}
Patcher.Finish();
File.WriteAllText(Path.Combine(run, "isolation-manifest.json"), JsonSerializer.Serialize(new { data = Path.Combine(run, "data"), changes, purpose = "Data paths and external writes only; gameplay methods unchanged." }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Prepared save/Steam isolation on the test-copy files before launch.");
