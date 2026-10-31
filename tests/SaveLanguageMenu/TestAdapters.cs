// Headless adapters only. The production menu patches and SourceStore are linked
// into this executable; these small game interfaces do not exercise Unity or Harmony.
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace BepInEx
{
    internal static class Paths
    {
        public static string CachePath = "";
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string name)
        {
            TargetType = type;
            TargetName = name;
        }
        public Type TargetType
        {
            get;
        }
        public string TargetName
        {
            get;
        }
    }
    internal static class AccessTools
    {
        public static FieldInfo Field(Type type, string name) => type.GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
    }
}

namespace TMPro
{
    public sealed class TextMeshProUGUI
    {
        public string text = "";
    }
    public sealed class TMP_InputField
    {
        public string text = "";
    }
}

namespace FarmerScala
{
    internal sealed class Plugin
    {
        public static Plugin? Instance = new();
        public readonly List<string> Messages = [];
        public void LogError(string message) => Messages.Add(message);
        public void LogInfo(string message) => Messages.Add(message);
        public void QueueOutput(string message) => Messages.Add(message);
        public void ReportFailure(string message) => Messages.Add(message);
    }
    internal sealed class ScalaSaveBadge
    {
        public bool IsScala { get; private set; }
        public void Configure(TMPro.TextMeshProUGUI label, bool scala)
        {
            IsScala = scala;
        }
    }
    internal static class MenuPatches
    {
        public static object? Call(string patch, string method, params object?[] args)
        {
            var type = typeof(SaveLanguageMenu).GetNestedType(patch, BindingFlags.NonPublic)!;
            try
            {
                return type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
        public static void Reset()
        {
            typeof(SaveLanguageMenu).GetField("pendingChooser", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
            typeof(SaveLanguageMenu).GetField("choicePopup", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
            typeof(SaveLanguageMenu).GetField("creating", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
            typeof(SaveLanguageMenu).GetField("choiceGeneration", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, 0L);
        }
    }
    // The native layout adapter records scope lifetime; actual production fit
    // calculations are linked separately and checked for geometric invariants.
    internal static class SaveLanguagePopupLayout
    {
        public static WarningPopup? Active;
        public static void Apply(WarningPopup popup)
        {
            Active = popup;
            popup.Stacked = true;
        }
        public static void Restore(WarningPopup popup)
        {
            if (!ReferenceEquals(Active, popup))
                return;
            popup.Stacked = false;
            Active = null;
        }
    }
}

namespace FarmerScala.Editor
{
    internal static class EditorIntegration
    {
        public static readonly List<(string Old, string New)> Renames = [];
        public static void SaveRenamed(string oldName, string newName) => Renames.Add((oldName, newName));
    }
}

public sealed class MainSim
{
    public static MainSim? Inst;
    public readonly WarningPopup warningPopup = new();
}

public sealed class WarningPopup
{
    public readonly record struct ButtonData(string text, Action callback);
    public string Text = "";
    public List<ButtonData> Buttons = [];
    public bool Open;
    public bool Stacked;
    public int Shown;
    public void ShowPopup(string text, List<ButtonData> buttons)
    {
        FarmerScala.MenuPatches.Call("PopupReused", "Prefix", this);
        Text = text;
        Buttons = buttons;
        Open = true;
        Shown++;
    }
    public void Close()
    {
        FarmerScala.MenuPatches.Call("PopupClosed", "Prefix", this);
        Open = false;
        Disable();
    }
    public void Disable()
    {
        FarmerScala.MenuPatches.Call("PopupDisabled", "Prefix", this);
        Open = false;
    }
}

public static class Saver
{
    public static string Root = "";
    public static int NativeCreations;
    public static Action<string>? ImmediatelyBeforeCreate;
    public static Action<string>? AfterLanguageBeforeNativeFile;
    public static string GetPathOfSaveDirectory(string name) => Path.Combine(Root, name);
    public static string GetPathOfSaveFile(string name)
    {
        string path = Path.Combine(GetPathOfSaveDirectory(name), "save.json");
        string legacy = Path.Combine(Root, name + ".json");
        return !File.Exists(path) && File.Exists(legacy) ? legacy : path;
    }
    public static void CreateNewSaveGame(string saveName)
    {
        ImmediatelyBeforeCreate?.Invoke(saveName);
        FarmerScala.MenuPatches.Call("NewSaveLanguage", "Prefix", saveName);
        AfterLanguageBeforeNativeFile?.Invoke(saveName);
        Directory.CreateDirectory(GetPathOfSaveDirectory(saveName));
        // A deliberate truncating native-file adapter makes ordering/collision
        // regressions visible without touching any installed game data.
        File.WriteAllText(GetPathOfSaveFile(saveName), "native save");
        NativeCreations++;
    }
    public static bool RenameSave(string saveName, string newSaveName)
    {
        bool success = !string.IsNullOrWhiteSpace(newSaveName) && !Directory.Exists(GetPathOfSaveDirectory(newSaveName))
            && Directory.Exists(GetPathOfSaveDirectory(saveName));
        if (success)
            Directory.Move(GetPathOfSaveDirectory(saveName), GetPathOfSaveDirectory(newSaveName));
        FarmerScala.MenuPatches.Call("RenameLanguageIdentity", "Postfix", saveName, newSaveName, success);
        return success;
    }
}

public sealed class SaveChooser
{
    public readonly Dictionary<string, SaveOption> Options = [];
    public int NativeCreateCalls;
    public void CreateNewSave()
    {
        if (!(bool)FarmerScala.MenuPatches.Call("NewGameChoice", "Prefix", this)!)
            return;
        NativeCreateCalls++;
        string name = GenerateUnusedSaveName();
        Saver.CreateNewSaveGame(name);
        var option = new SaveOption();
        Options.Add(name, option);
        option.Setup(name, this);
        option.Edit();
    }
    public static string GenerateUnusedSaveName()
    {
        object?[] args = [null];
        if (!(bool)FarmerScala.MenuPatches.Call("FreshNameGuard", "Prefix", args)!)
            return (string)args[0]!;
        for (int i = 0; ; i++)
            if (!File.Exists(Saver.GetPathOfSaveFile("Save" + i)))
                return "Save" + i;
    }
    public bool RenameSave(string oldName, string newName)
    {
        if (!Saver.RenameSave(oldName, newName))
            return false;
        SaveOption option = Options[oldName];
        Options.Remove(oldName);
        Options.Add(newName, option);
        return true;
    }
}

public sealed class SaveOption
{
    public readonly SaveOptionComponents gameObject = new();
    public T? GetComponent<T>() where T : class => gameObject.Badge as T;
    private string fileName = "";
    private readonly TMPro.TextMeshProUGUI nameText = new();
    private readonly TMPro.TMP_InputField nameEditInput = new();
    private SaveChooser saveChooser = null!;
    public string FileName => fileName;
    public string Label => nameText.text;
    public string Input
    {
        get => nameEditInput.text; set => nameEditInput.text = value;
    }
    public bool Editing;
    public void Setup(string name, SaveChooser chooser)
    {
        fileName = name;
        nameText.text = name;
        saveChooser = chooser;
        FarmerScala.MenuPatches.Call("SaveListLanguage", "Postfix", this);
    }
    public void Edit()
    {
        Editing = true;
        nameEditInput.text = fileName;
    }
    public void EditFinished()
    {
        if (nameEditInput.text != fileName && saveChooser.RenameSave(fileName, nameEditInput.text))
        {
            fileName = nameEditInput.text;
            nameText.text = fileName;
        }
        Editing = false;
        FarmerScala.MenuPatches.Call("RenamedSaveLanguage", "Postfix", this);
    }
}

public sealed class SaveOptionComponents
{
    internal FarmerScala.ScalaSaveBadge? Badge;
    public T AddComponent<T>() where T : new()
    {
        var component = new T();
        Badge = component as FarmerScala.ScalaSaveBadge;
        return component;
    }
}
