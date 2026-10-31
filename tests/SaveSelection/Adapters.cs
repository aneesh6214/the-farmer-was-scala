using System.Reflection;
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string method) { }
    }
    public static class AccessTools
    {
        public static FieldInfo Field(Type type, string name) => type.GetField(name,BindingFlags.NonPublic | BindingFlags.Static)!;
    }
}
public static class Helper { public static string persistentDataPath = ""; }
public static class OptionHolder
{
    private static Dictionary<string,object> options = new();
    public static Dictionary<string,object> Values => options;
    public static void Reset() => options = new();
}
namespace FarmerScala
{
    public class Plugin
    {
        public static Plugin? Instance;
        public void LogError(string message) => throw new Exception(message);
    }
}
