using System.Text.Json;
using FarmerScala.Compiler;

Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
string? Arg(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, "sdk")))
            return dir.FullName;
    return AppContext.BaseDirectory;
}
string root = FindRoot();
string sdk = Path.GetFullPath(Arg("--sdk") ?? Path.Combine(root, "sdk"));
string toolchain = Path.GetFullPath(Arg("--toolchain") ?? Environment.GetEnvironmentVariable("FARMER_SCALA_TOOLCHAIN") ?? Path.Combine(root, ".local", "toolchain"));
using var compiler = new CompilerService(sdk, toolchain);
while (await Console.In.ReadLineAsync() is { } line)
{
    object? id = null;
    try
    {
        if (line.Length > 1048576)
            throw new InvalidDataException("Compiler request exceeds the 1 MiB limit.");
        using var document = JsonDocument.Parse(line);
        var request = document.RootElement;
        id = request.TryGetProperty("id", out var requestId) ? requestId.Clone() : null;
        if (request.GetProperty("op").GetString() != "compile")
            throw new InvalidDataException("Unsupported compiler operation.");
        var result = await compiler.CompileAsync(request.GetProperty("project").GetString()!, request.GetProperty("entry").GetString()!, request.GetProperty("output").GetString()!);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            id,
            result.ok,
            result.artifact,
            result.sourceMap,
            result.hash,
            result.diagnostics,
            result.elapsedMs,
            result.cached,
            result.dependencies
        }));
    }
    catch (Exception error)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            id,
            ok = false,
            artifact = (string?)null,
            diagnostics = new[] { new Diagnostic("", 1, 1, 1, 1, error.Message) }
        }));
    }
}
