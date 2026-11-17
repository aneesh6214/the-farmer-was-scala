using System.Diagnostics;
using System.Text.Json;

namespace FarmerScala.Compiler;

internal static class CompiledDependencies
{
    internal static async Task<string[]> Read(string toolchain, string output, string entry, IEnumerable<string> modules, CancellationToken cancellation)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(toolchain))!;
        string analysis = new[] { Path.Combine(parent, "analysis"), Path.Combine(parent, "tools", "analysis") }
            .FirstOrDefault(p => File.Exists(Path.Combine(p, "classpath.txt")))
            ?? throw new FileNotFoundException("The bundled source analysis service is missing.");
        string classpath = string.Join(Path.PathSeparator, File.ReadAllLines(Path.Combine(analysis, "classpath.txt"))
            .Where(n => n.Length > 0).Select(n => Path.Combine(analysis, n)));
        var info = new ProcessStartInfo(Path.Combine(toolchain, "java", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-Xmx256m", "-cp", classpath, "farmer.analysis.CompiledDependencies", output, Path.GetFileNameWithoutExtension(entry) }.Concat(modules))
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not read compiled source dependencies.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        using var registration = cancellation.Register(() => { try { process.Kill(true); } catch { } });
        await process.WaitForExitAsync(cancellation);
        string text = await stdout;
        if (process.ExitCode != 0) throw new IOException("Could not read compiled source dependencies: " + await stderr);
        return JsonSerializer.Deserialize<string[]>(text) ?? throw new InvalidDataException("Missing compiled dependency metadata.");
    }
}
