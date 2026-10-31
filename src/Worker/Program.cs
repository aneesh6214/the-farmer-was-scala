using System.Text;
using System.Text.Json.Nodes;
using FarmerScala.Worker;

Console.OutputEncoding = new UTF8Encoding(false);
try
{
    var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
    var supported = new HashSet<string>(["--artifact", "--entry", "--project", "--task-file", "--globals-file"], StringComparer.Ordinal);
    for (int index = 0; index < args.Length; index += 2)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            throw new ArgumentException("Expected --artifact path --entry filename [--task-file path].");
        if (!supported.Contains(args[index]))
            throw new ArgumentException("Unknown worker argument: " + args[index]);
        if (!arguments.TryAdd(args[index], args[index + 1]))
            throw new ArgumentException("Duplicate worker argument.");
    }
    string Required(string name) => arguments.TryGetValue(name, out var value) ? value : throw new ArgumentException("Missing " + name);
    string EnvironmentRequired(string name) => Environment.GetEnvironmentVariable(name) ?? throw new ArgumentException("Missing " + name);
    string artifact = Required("--artifact");
    string entry = Required("--entry");
    JsonNode? globals = arguments.TryGetValue("--globals-file", out var globalsPath) ? JsonNode.Parse(File.ReadAllText(globalsPath)) : null;
    JsonObject? task = arguments.TryGetValue("--task-file", out var taskPath)
        ? JsonNode.Parse(File.ReadAllText(taskPath)) as JsonObject ?? throw new IOException("Invalid drone task file.") : null;
    using var transport = new WorkerTransport(EnvironmentRequired("TFWR_RUN_ID"), EnvironmentRequired("TFWR_DRONE_ID"));
    transport.Connect(int.Parse(EnvironmentRequired("TFWR_PORT")), EnvironmentRequired("TFWR_TOKEN"));
    ScalaEngine? execution = null;
    try
    {
        execution = new ScalaEngine(transport, artifact, entry, task, Environment.GetEnvironmentVariable("TFWR_START_PAUSED") == "step", globals);
        JsonNode? result = execution.Run();
        transport.Event("completed", new JsonObject { ["result"] = result });
    }
    catch (Exception e) when (!transport.Cancellation.IsCancellationRequested)
    {
        transport.Event("fault", execution?.Fault(e) ?? new JsonObject { ["file"] = entry, ["line"] = 1, ["column"] = 1, ["message"] = e.Message });
        Console.Error.WriteLine(e.GetType().Name + ": " + e.Message);
        return 1;
    }
    catch (Exception) when (transport.Cancellation.IsCancellationRequested) { return 0; }
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception e)
{
    Console.Error.WriteLine("Scala worker: " + e.Message);
    return 1;
}
