using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FarmerScala.Editor;

internal enum SaveLanguage
{
    Default, NativeScala
}

internal sealed class SaveLanguageSettings
{
    public int FormatVersion { get; set; } = 1;
    public string SaveId { get; set; } = "";
    public string Language { get; set; } = "";
}

public sealed class ProjectSnapshot
{
    public string Directory { get; internal set; } = "";
    public string EntryFile { get; internal set; } = "";
    public IReadOnlyDictionary<string, string> Sources { get; internal set; } = new Dictionary<string, string>();
}

internal sealed class ProjectManifest
{
    public int FormatVersion { get; set; } = 1;
    public string SaveName { get; set; } = "";
    public bool Enabled
    {
        get; set;
    }
    public string? Language
    {
        get; set;
    }
    public string? SaveId
    {
        get; set;
    }
    public List<string> Modules { get; set; } = new List<string>();
    public WorkspaceLayout? ScalaLayout
    {
        get; set;
    }
    public WorkspaceLayout? VanillaLayout
    {
        get; set;
    }
}

// Unity-independent layout data keeps source-store persistence tests headless.
internal sealed class WorkspaceLayout
{
    public int Version { get; set; } = 1;
    public List<WindowLayout> Windows { get; set; } = new List<WindowLayout>();
    public string? ActiveWindow
    {
        get; set;
    }
    // Legacy container anchors are retained for diagnostics, not view restore.
    public float PanX
    {
        get; set;
    }
    public float PanY
    {
        get; set;
    }
    public float Zoom { get; set; } = 1f;
    // Legacy world anchors are diagnostic only: the main menu can translate
    // their Canvas parent long after the initial scene layout has completed.
    public float? FarmWorldX
    {
        get; set;
    }
    public float? FarmWorldY
    {
        get; set;
    }
    // Farm UI position in zoomContainer coordinates; independent of parent
    // Canvas translations and native container bounds rebasing.
    public float? FarmViewX
    {
        get; set;
    }
    public float? FarmViewY
    {
        get; set;
    }
}

internal sealed class WindowLayout
{
    public string Name { get; set; } = "";
    public bool IsCode
    {
        get; set;
    }
    public string? DocPage
    {
        get; set;
    }
    public float X
    {
        get; set;
    }
    public float Y
    {
        get; set;
    }
    public float Width
    {
        get; set;
    }
    public float Height
    {
        get; set;
    }
    public float ScrollY
    {
        get; set;
    }
    public bool Minimized
    {
        get; set;
    }
    public string? DockedParent
    {
        get; set;
    }
    public int Order
    {
        get; set;
    }
}

/// <summary>Original mod source storage. Never writes vanilla top-level .py files.</summary>
internal sealed class SourceStore
{
    private readonly string snapshotRoot;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public SourceStore(string stateRoot)
    {
        snapshotRoot = Path.Combine(Path.GetFullPath(stateRoot), "runs");
    }

    public static string ProjectRoot(string saveDirectory) => Path.Combine(saveDirectory, ".farmer-scala");
    public static string SourceDirectory(string saveDirectory) => Path.Combine(ProjectRoot(saveDirectory), "src");
    public static string LanguageFile(string saveDirectory) => Path.Combine(ProjectRoot(saveDirectory), "language.json");

    // Native backups run on a background thread while the editor persists layout/sources.
    // Serialize complete per-save operations, including reads, so backup copies never
    // observe a half-updated project or race a Windows atomic file replacement.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> saveLocks =
        new System.Collections.Concurrent.ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    internal static IDisposable HoldSaveFiles(string saveDirectory)
        => new SaveFileLease(saveLocks.GetOrAdd(Path.GetFullPath(saveDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), _ => new object()));
    private sealed class SaveFileLease : IDisposable
    {
        private object? gate;
        internal SaveFileLease(object value) { gate = value; System.Threading.Monitor.Enter(value); }
        public void Dispose()
        {
            if (gate == null) return;
            var value = gate; gate = null; System.Threading.Monitor.Exit(value);
        }
    }
    public SaveLanguage ReadLanguage(string saveDirectory)
    {
        using var access = HoldSaveFiles(saveDirectory);
        return ReadState(saveDirectory).Language;
    }

    /// <summary>Commit a new save's choice before native save creation. Never converts an existing save.</summary>
    public void InitializeLanguage(string saveDirectory, string saveName, SaveLanguage language)
    {
        using var access = HoldSaveFiles(saveDirectory);
        CheckLanguage(language);
        var state = ReadState(saveDirectory);
        if (state.Manifest == null && state.Settings == null)
        {
            if (File.Exists(Path.Combine(saveDirectory, "save.json")) || File.Exists(saveDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".json") ||
                System.IO.Directory.Exists(saveDirectory) && System.IO.Directory.EnumerateFileSystemEntries(saveDirectory).Any())
                throw new IOException("Choose a language only when creating a new, unused save.");
        }
        else if (state.Language != language)
            throw LanguageLocked();
        var settings = EnsureMarker(saveDirectory, language, PreferredId(state));
        if (state.Manifest != null || language == SaveLanguage.NativeScala)
        {
            var manifest = state.Manifest ?? new ProjectManifest();
            manifest.SaveName = saveName;
            manifest.Enabled = language == SaveLanguage.NativeScala;
            manifest.Language = Name(language);
            manifest.SaveId = settings.SaveId;
            WriteManifest(saveDirectory, manifest, language);
        }
    }

    /// <summary>Migrate legacy opt-in metadata without touching sources. Unmarked ordinary saves stay read-only.</summary>
    public SaveLanguage FreezeLanguage(string saveDirectory, string saveName)
    {
        using var access = HoldSaveFiles(saveDirectory);
        var state = ReadState(saveDirectory);
        if (state.Manifest == null && state.Settings == null)
            return SaveLanguage.Default;
        var settings = EnsureMarker(saveDirectory, state.Language, PreferredId(state));
        if (state.Manifest != null)
        {
            var manifest = state.Manifest;
            manifest.SaveName = saveName; // Advisory only: native save-folder renames retain the stable SaveId.
            manifest.Language = Name(state.Language);
            manifest.SaveId = settings.SaveId;
            manifest.Enabled = state.Language == SaveLanguage.NativeScala;
            WriteManifest(saveDirectory, manifest, state.Language);
        }
        return state.Language;
    }

    public ProjectManifest ReadManifest(string saveDirectory)
    {
        using var access = HoldSaveFiles(saveDirectory);
        var state = ReadState(saveDirectory);
        var manifest = state.Manifest ?? new ProjectManifest();
        if (state.Settings != null)
        {
            manifest.Language = Name(state.Language);
            manifest.SaveId = state.Settings.SaveId;
        }
        manifest.Enabled = state.Language == SaveLanguage.NativeScala;
        return manifest;
    }

    public void SaveLayout(string saveDirectory, bool scala, WorkspaceLayout layout)
    {
        using var access = HoldSaveFiles(saveDirectory);
        if (!IsUsableLayout(layout))
            throw new IOException("Unsupported or invalid Scala workspace layout.");
        var manifest = ReadManifest(saveDirectory);
        if (scala)
            manifest.ScalaLayout = layout;
        else
            manifest.VanillaLayout = layout;
        WriteManifest(saveDirectory, manifest, manifest.Enabled ? SaveLanguage.NativeScala : SaveLanguage.Default);
    }

    public static bool IsUsableLayout(WorkspaceLayout? layout)
    {
        bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        return layout != null && layout.Version == 1 && layout.Windows != null &&
            Finite(layout.PanX) && Finite(layout.PanY) && Finite(layout.Zoom) && layout.Zoom > 0f &&
            layout.FarmWorldX.HasValue == layout.FarmWorldY.HasValue &&
            (!layout.FarmWorldX.HasValue || (Finite(layout.FarmWorldX.Value) && Finite(layout.FarmWorldY!.Value))) &&
            layout.FarmViewX.HasValue == layout.FarmViewY.HasValue &&
            (!layout.FarmViewX.HasValue || (Finite(layout.FarmViewX.Value) && Finite(layout.FarmViewY!.Value))) &&
            layout.Windows.All(x => x != null && !string.IsNullOrWhiteSpace(x.Name) &&
                (x.IsCode || !string.IsNullOrWhiteSpace(x.DocPage)) &&
                !StringComparer.OrdinalIgnoreCase.Equals(x.Name, x.DockedParent) &&
                Finite(x.X) && Finite(x.Y) && Finite(x.Width) && Finite(x.Height) && Finite(x.ScrollY) && x.Width >= 0f && x.Height >= 0f) &&
            layout.Windows.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == layout.Windows.Count;
    }

    public Dictionary<string, string> ReadSources(string saveDirectory)
    {
        using var access = HoldSaveFiles(saveDirectory);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string source = SourceDirectory(saveDirectory);
        if (!System.IO.Directory.Exists(source))
            return result;
        var canonicalNames = ReadManifest(saveDirectory).Modules.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
        foreach (string file in System.IO.Directory.GetFiles(source, "*.scala", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            // Windows may retain the physical file's previous casing after a
            // case-only rename; the owned window manifest is authoritative.
            if (canonicalNames.TryGetValue(name, out var canonical))
                name = canonical;
            result.Add(name, File.ReadAllText(file, Utf8).Replace("\r", ""));
        }
        ValidateModuleSet(result.Keys);
        return result;
    }

    public void SaveSources(string saveDirectory, string saveName, bool enabled, IDictionary<string, string> sources)
    {
        using var access = HoldSaveFiles(saveDirectory);
        var manifest = ReadManifest(saveDirectory);
        var language = manifest.Enabled ? SaveLanguage.NativeScala : SaveLanguage.Default;
        if (enabled != manifest.Enabled)
            throw LanguageLocked();
        ValidateModuleSet(sources.Keys);
        foreach (string name in manifest.Modules)
            ValidateFileName(name);
        EnsureMarker(saveDirectory, language, manifest.SaveId);
        string source = SourceDirectory(saveDirectory);
        System.IO.Directory.CreateDirectory(source);
        foreach (var entry in sources)
        {
            ValidateFileName(entry.Key);
            string path = Path.Combine(source, entry.Key + ".scala");
            if (!File.Exists(path) || File.ReadAllText(path, Utf8) != entry.Value)
                AtomicWrite(path, entry.Value);
        }
        // Only modules previously owned by an editor window may be removed.
        // External package/data files and untracked source files stay intact.
        foreach (string oldModule in manifest.Modules.Except(sources.Keys, StringComparer.OrdinalIgnoreCase))
        {
            ValidateFileName(oldModule);
            string oldPath = Path.Combine(source, oldModule + ".scala");
            if (File.Exists(oldPath))
            {
                string history = Path.Combine(ProjectRoot(saveDirectory), "history");
                System.IO.Directory.CreateDirectory(history);
                File.Move(oldPath, Path.Combine(history, DateTime.UtcNow.Ticks + "-" + oldModule + ".scala"));
            }
        }
        manifest.Modules = sources.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        manifest.SaveName = saveName;
        WriteManifest(saveDirectory, manifest, language);
    }

    public ProjectSnapshot Snapshot(string saveDirectory, string entry, IDictionary<string, string> sources)
    {
        using var access = HoldSaveFiles(saveDirectory);
        ValidateFileName(entry);
        if (!sources.ContainsKey(entry))
            throw new IOException("Entry window is not part of the active Scala project: " + entry);
        string runDirectory = Path.Combine(snapshotRoot, Guid.NewGuid().ToString("N"));
        string runSource = Path.Combine(runDirectory, "src");
        CopyDirectory(SourceDirectory(saveDirectory), runSource);
        // Buffer contents are the clicked editor revision even if an external
        // editor writes between SaveSources and this copy. Untracked external
        // modules remain available, and the fingerprint covers exactly what the
        // compiler will read from this private, immutable snapshot.
        foreach (var source in sources)
        {
            ValidateFileName(source.Key);
            string target = Path.Combine(runSource, source.Key + ".scala");
            // Windows keeps a copied filename's old casing on overwrite. This
            // unique private snapshot must match the editor's module spelling.
            if (File.Exists(target))
                File.Delete(target);
            File.WriteAllText(target, source.Value, Utf8);
        }
        var snapshotSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in System.IO.Directory.GetFiles(runSource, "*.scala", SearchOption.TopDirectoryOnly))
            snapshotSources.Add(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file, Utf8));
        ValidateModuleSet(snapshotSources.Keys);
        return new ProjectSnapshot
        {
            Directory = runSource,
            EntryFile = entry + ".scala",
            Sources = snapshotSources
        };
    }

    private void WriteManifest(string saveDirectory, ProjectManifest manifest, SaveLanguage language)
    {
        var state = ReadState(saveDirectory);
        if (state.Language != language)
            throw LanguageLocked();
        var settings = EnsureMarker(saveDirectory, language, PreferredId(state));
        manifest.Language = Name(language);
        manifest.SaveId = settings.SaveId;
        manifest.Enabled = language == SaveLanguage.NativeScala;
        AtomicWrite(Path.Combine(ProjectRoot(saveDirectory), "project.json"), JsonConvert.SerializeObject(manifest, Formatting.Indented));
    }

    private sealed class LanguageState
    {
        public ProjectManifest? Manifest;
        public SaveLanguageSettings? Settings;
        public SaveLanguage Language;
    }

    private static string? PreferredId(LanguageState state) => state.Settings?.SaveId ?? state.Manifest?.SaveId;
    private static IOException LanguageLocked() => new IOException("This save's language is fixed. Create a new save to use a different language.");
    private static void CheckLanguage(SaveLanguage language)
    {
        if (language != SaveLanguage.Default && language != SaveLanguage.NativeScala)
            throw new IOException("Unknown save language.");
    }
    private static string Name(SaveLanguage language) => language == SaveLanguage.NativeScala ? "native-scala" : "default";
    private static SaveLanguage ParseLanguage(string? value)
    {
        if (value == "default")
            return SaveLanguage.Default;
        if (value == "native-scala")
            return SaveLanguage.NativeScala;
        throw new IOException("Unknown save language in metadata.");
    }
    private static string ValidId(string? value)
    {
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
            throw new IOException("Invalid save-language identity.");
        return id.ToString("N");
    }
    private static JObject ReadObject(string path)
    {
        try
        {
            return JObject.Parse(File.ReadAllText(path, Utf8), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch (JsonException error) { throw new IOException("Invalid save-language metadata: " + Path.GetFileName(path), error); }
    }
    private static JToken? Property(JObject data, string name, bool required = false)
    {
        var matches = data.Properties().Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1 || required && matches.Length == 0)
            throw new IOException("Missing or duplicate save-language field: " + name);
        return matches.Length == 0 ? null : matches[0].Value;
    }
    private static void RequireType(JToken? token, JTokenType type, string name)
    {
        if (token == null || token.Type != type)
            throw new IOException("Invalid save-language field: " + name);
    }
    private static LanguageState ReadState(string saveDirectory)
    {
        var result = new LanguageState();
        if (File.Exists(ProjectRoot(saveDirectory)))
            throw new IOException("Scala project metadata path is not a directory.");
        string projectPath = Path.Combine(ProjectRoot(saveDirectory), "project.json");
        if (System.IO.Directory.Exists(projectPath))
            throw new IOException("Scala project metadata path is not a file.");
        if (File.Exists(projectPath))
        {
            var data = ReadObject(projectPath);
            var version = Property(data, "FormatVersion", true);
            RequireType(version, JTokenType.Integer, "FormatVersion");
            if (version!.ToString(Formatting.None) != "1")
                throw new IOException("Unsupported Scala project format.");
            RequireType(Property(data, "Enabled", true), JTokenType.Boolean, "Enabled");
            var language = Property(data, "Language");
            var saveId = Property(data, "SaveId");
            if (language != null && language.Type != JTokenType.Null)
                RequireType(language, JTokenType.String, "Language");
            if (saveId != null && saveId.Type != JTokenType.Null)
                RequireType(saveId, JTokenType.String, "SaveId");
            var modules = Property(data, "Modules");
            if (modules != null && (modules.Type != JTokenType.Array || modules.Any(x => x.Type != JTokenType.String)))
                throw new IOException("Invalid Scala module metadata.");
            try
            {
                result.Manifest = data.ToObject<ProjectManifest>() ?? throw new IOException("Empty Scala project metadata.");
            }
            catch (JsonException error) { throw new IOException("Invalid Scala project metadata.", error); }
            var manifest = result.Manifest;
            foreach (string module in manifest.Modules)
                ValidateFileName(module);
            if (manifest.Modules.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Modules.Count)
                throw new IOException("Duplicate Scala module metadata.");
            result.Language = manifest.Enabled ? SaveLanguage.NativeScala : SaveLanguage.Default;
            if (manifest.Language != null)
            {
                if (ParseLanguage(manifest.Language) != result.Language)
                    throw new IOException("Save language conflicts with the legacy enabled setting.");
                manifest.SaveId = ValidId(manifest.SaveId);
            }
            else if (manifest.SaveId != null)
                throw new IOException("Incomplete save-language metadata.");
        }
        string languagePath = LanguageFile(saveDirectory);
        if (System.IO.Directory.Exists(languagePath))
            throw new IOException("Save-language metadata path is not a file.");
        if (File.Exists(languagePath))
        {
            var data = ReadObject(languagePath);
            var version = Property(data, "FormatVersion", true);
            RequireType(version, JTokenType.Integer, "FormatVersion");
            if (version!.ToString(Formatting.None) != "1")
                throw new IOException("Unsupported save-language format.");
            RequireType(Property(data, "Language", true), JTokenType.String, "Language");
            RequireType(Property(data, "SaveId", true), JTokenType.String, "SaveId");
            var settings = data.ToObject<SaveLanguageSettings>() ?? throw new IOException("Empty save-language metadata.");
            settings.SaveId = ValidId(settings.SaveId);
            SaveLanguage language = ParseLanguage(settings.Language);
            if (result.Manifest != null && result.Language != language)
                throw new IOException("Save language metadata conflicts with the project.");
            if (result.Manifest?.SaveId != null && result.Manifest.SaveId != settings.SaveId)
                throw new IOException("Save-language identity conflicts with the project.");
            result.Settings = settings;
            result.Language = language;
        }
        return result;
    }

    private static SaveLanguageSettings EnsureMarker(string saveDirectory, SaveLanguage language, string? preferredId)
    {
        CheckLanguage(language);
        string path = LanguageFile(saveDirectory);
        SaveLanguageSettings Existing()
        {
            var state = ReadState(saveDirectory);
            var settings = state.Settings ?? throw new IOException("Save-language metadata disappeared.");
            if (state.Language != language)
                throw LanguageLocked();
            if (preferredId != null && settings.SaveId != ValidId(preferredId))
                throw new IOException("Save-language identity changed.");
            return settings;
        }
        if (File.Exists(path))
            return Existing();
        var created = new SaveLanguageSettings { SaveId = preferredId == null ? Guid.NewGuid().ToString("N") : ValidId(preferredId), Language = Name(language) };
        System.IO.Directory.CreateDirectory(ProjectRoot(saveDirectory));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonConvert.SerializeObject(created, Formatting.Indented), Utf8);
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path)) { return Existing(); }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return created;
    }

    internal static void ValidateFileName(string module)
    {
        if (string.IsNullOrWhiteSpace(module) || module.Length > 220 || !Regex.IsMatch(module,
                @"^[\p{L}_][\p{L}\p{N}_-]*(\.[\p{L}_][\p{L}\p{N}_-]*)*$", RegexOptions.CultureInvariant) ||
            module.Split('.').Any(part => part.StartsWith("__Farmer", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new IOException("Invalid Scala module name '" + module + "'. Use dot-separated names beginning with a letter or underscore; letters, digits, underscores and hyphens are allowed.");
    }

    /// <summary>Use only for user-entered create/rename text, never persisted canonical identities.</summary>
    internal static string NormalizeFileNameInput(string input)
    {
        string module = (input ?? "").Trim();
        if (module.EndsWith(".scala", StringComparison.OrdinalIgnoreCase))
            module = module.Substring(0, module.Length - 6);
        ValidateFileName(module);
        return module;
    }

    internal static void ValidateModuleSet(IEnumerable<string> modules)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string module in modules)
        {
            ValidateFileName(module);
            if (!names.Add(module))
                throw new IOException("Duplicate Scala module name: " + module);
        }
        foreach (string module in names)
            for (int dot = module.IndexOf('.'); dot >= 0; dot = module.IndexOf('.', dot + 1))
                if (names.Contains(module.Substring(0, dot)))
                    throw new IOException("Scala module '" + module.Substring(0, dot) + "' is also used as a namespace by '" + module + "'. Rename one of these files.");
    }

    private static void AtomicWrite(string path, string content)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content, Utf8);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Replace(temporary, path, path + ".bak");
                    else
                        File.Move(temporary, path);
                    return;
                }
                // A concurrent reader or scanner can briefly hold a Windows delete-sharing lock.
                // Keep the existing file intact and retry the atomic replacement for at most 100 ms.
                catch (IOException) when (attempt < 4 && File.Exists(temporary))
                {
                    System.Threading.Thread.Sleep(25);
                }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(destination);
        if (!System.IO.Directory.Exists(source))
            return;
        foreach (string file in System.IO.Directory.GetFiles(source))
        {
            if (file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
        }
        foreach (string directory in System.IO.Directory.GetDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Run snapshots do not follow directory links: " + directory);
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
