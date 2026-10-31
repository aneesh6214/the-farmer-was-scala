// Test-only adapters for the plugin state. Native game types are referenced from
// the user's installed assemblies; this harness does not reconstruct game code.
namespace FarmerScala.Editor
{
    public static class EditorIntegration
    {
        public static bool Enabled
        {
            get; set;
        }
        public static bool SourceWriteBlocked
        {
            get; set;
        }
        public static bool BlocksNativeExecution => Enabled || SourceWriteBlocked;
        private static void AfterLoad(MainSim main)
        {
        }
    }
}

namespace FarmerScala
{
    public sealed class Plugin
    {
        public void LogInfo(string text)
        {
        }
        public static Plugin? Instance
        {
            get; set;
        }
        public TestHost? Host
        {
            get; set;
        }
        public void QueueOutput(string text)
        {
        }
    }
    public sealed class TestHost
    {
        public bool IsRunning
        {
            get; set;
        }
    }
}

// The game uses Mono's Harmony build. Its dynamic patch machinery is not needed
// for pure prefix decisions and is incompatible with this .NET 10 test process.
namespace HarmonyLib
{
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method, AllowMultiple = true)]
    public sealed class HarmonyPatch : System.Attribute
    {
        public HarmonyPatch()
        {
        }
        public HarmonyPatch(System.Type type, string method)
        {
        }
    }
    public static class AccessTools
    {
        private const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
        public static System.Reflection.MethodInfo Method(System.Type type, string name) => type.GetMethod(name, Flags) ?? throw new System.MissingMethodException(type.FullName, name);
        public static System.Reflection.FieldInfo Field(System.Type type, string name) => type.GetField(name, Flags) ?? throw new System.MissingFieldException(type.FullName, name);
    }
}
