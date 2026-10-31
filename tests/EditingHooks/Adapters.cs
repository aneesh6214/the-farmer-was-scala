using System.Reflection;
public static class OptionHolder
{
    public static string GetString(string name) => "disabled";
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string name)
        {
        }
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
    [Flags]
    public enum EventModifiers
    {
        None = 0, Shift = 1, Control = 2, Alt = 4, Command = 8
    }
    public enum KeyCode
    {
        None = 0, Backspace = 8, Return = 13, Slash = 47, L = 108, KeypadEnter = 271
    }
    public sealed class Event
    {
        public KeyCode keyCode; public char character; public EventModifiers modifiers; public bool Used; public void Use() => Used = true;
    }
    public static class Input
    {
        public static string compositionString = "";
    }
    public static class Time
    {
        public static float time = 50;
    }
}
namespace TMPro
{
    public sealed class CodeInputField
    {
        public CodeWindow Parent = null!;
        public string text = "";
        public bool readOnly, isFocused = true;
        public int selectionStringAnchorPosition, selectionStringFocusPosition;
        public bool ThrowDuringLabel;
        public int stringPosition
        {
            get => selectionStringFocusPosition; set => selectionStringAnchorPosition = selectionStringFocusPosition = value;
        }
        public T? GetComponentInParent<T>() where T : class => Parent as T;
        public void ForceLabelUpdate()
        {
            if (ThrowDuringLabel)
                throw new NullReferenceException("injected native mesh failure");
        }
    }
}
public sealed class NativeCompleter
{
    private bool open, hasOwner;
    public bool IsOpen
    {
        get => open; set
        {
            open = value;
            hasOwner |= value;
        }
    }
    public bool IsMatch;
    // Actual Core CodeCompleter.Close dereferences func.CodeInput even when
    // the popup is inactive. func is null until its first successful Open.
    public void Close()
    {
        if (!hasOwner)
            throw new NullReferenceException("native CodeCompleter.func uninitialized");
        open = false;
    }
}
public sealed class NativeTooltip
{
    public void CloseTooltip()
    {
    }
}
public sealed class Workspace
{
    public NativeCompleter codeCompleter = new(); public NativeTooltip tooltip = new(); public UndoHistory undoHistory = new();
}
public sealed class UndoHistory
{
    private readonly Stack<(Func<object, object> Restore, object State)> undo = new(), redo = new();
    public int Count => undo.Count;
    public void AddChange(Func<object, object> restore, object state, object target)
    {
        undo.Push((restore, state));
        redo.Clear();
    }
    public void Undo()
    {
        var item = undo.Pop();
        redo.Push((item.Restore, item.Restore(item.State)));
    }
    public void Redo()
    {
        var item = redo.Pop();
        undo.Push((item.Restore, item.Restore(item.State)));
    }
}
public sealed class CodeWindow
{
    public TMPro.CodeInputField CodeInput = new();
    public Workspace workspace = new();
    public object gameObject = new();
    public int Updates;
    private string undoState = "", prevText = "";
    private float undoChangeTime;
    private bool undoPreventNextChange;
    public CodeWindow(string text = "")
    {
        CodeInput.Parent = this;
        CodeInput.text = text;
        CodeInput.stringPosition = text.Length;
        undoState = prevText = text;
    }
    private bool UpdateTokens()
    {
        Updates++;
        bool changed = prevText != CodeInput.text;
        prevText = CodeInput.text;
        undoPreventNextChange = false;
        return changed;
    }
    private object SetToCodeState(object state)
    {
        string reverse = CodeInput.text;
        CodeInput.text = (string)state;
        undoState = CodeInput.text;
        return reverse;
    }
    public void Load(string source)
    {
    }
}
namespace FarmerScala
{
    public sealed class Plugin
    {
        public static Plugin? Instance = new(); public readonly List<string> Logs = new(); public void LogInfo(string message) => Logs.Add(message);
    }
}
namespace FarmerScala.Editor
{
    internal static class EditorIntegration
    {
        internal static bool Enabled = true; internal static bool IsScalaWindow(CodeWindow window) => Enabled;
    }
    internal static class ScalaEditorTools
    {
        internal static int Requests; internal static void RequestFormat(CodeWindow window) => Requests++;
    }
}
