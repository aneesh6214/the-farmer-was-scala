using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// This DLL deliberately references no game/Unity types. It runs before their
// initializers, so options, save paths and Logger's captured path are isolated.
public static class Patcher
{
    private static string root = "", data = "";
    private static bool redirected;
    private static int protectedWrites;
    public static IEnumerable<string> TargetDLLs => new[] { "Utils.dll", "Core.dll", "com.rlabrecque.steamworks.net.dll" };
    public static void ConfigureForPreparation(string directory)
    {
        root = Path.GetFullPath(directory); data = Path.Combine(root, "data");
        if (!File.Exists(Path.Combine(root, "test-run.marker")) || !Directory.Exists(data)) throw new Exception("Missing isolated profile marker.");
    }
    public static void Initialize()
    {
        try
        {
            root = Path.GetFullPath(Environment.GetEnvironmentVariable("FARMER_SCALA_TEST_RUN") ?? throw new Exception("Missing test run directory."));
            data = Path.Combine(root, "data");
            string game = Path.GetFullPath(Environment.GetEnvironmentVariable("FARMER_SCALA_TEST_GAME") ?? "");
            string executable = Process.GetCurrentProcess().MainModule?.FileName ?? throw new Exception("Missing executable path.");
            if (!Environment.GetCommandLineArgs().Contains("-batchmode") || !Environment.GetCommandLineArgs().Contains("-nographics")) throw new Exception("Integration requires headless launch flags.");
            string actual = Path.GetDirectoryName(executable) ?? throw new Exception("Missing executable directory.");
            if (!File.Exists(Path.Combine(root, "test-run.marker")) || !Directory.Exists(data) ||
                !string.Equals(actual, game, StringComparison.OrdinalIgnoreCase)) throw new Exception("Not the isolated game copy.");
        }
        catch (Exception error) { Abort(error); }
    }
    public static void Patch(AssemblyDefinition assembly)
    {
        try
        {
            if (assembly.Name.Name == "Utils")
            {
                var getter = assembly.MainModule.Types.Single(t => t.Name == "Helper").Methods.Single(m => m.Name == "get_persistentDataPath");
                getter.Body = new MethodBody(getter);
                var il = getter.Body.GetILProcessor(); il.Emit(OpCodes.Ldstr, data); il.Emit(OpCodes.Ret);
                redirected = true;
            }
            else if (assembly.Name.Name == "Core")
            {
                foreach (var leaf in new[] { ("FarmRenderer", "RenderInstanced"), ("VFXManager", "DrawMovingMeshEffects"), ("VFXManager", "DrawCrumbleEffects"), ("VFXManager", "DrawCrumbleDecorationEffects") })
                {
                    var draw = assembly.MainModule.Types.Single(t => t.Name == leaf.Item1).Methods.Single(m => m.Name == leaf.Item2);
                    if (draw.ReturnType.MetadataType != MetadataType.Void) throw new Exception("Unexpected render leaf signature.");
                    draw.Body = new MethodBody(draw);
                    draw.Body.GetILProcessor().Emit(OpCodes.Ret);
                }
                File.WriteAllText(Path.Combine(root, "headless-rendering.txt"), "FarmRenderer.RenderInstanced and VFXManager.DrawMovingMeshEffects/DrawCrumbleEffects/DrawCrumbleDecorationEffects: GPU drawing suppressed; native Update and simulation unchanged.");
            }
            else
            {
                var type = assembly.MainModule.GetType("Steamworks.SteamUserStats");
                // Prevent test startup relaunching the ordinary Steam game copy.
                // Init and entitlement queries still use the real Steam client.
                var restart = assembly.MainModule.GetType("Steamworks.SteamAPI").Methods.Single(m => m.Name == "RestartAppIfNecessary");
                restart.Body = new MethodBody(restart);
                restart.Body.GetILProcessor().Emit(OpCodes.Ldc_I4_0);
                restart.Body.GetILProcessor().Emit(OpCodes.Ret);
                var names = new HashSet<string> { "SetAchievement", "ClearAchievement", "SetStat", "UpdateAvgRateStat", "ResetAllStats", "IndicateAchievementProgress", "StoreStats", "UploadLeaderboardScore" };
                foreach (var method in type.Methods.Where(m => m.IsStatic && names.Contains(m.Name)))
                {
                    method.Body = new MethodBody(method) { InitLocals = true };
                    var il = method.Body.GetILProcessor();
                    il.Emit(OpCodes.Ldstr, Path.Combine(root, "external-write-attempts.log"));
                    il.Emit(OpCodes.Ldstr, method.FullName + "\n");
                    var append = new MethodReference("AppendAllText", assembly.MainModule.TypeSystem.Void,
                        new TypeReference("System.IO", "File", assembly.MainModule, assembly.MainModule.TypeSystem.CoreLibrary)) { HasThis = false };
                    append.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.String));
                    append.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.String));
                    il.Emit(OpCodes.Call, append);
                    if (method.ReturnType.MetadataType == MetadataType.Boolean) il.Emit(OpCodes.Ldc_I4_0);
                    else
                    {
                        var value = new VariableDefinition(method.ReturnType); method.Body.Variables.Add(value);
                        il.Emit(OpCodes.Ldloca, value); il.Emit(OpCodes.Initobj, method.ReturnType); il.Emit(OpCodes.Ldloc, value);
                    }
                    il.Emit(OpCodes.Ret); protectedWrites++;
                }
            }
        }
        catch (Exception error) { Abort(error); }
    }
    public static void Finish()
    {
        if (!redirected || protectedWrites < 9) Abort(new Exception("Required save/Steam isolation patches were not applied."));
        File.WriteAllText(Path.Combine(root, "isolation-ready.txt"), data + "\nProtected write methods: " + protectedWrites);
    }
    private static void Abort(Exception error)
    {
        try { if (root.Length > 0) File.WriteAllText(Path.Combine(root, "isolation-failed.txt"), error.ToString()); } catch { }
        Environment.Exit(86);
    }
}
