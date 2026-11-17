using System.Diagnostics;
using System.Reflection;
using FarmerScala.Editor;

internal static class ProviderChecks
{
    internal static async Task Run(string root, Action<bool, string> check)
    {
        string install = Path.Combine(root, ".local");
        string analysis = Path.Combine(install, "tools/analysis");
        Directory.CreateDirectory(analysis);
        foreach (string file in Directory.GetFiles(Path.Combine(install, "analysis")))
            File.Copy(file, Path.Combine(analysis, Path.GetFileName(file)), true);
        Directory.CreateDirectory(Path.Combine(analysis, "sources"));
        foreach (string file in Directory.GetFiles(Path.Combine(install, "analysis", "sources")))
            File.Copy(file, Path.Combine(analysis, "sources", Path.GetFileName(file)), true);
        string sdk = Path.Combine(install, "sdk");
        foreach (string file in Directory.GetFiles(Path.Combine(root, "sdk"), "*.scala", SearchOption.AllDirectories))
        {
            string target = Path.Combine(sdk, Path.GetRelativePath(Path.Combine(root, "sdk"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
        var targetLibraries = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(install, "compiler-checks/build/compiler-classpath.json")))
            .Values<string>().Where(p => p!.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(install, p!).Replace('\\', '/')).ToArray();
        File.WriteAllText(Path.Combine(analysis, "editor-classpath.json"), Newtonsoft.Json.JsonConvert.SerializeObject(targetLibraries));
        check(targetLibraries.Length > 0 && targetLibraries.All(p => !Path.IsPathRooted(p)), "editor bootstrap uses portable library paths without compiled player classes");
        BepInEx.Paths.CachePath = Path.Combine(install, "analysis-provider-cache");
        using (var formatOnly = new CompilerAnalysisProvider(install))
        {
            // No build/configure call: all editor services initialize from bundled libraries.
            var snapshot = new Dictionary<string, string> { ["models.modeling"] = "val count=3\nquick_print(count)\n", ["broken"] = "val invalid = (\n" };
            var wait = Stopwatch.StartNew();
            AnalysisFormatResult formatted = new();
            bool ready = false;
            while (wait.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (formatOnly.TryFormat(snapshot, "models.modeling", out formatted))
                {
                    ready = true;
                    break;
                }
                await Task.Delay(20);
            }
            check(ready && formatted.Error == null && formatted.Source?.Contains("val count = 3", StringComparison.Ordinal) == true,
                "formatting works before first successful build and despite another invalid module");
            wait.Restart();
            IReadOnlyList<AnalysisDiagnostic> coldIssues = Array.Empty<AnalysisDiagnostic>();
            ready = false;
            while (wait.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (formatOnly.TryDiagnostics(snapshot, out coldIssues)) { ready = true; break; }
                await Task.Delay(20);
            }
            check(ready && coldIssues.Any(i => i.File == "broken"), "type diagnostics work on a broken project before any successful compilation");
        }
        using var provider = new CompilerAnalysisProvider(install);
        var original = new Dictionary<string, string> { ["f0"] = "class Café:\n  def récolte(): Int = 1\nval café = Café()\ncafé.\n" };
        int Cursor(IReadOnlyDictionary<string, string> source) => source["f0"].Length - 1;
        var timer = Stopwatch.StartNew();
        bool immediate = provider.TryComplete(original, "f0", Cursor(original), out _);
        check(!immediate && timer.ElapsedMilliseconds < 200, "Unity-facing provider queues initial analysis without waiting for JVM");
        async Task<List<string>> Complete(IReadOnlyDictionary<string, string> source)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (provider.TryComplete(source, "f0", Cursor(source), out var names))
                    return names;
                await Task.Delay(20);
            }
            throw new Exception("Timed out waiting for actual cached compiler completion.");
        }
        var first = await Complete(original);
        check(first.Contains("récolte"), "C# provider and Java helper exchange Unicode source as UTF-8");
        var changed = new Dictionary<string, string> { ["f0"] = original["f0"].Replace("récolte", "newMethod", StringComparison.Ordinal) };
        check(!provider.TryComplete(changed, "f0", Cursor(changed), out _), "changed snapshot never reads stale completion cache");
        var second = await Complete(changed);
        check(second.Contains("newMethod") && !second.Contains("récolte"), "compiler completion refreshes removed and added members");
        async Task<T> Wait<T>(Func<(bool Ready, T Value)> poll)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                var (ready, value) = poll();
                if (ready)
                    return value;
                await Task.Delay(20);
            }
            throw new Exception("Timed out waiting for editor tools provider.");
        }
        var bad = new Dictionary<string, string> { ["f0"] = "val x: Int = \"bad\"\n" };
        timer.Restart();
        bool queued = provider.TryDiagnostics(bad, out _);
        check(!queued && timer.ElapsedMilliseconds < 200, "diagnostics provider queues without blocking Unity");
        var issues = await Wait(() => (provider.TryDiagnostics(bad, out var values), values));
        check(issues.Count > 0 && issues[0].File == "f0", "provider transports real compiler diagnostics");
        var good = new Dictionary<string, string> { ["f0"] = "val x: Int = 4\n" };
        check(!provider.TryDiagnostics(good, out _), "changed source cannot read obsolete diagnostic cache");
        var cleared = await Wait(() => (provider.TryDiagnostics(good, out var values), values));
        check(cleared.Count == 0, "provider transports cleared diagnostics for corrected source");
        var formatInput = new Dictionary<string, string> { ["f0"] = "val x=4\nquick_print(x)\n" };
        var result = await Wait(() => (provider.TryFormat(formatInput, "f0", out var value), value));
        check(result.Source?.Contains("val x = 4", StringComparison.Ordinal) == true && result.Error == null, "provider transports explicit formatting results");
        var process = (Process?)typeof(CompilerAnalysisProvider).GetField("process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(provider);
        int pid = process?.Id ?? throw new Exception("Analysis worker process did not start.");
        timer.Restart();
        provider.Dispose();
        check(timer.ElapsedMilliseconds < 1000 && !provider.TryComplete(changed, "f0", Cursor(changed), out _), "provider shutdown revokes analysis promptly");
        bool killed;
        try
        {
            using var child = Process.GetProcessById(pid);
            killed = child.WaitForExit(3000);
        }
        catch (ArgumentException) { killed = true; }
        check(killed, "analysis Java process is owned and terminated on dispose");
    }
}
