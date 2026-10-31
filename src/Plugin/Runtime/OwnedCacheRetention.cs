using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FarmerScala.Runtime;

/// <summary>Deletes only recognized, unpinned generations directly beneath this mod's cache.</summary>
internal sealed class OwnedCacheRetention
{
    private readonly string root;
    private readonly Action<string> log;
    private readonly object gate = new object();
    private readonly Dictionary<string, int> pins = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly int runLimit, artifactLimit;
    private readonly TimeSpan minimumAge;

    public OwnedCacheRetention(string root, Action<string> log, int runLimit = 128, int artifactLimit = 64, TimeSpan? minimumAge = null)
    {
        this.root = Full(root);
        this.log = log;
        this.runLimit = runLimit;
        this.artifactLimit = artifactLimit;
        // SourceStore creates a snapshot immediately before handing it to Warm/Play.
        // Fresh snapshots also sort into the retained newest 128 generations.
        this.minimumAge = minimumAge ?? TimeSpan.FromSeconds(15);
    }

    public IDisposable Pin(params string?[] paths)
    {
        var owned = paths.Where(p => !string.IsNullOrEmpty(p)).Select(p => Generation(p!))
            .Where(p => p != null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (gate)
        {
            foreach (string path in owned)
            {
                pins.TryGetValue(path, out int count);
                pins[path] = count + 1;
                try
                {
                    if (Directory.Exists(path) && NoReparseAncestors(path))
                        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return new Lease(this, owned);
    }

    public void Prune(bool includeCompilerArtifacts = true)
    {
        PruneCategory(Path.Combine(root, "runs"), true, runLimit);
        PruneCategory(Path.Combine(root, "artifacts"), false, artifactLimit);
        if (includeCompilerArtifacts)
            PruneCategory(Path.Combine(root, "compiler-project", "artifacts"), false, artifactLimit);
    }

    private void PruneCategory(string category, bool run, int keep)
    {
        try
        {
            if (!Directory.Exists(category) || !NoReparseAncestors(category))
                return;
            var candidates = Directory.GetDirectories(category, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new DirectoryInfo(path)).Where(info => Recognized(info.Name, run))
                .OrderByDescending(info => info.LastWriteTimeUtc).Skip(keep).ToArray();
            foreach (var candidate in candidates)
            {
                lock (gate)
                {
                    string path = Full(candidate.FullName);
                    if (pins.ContainsKey(path) || DateTime.UtcNow - candidate.LastWriteTimeUtc < minimumAge)
                        continue;
                    // Recheck absolute containment and every reparse point immediately before
                    // each recursive deletion. Never enumerate or delete a user source tree.
                    if (!SafeCandidate(path, category, run) || !SafeTree(path))
                        continue;
                    Directory.Delete(path, recursive: true);
                }
            }
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException)
        {
            log("Scala cache retention: " + error.Message);
        }
    }

    private bool SafeCandidate(string path, string category, bool run)
    {
        path = Full(path);
        category = Full(category);
        return category.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetDirectoryName(path), category, StringComparison.OrdinalIgnoreCase) &&
            path.StartsWith(category + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Recognized(Path.GetFileName(path), run) && Directory.Exists(path) && NoReparseAncestors(path);
    }

    private static bool SafeTree(string path)
    {
        var pending = new Stack<string>();
        pending.Push(path);
        string prefix = Full(path) + Path.DirectorySeparatorChar;
        while (pending.Count != 0)
            foreach (string entry in Directory.GetFileSystemEntries(pending.Pop()))
            {
                string full = Full(entry);
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
                var attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(full);
            }
        return true;
    }

    private string? Generation(string path)
    {
        string full = Full(path);
        foreach (var category in new[] { (Path.Combine(root, "runs"), true), (Path.Combine(root, "artifacts"), false), (Path.Combine(root, "compiler-project", "artifacts"), false) })
        {
            string prefix = category.Item1 + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            string relative = full.Substring(prefix.Length);
            int slash = relative.IndexOf(Path.DirectorySeparatorChar);
            string name = slash < 0 ? relative : relative.Substring(0, slash);
            return Recognized(name, category.Item2) ? Path.Combine(category.Item1, name) : null;
        }
        return null;
    }

    private static bool Recognized(string name, bool run)
    {
        if (run)
            return Guid.TryParseExact(name, "N", out _);
        return name.Length == 64 && name.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F');
    }

    private static bool NoReparseAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
        return true;
    }
    private static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private void Release(string[] paths)
    {
        lock (gate)
            foreach (string path in paths)
                if (pins.TryGetValue(path, out int count))
                {
                    if (count == 1)
                        pins.Remove(path);
                    else
                        pins[path] = count - 1;
                }
    }

    private sealed class Lease : IDisposable
    {
        private OwnedCacheRetention? owner;
        private readonly string[] paths;
        public Lease(OwnedCacheRetention owner, string[] paths)
        {
            this.owner = owner;
            this.paths = paths;
        }
        public void Dispose() => System.Threading.Interlocked.Exchange(ref owner, null)?.Release(paths);
    }
}
