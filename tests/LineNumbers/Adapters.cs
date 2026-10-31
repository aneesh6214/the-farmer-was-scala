// Small Unity/TMP surface for exercising the production gutter's lifecycle and
// viewport behavior. The production build validates bindings to real game APIs.
using System.Reflection;
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string method) { }
    }
    public static class AccessTools
    {
        public static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public static MethodInfo Method(Type type, string name) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    }
}
namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed;
        public static void Destroy(Object value) { value.Destroyed = true; }
    }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public RectTransform transform => gameObject.Rect;
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public class MonoBehaviour : Component { }
    public class CanvasRenderer : Component { }
    public class GameObject : Object
    {
        private readonly List<Component> components = new();
        public bool Active = true;
        public bool activeInHierarchy => Active;
        public RectTransform Rect = null!;
        public GameObject(string name = "", params Type[] types)
        {
            AddComponent<RectTransform>();
            foreach (var type in types) if (type != typeof(RectTransform)) Add(type);
        }
        private Component Add(Type type)
        {
            var value = (Component)Activator.CreateInstance(type)!;
            value.gameObject = this;
            components.Add(value);
            if (value is RectTransform rect) Rect = rect;
            return value;
        }
        public T AddComponent<T>() where T : Component, new() => (T)Add(typeof(T));
        public T? GetComponent<T>() where T : Component => components.OfType<T>().FirstOrDefault(x => !x.Destroyed);
        public void SetActive(bool active) => Active = active;
    }
    public readonly record struct Vector2(float x, float y)
    {
        public static Vector2 zero => default;
        public static Vector2 one => new(1,1);
    }
    public readonly record struct Vector3(float x, float y, float z);
    public readonly record struct Vector4(float x, float y, float z, float w)
    {
        public static Vector4 zero => default;
    }
    public readonly record struct Rect(float width, float height)
    {
        public float yMax => 0;
        public float yMin => -height;
        public Vector2 size => new(width,height);
    }
    public class RectTransform : Component
    {
        public RectTransform? Parent;
        public readonly List<RectTransform> Children = new();
        public Vector2 anchorMin, anchorMax, pivot, offsetMin, offsetMax, sizeDelta = new(30,120), anchoredPosition;
        public Rect rect => anchorMin == Vector2.zero && anchorMax == Vector2.one && Parent != null ? Parent.rect : new(sizeDelta.x,sizeDelta.y);
        public void SetParent(RectTransform? value, bool worldPositionStays) { Parent = value; value?.Children.Add(this); }
        public void SetAsFirstSibling() { Parent?.Children.Remove(this); Parent?.Children.Insert(0,this); }
        public Vector3 TransformPoint(Vector3 value)
        {
            value = new(value.x + anchoredPosition.x, value.y + anchoredPosition.y, value.z);
            return Parent?.TransformPoint(value) ?? value;
        }
        public Vector3 InverseTransformPoint(Vector3 value)
        {
            if (Parent != null) value = Parent.InverseTransformPoint(value);
            return new(value.x - anchoredPosition.x, value.y - anchoredPosition.y, value.z);
        }
    }
    public readonly record struct Color(float r, float g, float b, float a)
    {
        public static Color gray => new(.5f,.5f,.5f,1);
    }
    public static class ColorUtility
    {
        public static bool TryParseHtmlString(string text, out Color color) { color = new(.48f,.49f,.52f,1); return true; }
    }
    public static class Mathf { public static float Ceil(float value) => MathF.Ceiling(value); }
    public class Material : Object { }
}
namespace TMPro
{
    public enum TextAlignmentOptions { BaselineRight }
    public enum TextWrappingModes { NoWrap }
    public enum TextOverflowModes { Overflow }
    public enum FontStyles { Normal }
    public struct FaceInfo { public float pointSize, scale, ascentLine; }
    public class TMP_FontAsset : UnityEngine.Object { public FaceInfo faceInfo = new() { pointSize=20,scale=1,ascentLine=16 }; }
    public class TMP_Text : UnityEngine.Component
    {
        public TMP_FontAsset font = new();
        public float fontSize = 20;
        public UnityEngine.Vector4 margin = new(3,4,5,6);
        public TMP_TextInfo textInfo = new();
        public UnityEngine.RectTransform rectTransform => transform;
        public event Action<TMP_TextInfo>? OnPreRenderText;
        public void ForceMeshUpdate(bool ignoreActive, bool forceTextReparsing) => OnPreRenderText?.Invoke(textInfo);
        public void NotifyMesh() => OnPreRenderText?.Invoke(textInfo);
    }
    public class TextMeshProUGUI : TMP_Text
    {
        public TextAlignmentOptions alignment;
        public TextWrappingModes textWrappingMode;
        public bool enableAutoSizing, richText, raycastTarget;
        public TextOverflowModes overflowMode;
        public FontStyles fontStyle;
        public UnityEngine.Color color;
        public UnityEngine.Material? fontSharedMaterial;
        public string text = "";
        public UnityEngine.Vector2 GetPreferredValues(string value) => new(value.Length * fontSize * .6f,fontSize);
    }
    public class TMP_TextInfo
    {
        public int lineCount, characterCount;
        public TMP_LineInfo[] lineInfo = Array.Empty<TMP_LineInfo>();
        public TMP_CharacterInfo[] characterInfo = Array.Empty<TMP_CharacterInfo>();
    }
    public struct TMP_LineInfo { public int firstCharacterIndex, characterCount; public float baseline, ascender, descender, lineHeight; }
    public struct TMP_CharacterInfo { public int index; }
}
public class BreakPointPanel : UnityEngine.Component
{
    private readonly List<int> breakpointLines = new();
    public void Toggle(int zeroBasedLine) { if (!breakpointLines.Remove(zeroBasedLine)) breakpointLines.Add(zeroBasedLine); }
    public void Clear() => breakpointLines.Clear();
}
public class Input
{
    public string text = "";
    public TMPro.TMP_Text textComponent = new UnityEngine.GameObject().AddComponent<TMPro.TMP_Text>();
}
public class CodeWindow : UnityEngine.Component
{
    private readonly TMPro.TMP_Text codeText = new UnityEngine.GameObject().AddComponent<TMPro.TMP_Text>();
    private readonly BreakPointPanel breakpointPanel = new UnityEngine.GameObject().AddComponent<BreakPointPanel>();
    private UnityEngine.Vector2 extraSize = new(80,100);
    public UnityEngine.Vector2 ExtraSize => extraSize;
    public int SizeUpdates;
    private void UpdateSize() => SizeUpdates++;
    public BreakPointPanel Panel => breakpointPanel;
    public TMPro.TMP_Text Overlay => codeText;
    public Input CodeInput = new();
    public UnityEngine.RectTransform scrollViewRect = new UnityEngine.GameObject().Rect;
    public bool Scala = true;
}
namespace FarmerScala
{
    public class Plugin
    {
        public static Plugin? Instance;
        public void LogInfo(string message) => throw new Exception(message);
    }
}
namespace FarmerScala.Editor
{
    internal static class EditorIntegration { public static bool IsScalaWindow(CodeWindow window) => window.Scala; }
    internal static class ScalaFontRendering
    {
        private static readonly UnityEngine.Material shared = new();
        public static UnityEngine.Material CodeMaterial(TMPro.TMP_FontAsset font) => shared;
    }
}
