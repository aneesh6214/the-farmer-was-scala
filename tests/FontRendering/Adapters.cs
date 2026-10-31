using System.Reflection;
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type type, string method) { }
    }
    public static class AccessTools
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        public static FieldInfo Field(Type type, string name) => type.GetField(name, Flags)!;
        public static MethodInfo Method(Type type, string name) => type.GetMethod(name, Flags)!;
    }
}
namespace UnityEngine
{
    public class Object
    {
        public string name = ""; public HideFlags hideFlags; public bool Destroyed;
        public static void Destroy(Object target)
        {
            if (target.Destroyed) throw new Exception("Double destruction");
            target.GetType().GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, null);
            target.Destroyed = true;
        }
        private static bool Missing(Object? target) => ReferenceEquals(target, null) || target.Destroyed;
        public static bool operator ==(Object? a, Object? b) => Missing(a) && Missing(b) || !Missing(a) && !Missing(b) && ReferenceEquals(a,b);
        public static bool operator !=(Object? a, Object? b) => !(a == b);
        public override bool Equals(object? obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
    public enum HideFlags { HideAndDontSave }
    public struct Color
    {
        public float r,g,b,a;
        public Color(float r,float g,float b,float a) { this.r=r;this.g=g;this.b=b;this.a=a; }
    }
    public readonly record struct Vector3(float x,float y,float z);
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public class MonoBehaviour : Component { }
    public sealed class GameObject : Object
    {
        private readonly List<Component> components = new();
        public T AddComponent<T>() where T : Component, new() { var item=new T { gameObject=this }; components.Add(item); return item; }
        public T? GetComponent<T>() where T : Component => components.OfType<T>().FirstOrDefault(x=>!x.Destroyed);
    }
    public sealed class Material : Object
    {
        public static int Copies;
        public readonly Dictionary<int,Color> Colors = new();
        public readonly Dictionary<string,object> Other = new();
        public Material() { }
        public Material(Material source) { Copies++; foreach(var item in source.Colors)Colors.Add(item.Key,item.Value);foreach(var item in source.Other)Other.Add(item.Key,item.Value); }
        public bool HasProperty(int id) => Colors.ContainsKey(id);
        public Color GetColor(int id) => Colors[id];
        public void SetColor(int id,Color value) => Colors[id]=value;
    }
    public static class Shader
    {
        private static readonly Dictionary<string,int> ids=new();
        public static int PropertyToID(string name) { if(!ids.ContainsKey(name))ids[name]=ids.Count+1; return ids[name]; }
    }
}
namespace TMPro
{
    public sealed class TMP_FontAsset : UnityEngine.Object { public UnityEngine.Material material = null!; }
    public sealed class TMP_Text : UnityEngine.Component
    {
        public UnityEngine.Material fontSharedMaterial = null!;
        public TMP_FontAsset font = null!;
    }
    public static class TMP_TextUtilities
    {
        public static int GetSimpleHashCode(string name) { int hash=0;foreach(char c in name)hash=unchecked(((hash<<5)+hash)^c);return hash; }
    }
    public static class MaterialReferenceManager
    {
        public static readonly Dictionary<int,UnityEngine.Material> Materials=new();
        public static readonly Dictionary<int,TMP_FontAsset> Fonts=new();
        public static void AddFontAsset(TMP_FontAsset font) => Fonts.TryAdd(TMP_TextUtilities.GetSimpleHashCode(font.name.ToUpperInvariant()),font);
        public static void AddFontMaterial(int hash,UnityEngine.Material material) => Materials.Add(hash,material);
        public static bool TryGetFontAsset(int hash,out TMP_FontAsset font) => Fonts.TryGetValue(hash,out font!);
        public static bool TryGetMaterial(int hash,out UnityEngine.Material material) => Materials.TryGetValue(hash,out material!);
    }
}
public sealed class CodeWindow
{
    private readonly TMPro.TMP_Text codeText;
    public bool Scala;
    public CodeWindow(TMPro.TMP_Text text,bool scala) { codeText=text;Scala=scala; }
    private bool UpdateTokens() => true;
    private void OnThemeChanged() { }
    public void UpdateSearch() { }
    public TooltipInfo? GetTooltipInfo(Action callback) => null;
}
public sealed class TooltipInfo
{
    public enum Anchor { Auto,TopLeft,BottomLeft,TopRight,BottomRight }
    public string text,docs;public float delay;public UnityEngine.Vector3 fixedPosition;public Anchor anchor;public object? itemBlock;
    public TooltipInfo(string text,float delay=0,UnityEngine.Vector3 fixedPosition=default,Anchor anchor=Anchor.Auto,string docs="") { this.text=text;this.delay=delay;this.fixedPosition=fixedPosition;this.anchor=anchor;this.docs=docs; }
}
namespace FarmerScala
{
    public sealed class Plugin { public static Plugin? Instance; public void LogInfo(string message) { } }
}
namespace FarmerScala.Editor
{
    internal static class EditorIntegration { internal static bool IsScalaWindow(CodeWindow window) => window.Scala; }
}
