using Mono.Cecil;

// Inspect metadata only. HarmonyX is a Mono/Unity dependency and cannot be
// executed faithfully by the .NET test host. The manual checklist covers loading.
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string game = args.Length > 0 ? args[0] : @"C:\Program Files (x86)\Steam\steamapps\common\The Farmer Was Replaced";
string managed = Path.Combine(game, "TheFarmerWasReplaced_Data", "Managed");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(ModuleDefinition).Assembly.Location)!);
resolver.AddSearchDirectory(Path.Combine(root, ".local/bepinex/5.4.23.6/BepInEx/core"));
var options = new ReaderParameters { AssemblyResolver = resolver };
using var plugin = ModuleDefinition.ReadModule(Path.Combine(root, "src/Plugin/bin/Release/netstandard2.1/FarmerScala.dll"), options);
using var core = ModuleDefinition.ReadModule(Path.Combine(managed, "Core.dll"), options);
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
IEnumerable<TypeDefinition> All(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(All));
foreach (var type in plugin.Types.SelectMany(All))
{
    var attributes = type.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch").ToList();
    var typeArgument = attributes.SelectMany(a => a.ConstructorArguments).FirstOrDefault(a => a.Value is TypeReference).Value as TypeReference;
    var methodName = attributes.SelectMany(a => a.ConstructorArguments).FirstOrDefault(a => a.Value is string).Value as string;
    if (typeArgument == null || methodName == null) continue;
    var targetType = typeArgument.Resolve();
    var targets = targetType.Methods.Where(m => m.Name == methodName).ToList();
    Check(targets.Count != 0, "Missing patch target: " + typeArgument.FullName + "." + methodName);
    if (targets.Count != 1) continue;
    var target = targets[0];
    foreach (var patch in type.Methods.Where(m => m.Name is "Prefix" or "Postfix" or "Finalizer"))
    foreach (var parameter in patch.Parameters)
    {
        string name = parameter.Name;
        if (name.StartsWith("__"))
        {
            if (int.TryParse(name.Substring(2), out int index))
                Check(index >= 0 && index < target.Parameters.Count, "Missing indexed argument: " + type.FullName + "." + name);
            if (name == "__result") Check(target.ReturnType.FullName != "System.Void", "Void patch target has a result: " + type.FullName);
            continue;
        }
        Check(target.Parameters.Any(p => p.Name == name), "Missing native argument: " + typeArgument.FullName + "." + methodName + "." + name);
    }
}
TypeDefinition Native(string name) => core.Types.Single(t => t.Name == name);
Check(!Native("Logger").Methods.Single(m => m.Name == "Log").IsStatic, "Output must use the simulation-owned logger.");
Check(Native("Saver").Methods.Single(m => m.Name == "SaveAsync").ReturnType.FullName.StartsWith("System.Threading.Tasks.Task`1"), "Saving must use the asynchronous pipeline.");
Check(Native("ErrorMessage").Methods.Single(m => m.Name == "ShowError").Parameters.Count == 4, "Error popup must support the visible clipping area.");
Check(Native("CodeWindow").Methods.Single(m => m.Name == "Load").Parameters.Count == 2, "Library loading must pass the native read-only flag.");
Console.WriteLine($"Native patch metadata checks passed: {checks}. Unity loading and visual behavior require manual verification.");
