using Newtonsoft.Json.Linq;

public class Node
{
}
public sealed class CodeWindow
{
}
public static class Logger
{
    public static void Close()
    {
    }
}
public sealed class MainSim
{
    public static MainSim Inst = new();
    public bool Executing;
    public int Starts;
    public bool IsExecuting() => Executing;
    public void StartMainExecution(CodeWindow window, Node node)
    {
        Executing = true;
        Starts++;
    }
    public void StopMainExecution() => Executing = false;
}
namespace BepInEx
{
    public static class Paths
    {
        public static string CachePath = "";
    }
}
namespace FarmerScala
{
    public sealed class Plugin
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines = new();
        public void QueueOutput(string message) => Lines.Enqueue(message);
        public void ReportFailure(string message) => Lines.Enqueue(message);
        public void LogInfo(string message) => Lines.Enqueue(message);
        public void LogError(string message) => Lines.Enqueue(message);
    }
    public static class SimulationParseScope
    {
        public static bool Active => false;
    }
}
namespace FarmerScala.Editor
{
    public static class ScalaAnalysis
    {
        public static void CompilerReady(string artifact)
        {
        }
    }
    public sealed class ProjectSnapshot
    {
        public string Directory { get; set; } = "";
        public string EntryFile { get; set; } = "main.scala";
        public IReadOnlyDictionary<string, string> Sources { get; set; } = new Dictionary<string, string>();
    }
    public static class EditorIntegration
    {
        public static bool Enabled = true;
        public static ProjectSnapshot Project = new();
        public static string? Error;
        public static ProjectSnapshot CaptureProject(CodeWindow window) => Project;
        public static void BeginRun(CodeWindow window)
        {
        }
        public static void EndRun()
        {
        }
        public static void BeginStep(CodeWindow window)
        {
        }
        public static void EndStep()
        {
        }
        public static void Highlight(string file, int line)
        {
        }
        public static void SetError(string file, int line, int column, string message, string? fallbackFile = null, bool sourceHasNativeWindow = true, int? endLine = null, int? endColumn = null) => Error = file + ":" + line + ":" + column + ":" + message;
    }
}
namespace FarmerScala.Game
{
    public sealed class NativeGameSession : IDisposable
    {
        public event Action<int, JObject>? TaskStarting;
        public event Action<int>? DroneRemoved;
        public NativeGameSession(CodeWindow window, Action<string> log)
        {
        }
        public Node RootNode => new();
        public bool HasLiveExecution => false;
        public Node CreateSimulationNode(CodeWindow window) => new();
        public JObject BuildManifest() => new() { ["api"] = new JArray(), ["enums"] = new JObject() };
        public void Enqueue(int drone, JObject request, Action<JToken> success, Action<string, string> fail) => success((string?)request["op"] switch
        {
            "get_world_size" or "get_pos_x" or "get_pos_y" => new JValue(4),
            "can_harvest" => new JValue(false),
            _ => JValue.CreateNull()
        });
        public void CompleteDrone(int drone, JToken? result) => MainSim.Inst.Executing = false;
        public static string? InjectedFault;
        public static JObject? InjectedLocation;
        public JObject? FaultLocation => InjectedLocation;
        public string? PollFault() => InjectedFault;
        public void Cancel()
        {
        }
        public void Dispose()
        {
        }
    }
}
