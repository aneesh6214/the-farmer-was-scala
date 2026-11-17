using System;
using System.Collections.Generic;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FarmerScala.Game;
using Newtonsoft.Json.Linq;

// Exercise the production queue without constructing a Unity scene or executing
// game code. Only these fields are used before Enqueue reaches its Wake boundary.
var session = (NativeGameSession)RuntimeHelpers.GetUninitializedObject(typeof(NativeGameSession));
var flags = BindingFlags.Instance | BindingFlags.NonPublic;
void Set(string name, object value) => typeof(NativeGameSession).GetField(name, flags)!.SetValue(session, value);
object EmptyField(string name)
{
    var field = typeof(NativeGameSession).GetField(name, flags)!;
    var value = Activator.CreateInstance(field.FieldType)!;
    field.SetValue(session, value);
    return value;
}
Set("cancellation", new CancellationTokenSource());
Set("api", new HashSet<string>(StringComparer.Ordinal) { "test_operation" });
var channels = EmptyField("channels");
EmptyField("activeStates");

// Hold ConcurrentDictionary's insertion lock to force cancellation after the
// initial token check and before the new channel/request is visible to Cancel.
var tables = channels.GetType().GetField("_tables", flags)!.GetValue(channels)!;
var locks = (object[])tables.GetType().GetField("_locks", flags)!.GetValue(tables)!;
int callbacks = 0;
string? error = null;
Thread? enqueueThread = null;
Monitor.Enter(locks[0]);
Task enqueue;
try
{
    enqueue = Task.Factory.StartNew(() =>
    {
        enqueueThread = Thread.CurrentThread;
        session.Enqueue(0, new JObject { ["op"] = "test_operation" }, _ => Interlocked.Increment(ref callbacks),
            (code, _) => { error = code; Interlocked.Increment(ref callbacks); });
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    if (!SpinWait.SpinUntil(() => enqueueThread != null && (enqueueThread.ThreadState & ThreadState.WaitSleepJoin) != 0, 5000))
        throw new Exception("The enqueue did not reach the forced insertion boundary.");
    session.Cancel();
}
finally { Monitor.Exit(locks[0]); }
enqueue.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
if (callbacks != 1 || error != "Cancelled") throw new Exception("A request inserted after cancellation did not complete exactly once.");
session.Cancel();
if (callbacks != 1) throw new Exception("Repeated cancellation completed a command twice.");
Console.WriteLine("PASS 2 NativeSession cancellation checks: post-drain enqueue callback completion and idempotence.");

// Force an enqueue across native retirement/drain using a gated backing queue.
// The native state/execution are metadata-only objects; no Unity/game action runs.
var retiredSession = (NativeGameSession)RuntimeHelpers.GetUninitializedObject(typeof(NativeGameSession));
foreach (string name in new[] { "channels", "activeStates" })
{
    var field = typeof(NativeGameSession).GetField(name, flags)!;
    field.SetValue(retiredSession, Activator.CreateInstance(field.FieldType));
}
typeof(NativeGameSession).GetField("cancellation", flags)!.SetValue(retiredSession, new CancellationTokenSource());
typeof(NativeGameSession).GetField("api", flags)!.SetValue(retiredSession, new HashSet<string>(StringComparer.Ordinal) { "test_operation" });
typeof(NativeGameSession).GetField("log", flags)!.SetValue(retiredSession, (Action<string>)(_ => { }));
var commandType = typeof(NativeGameSession).GetNestedType("Command", BindingFlags.NonPublic)!;
var channelType = typeof(NativeGameSession).GetNestedType("Channel", BindingFlags.NonPublic)!;
var bindingType = typeof(NativeGameSession).GetNestedType("StateBinding", BindingFlags.NonPublic)!;
var channel = Activator.CreateInstance(channelType)!;
var timeline = new List<string>();
bool removedFlagWasVisible = false;
string? lateError = null;
object Command(string name) => Activator.CreateInstance(commandType, new object[] {
    new JObject { ["op"] = "test_operation" }, (Action<JToken>)(_ => timeline.Add("unexpected success")),
    (Action<string, string>)((_, _) => timeline.Add(name)) })!;
using var enqueueEntered = new ManualResetEventSlim();
using var enqueueReleased = new ManualResetEventSlim();
var queueType = typeof(GatedQueue<>).MakeGenericType(commandType);
var queue = Activator.CreateInstance(queueType, enqueueEntered, enqueueReleased)!;
queueType.GetMethod("Seed")!.Invoke(queue, new[] { Command("queued") });
var requestsType = typeof(BlockingCollection<>).MakeGenericType(commandType);
var requests = Activator.CreateInstance(requestsType, queue, 64)!;
channelType.GetField("Requests")!.SetValue(channel, requests);
channelType.GetField("Pending")!.SetValue(channel, Command("pending"));
var nativeState = (ProgramState)RuntimeHelpers.GetUninitializedObject(typeof(ProgramState));
var nativeExecution = (Execution)RuntimeHelpers.GetUninitializedObject(typeof(Execution));
typeof(Execution).GetField("states", flags)!.SetValue(nativeExecution, new List<ProgramState>());
var binding = Activator.CreateInstance(bindingType, nativeState, nativeExecution, channel)!;
foreach (var entry in new[] { (Field: "channels", Value: channel), (Field: "activeStates", Value: binding) })
{
    object dictionary = typeof(NativeGameSession).GetField(entry.Field, flags)!.GetValue(retiredSession)!;
    dictionary.GetType().GetMethod("TryAdd")!.Invoke(dictionary, new[] { (object)7, entry.Value });
}
retiredSession.DroneRemoved += _ =>
{
    removedFlagWasVisible = (bool)channelType.GetField("Removed")!.GetValue(channel)!;
    timeline.Add("retired");
};
var lateEnqueue = Task.Factory.StartNew(() => retiredSession.Enqueue(7, new JObject { ["op"] = "test_operation" },
    _ => timeline.Add("unexpected late success"), (code, _) => { lateError = code; timeline.Add("late"); }),
    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
try
{
    if (!enqueueEntered.Wait(5000))
        throw new Exception("The late enqueue did not reach its forced insertion boundary.");
    retiredSession.PollFault();
}
finally { enqueueReleased.Set(); }
lateEnqueue.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
if (!removedFlagWasVisible || !timeline.SequenceEqual(new[] { "retired", "pending", "queued", "late" }))
    throw new Exception("Retirement authority was not revoked before every command failure: " + string.Join(",", timeline));
Console.WriteLine("PASS retirement notification and Removed flag precede pending/queued failures");
if (lateError != "DroneRemoved" || (int)requestsType.GetProperty("Count")!.GetValue(requests)! != 0)
    throw new Exception("An enqueue inserted after retirement/drain retained a command or missed its callback.");
Console.WriteLine("PASS post-retirement insertion fails exactly once and leaves no queued callback");

// A native side effect can fail after its binding returned. Preserve its queued call-site range.
var faultState = (ProgramState)RuntimeHelpers.GetUninitializedObject(typeof(ProgramState));
faultState.currentExecuteException = new ExecuteException("late native failure");
var faultChannel = Activator.CreateInstance(channelType)!;
var location = new JObject { ["file"] = "helper.scala", ["line"] = 9, ["column"] = 5, ["endLine"] = 9, ["endColumn"] = 10 };
var faultRequest = new JObject { ["op"] = "plant", ["location"] = location };
var faultCommand = Activator.CreateInstance(commandType, new object[] { faultRequest, (Action<JToken>)(_ => { }), (Action<string, string>)((_, _) => { }) })!;
channelType.GetField("Pending")!.SetValue(faultChannel, faultCommand);
var faultBinding = Activator.CreateInstance(bindingType, faultState, nativeExecution, faultChannel)!;
object activeFaults = typeof(NativeGameSession).GetField("activeStates", flags)!.GetValue(retiredSession)!;
activeFaults.GetType().GetMethod("TryAdd")!.Invoke(activeFaults, new[] { (object)8, faultBinding });
if (retiredSession.PollFault() != "late native failure" || !JToken.DeepEquals(retiredSession.FaultLocation, location))
    throw new Exception("Late native side-effect failure lost its Scala call-site range.");
faultRequest["location"]!["line"] = 99;
if ((int?)retiredSession.FaultLocation?["line"] != 9) throw new Exception("Native fault retained a mutable request location.");
Console.WriteLine("PASS native side-effect failure retains an independent exact Scala source range");

// Run the production patch decisions against the actual installed method
// metadata. No Unity object, native action, Steam callback or network is invoked.
object? Invoke(Type type, string method, params object?[] arguments)
    => type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);
void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
var targets = ((IEnumerable<MethodBase>)Invoke(typeof(FarmerScala.SteamScoreGuard), "TargetMethods")!).ToArray();
Check(targets.Length == 10 && targets.All(method => method != null) && targets.Distinct().Count() == targets.Length, "all 10 leaderboard interception targets exist in installed game");
var ranked = (MainSim.LeaderboardStartArgs)RuntimeHelpers.GetUninitializedObject(typeof(MainSim.LeaderboardStartArgs));
ranked.leaderboardType = LeaderboardType.reset;
var simulated = (MainSim.LeaderboardStartArgs)RuntimeHelpers.GetUninitializedObject(typeof(MainSim.LeaderboardStartArgs));
simulated.leaderboardType = LeaderboardType.simulation;
FarmerScala.Editor.EditorIntegration.Enabled = false;
Check((bool)Invoke(typeof(FarmerScala.RankedRunGuard), "Prefix", ranked)! && targets.All(method => (bool)Invoke(typeof(FarmerScala.SteamScoreGuard), "Prefix", method)!), "vanilla ranked and Steam routes remain available outside Scala mode");
FarmerScala.Editor.EditorIntegration.Enabled = true;
Check(!(bool)Invoke(typeof(FarmerScala.RankedRunGuard), "Prefix", ranked)! && (bool)Invoke(typeof(FarmerScala.RankedRunGuard), "Prefix", simulated)!, "Scala mode rejects ranked starts and permits ordinary simulation scheduling");
Check(targets.All(method => !(bool)Invoke(typeof(FarmerScala.SteamScoreGuard), "Prefix", method)!), "Scala mode blocks direct leaderboard UI, score and callback paths");
var dlcField = typeof(MainSim.LeaderboardStartArgs).GetField("dlcType")!;
foreach (object dlc in Enum.GetValues(dlcField.FieldType))
{
    dlcField.SetValue(ranked, dlc);
    foreach (LeaderboardType type in Enum.GetValues(typeof(LeaderboardType)))
    {
        ranked.leaderboardType = type;
        if (type == LeaderboardType.none || type == LeaderboardType.simulation) continue;
        Check(!(bool)Invoke(typeof(FarmerScala.RankedRunGuard), "Prefix", ranked)!, "Scala blocks " + dlc + " ranked mode " + type);
    }
}
ranked.leaderboardType = LeaderboardType.reset;

var noHostState = new object?[] { simulated, false };
Check(!(bool)Invoke(typeof(FarmerScala.SimulationParseScope), "Prefix", noHostState)! && !FarmerScala.SimulationParseScope.Active, "a simulation without an active Scala host is rejected");
FarmerScala.Plugin.Instance = new FarmerScala.Plugin { Host = new FarmerScala.TestHost { IsRunning = true } };
var scopeState = new object?[] { simulated, false };
Check((bool)Invoke(typeof(FarmerScala.SimulationParseScope), "Prefix", scopeState)! && (bool)scopeState[1]! && FarmerScala.SimulationParseScope.Active, "ordinary Scala simulation enters its isolated native parse scope");
Check(targets.All(method => (bool)Invoke(typeof(FarmerScala.SteamScoreGuard), "Prefix", method)! == (method.DeclaringType == typeof(LeaderboardManager))), "simulation scope permits its overlay and still forbids Steam and ranked UI paths");
var expectedError = new Exception("test finalizer");
Check(ReferenceEquals(Invoke(typeof(FarmerScala.SimulationParseScope), "Finalizer", expectedError, scopeState[1]), expectedError) && !FarmerScala.SimulationParseScope.Active, "simulation parse scope restores on exceptional exit");

var rankedState = (MainSim)RuntimeHelpers.GetUninitializedObject(typeof(MainSim));
var cancellationFlag = typeof(MainSim).GetField("leaderboardCancelled", flags)!;
if (cancellationFlag.FieldType != typeof(bool)) throw new Exception("Native ranked cancellation metadata changed.");
void SeedRankedBookkeeping()
{
    cancellationFlag.SetValue(rankedState, true);
    rankedState.totalLeaderboardTime = new Duration(123L);
    rankedState.numLeaderboardRuns = 4;
    rankedState.TimeFactor = 256.0;
}
bool BookkeepingUnchanged() => (bool)cancellationFlag.GetValue(rankedState)! && rankedState.numLeaderboardRuns == 4 && rankedState.totalLeaderboardTime.nanoseconds == 123L && rankedState.TimeFactor == 256.0;
FarmerScala.Editor.EditorIntegration.Enabled = false;
SeedRankedBookkeeping();
FarmerScala.RankedSaveLoadGuard.ResetRankedBookkeeping(rankedState, LeaderboardType.reset);
Check(BookkeepingUnchanged(), "ranked cleanup leaves literal vanilla-mode state untouched");
FarmerScala.Editor.EditorIntegration.Enabled = true;
SeedRankedBookkeeping();
FarmerScala.RankedSaveLoadGuard.ResetRankedBookkeeping(rankedState, LeaderboardType.simulation);
FarmerScala.RankedSaveLoadGuard.ResetRankedBookkeeping(rankedState, LeaderboardType.none);
Check(BookkeepingUnchanged(), "ranked cleanup leaves ordinary simulation and main-farm state untouched");
SeedRankedBookkeeping();
FarmerScala.RankedSaveLoadGuard.ResetRankedBookkeeping(rankedState, LeaderboardType.reset);
Check(!(bool)cancellationFlag.GetValue(rankedState)! && rankedState.numLeaderboardRuns == 0 && rankedState.totalLeaderboardTime.nanoseconds == 0L && rankedState.TimeFactor == 1.0,
    "discarded ranked state clears stale cancellation, averages and speedup before future vanilla runs");
FarmerScala.Editor.EditorIntegration.Enabled = false;
FarmerScala.Editor.EditorIntegration.SourceWriteBlocked = true;
Check(!(bool)Invoke(typeof(FarmerScala.RankedRunGuard), "Prefix", ranked)! && targets.All(method => !(bool)Invoke(typeof(FarmerScala.SteamScoreGuard), "Prefix", method)!),
    "unresolved or blocked save language cannot enter ranked or Steam score routes");
const BindingFlags popupFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
Check(new[] { "ShowPopup", "Close", "OnDisable" }.All(name => typeof(WarningPopup).GetMethod(name, popupFlags) != null) &&
    typeof(WarningPopup).GetField("window", popupFlags)?.FieldType == typeof(UnityEngine.UI.Image) &&
    typeof(WarningPopup).GetField("text", popupFlags)?.FieldType == typeof(TMPro.TextMeshProUGUI) &&
    typeof(WarningPopup).GetField("buttons", popupFlags)?.FieldType == typeof(UnityEngine.Transform) &&
    typeof(ColoredButton).GetField("nameText", popupFlags)?.FieldType == typeof(TMPro.TextMeshProUGUI) &&
    typeof(ColoredButton).GetField("buttonPanel", popupFlags)?.FieldType == typeof(UnityEngine.UI.Image),
    "popup Harmony targets and private layout field types match the installed game");
var memberMethod = typeof(CodeWindow).GetMethod("GetSubWordList", popupFlags);
Check(memberMethod?.ReturnType == typeof(List<string>) && memberMethod.GetParameters().Length == 1 &&
    memberMethod.GetParameters()[0].ParameterType == typeof(string), "native member-completion interception target matches supported signature");
var keyHandler = typeof(CodeWindow).GetProperty("CodeInput")!.PropertyType.GetMethod("KeyPressed", popupFlags);
Check(keyHandler != null && keyHandler.GetParameters().Length == 1 && keyHandler.GetParameters()[0].ParameterType.FullName == "UnityEngine.Event",
    "smart editing key interception matches native protected handler");
Check(new[] { "codeWinPrefab", "spawnWindowOffset" }.All(name => typeof(Workspace).GetField(name, popupFlags) != null) &&
    new[] { "fileNameText", "executeButton", "stepByStepButton", "prevText", "codeText", "errorMessage" }.All(name => typeof(CodeWindow).GetField(name, popupFlags) != null),
    "read-only source viewer uses verified native prefab and display fields");
Check(typeof(CodeWindow).GetMethod("UpdateTokens", popupFlags)?.ReturnType == typeof(bool) &&
    typeof(CodeWindow).GetMethod("SetToCodeState", popupFlags)?.ReturnType == typeof(object) &&
    new[] { "undoState", "undoChangeTime", "undoPreventNextChange" }.All(name => typeof(CodeWindow).GetField(name, popupFlags) != null),
    "source preservation and atomic editing hooks match native undo implementation");
Check(typeof(ErrorMessage).GetMethod("ShowError", popupFlags)?.GetParameters().Length == 4 &&
    typeof(ErrorMessage).GetField("errorText", popupFlags)?.FieldType == typeof(TMPro.TextMeshProUGUI),
    "diagnostic hover reuses the verified native error widget");
Console.WriteLine("NativeSession/leaderboard/popup/editor-metadata checks passed.");

public sealed class GatedQueue<T> : IProducerConsumerCollection<T>
{
    private readonly ConcurrentQueue<T> items = new ConcurrentQueue<T>();
    private readonly ManualResetEventSlim entered, released;
    public GatedQueue(ManualResetEventSlim entered, ManualResetEventSlim released)
    {
        this.entered = entered;
        this.released = released;
    }
    public void Seed(T item) => items.Enqueue(item);
    public bool TryAdd(T item)
    {
        entered.Set();
        if (!released.Wait(5000))
            throw new TimeoutException("The test insertion gate was not released.");
        items.Enqueue(item);
        return true;
    }
    public bool TryTake(out T item) => items.TryDequeue(out item!);
    public int Count => items.Count;
    public object SyncRoot => this;
    public bool IsSynchronized => false;
    public void CopyTo(T[] array, int index) => items.CopyTo(array, index);
    public void CopyTo(Array array, int index) => ((ICollection)items).CopyTo(array, index);
    public T[] ToArray() => items.ToArray();
    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
