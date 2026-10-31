using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace FarmerScala.Game
{
    /// <summary>Feeds RPC commands into the game's existing execution scheduler.</summary>
    public sealed class NativeGameSession : IDisposable
    {
        private static readonly HashSet<string> LanguageFunctions = new HashSet<string>(StringComparer.Ordinal)
        { "range", "len", "min", "max", "abs", "str", "list", "set", "dict", "leaderboard_run" };
        private readonly CodeWindow window;
        private readonly Action<string> log;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly ConcurrentDictionary<int, Channel> channels = new ConcurrentDictionary<int, Channel>();
        private readonly ConcurrentDictionary<int, StateBinding> activeStates = new ConcurrentDictionary<int, StateBinding>();
        private readonly ConcurrentDictionary<string, PyDroneHandle> handles = new ConcurrentDictionary<string, PyDroneHandle>();
        private readonly ConcurrentDictionary<PyDroneHandle, string> handleIds = new ConcurrentDictionary<PyDroneHandle, string>(new DroneHandleReferenceComparer());
        private readonly Dictionary<string, Dictionary<string, IPyObject>> constants = new Dictionary<string, Dictionary<string, IPyObject>>(StringComparer.Ordinal);
        private readonly HashSet<string> api;
        private const int ExternalWorkerWait = int.MaxValue;
        private readonly ConcurrentDictionary<Execution, byte> workerPausedExecutions = new ConcurrentDictionary<Execution, byte>();
        private string? pendingSimulationEntry;
        private JToken? pendingSimulationGlobals;
        private string? fatalFault;
        private int nextConnectionId;
        private int nextHandleId;

        public NativeGameSession(CodeWindow window, Action<string> log)
        {
            this.window = window ?? throw new ArgumentNullException(nameof(window));
            this.log = log ?? (_ => { });
            api = new HashSet<string>(BuiltinFunctions.Functions.Keys.Where(n => !LanguageFunctions.Contains(n)), StringComparer.Ordinal);
            RootNode = new CommandNode(this, window);
            PopulateConstants();
        }

        public Node RootNode
        {
            get;
        }
        /// <summary>First argument is a unique logical worker connection ID, not the native drone slot.</summary>
        public event Action<int, JObject>? TaskStarting;
        public event Action<int>? DroneRemoved;
        public JObject? FaultLocation { get; private set; }
        public bool HasLiveExecution => activeStates.Values.Any(b => b.Execution.sim.IsExecuting());

        public Node CreateSimulationNode(CodeWindow simulationWindow)
        {
            string entry = pendingSimulationEntry ?? throw new InvalidOperationException("No Scala simulation was requested.");
            return new ProgramNode(this, simulationWindow, entry, pendingSimulationGlobals ?? new JObject { ["$"] = "dict", ["items"] = new JArray() });
        }

        public JObject BuildManifest()
        {
            var enums = new JObject();
            foreach (var family in constants)
                enums[family.Key] = new JArray(family.Value.Keys.OrderBy(n => n, StringComparer.Ordinal));
            return new JObject { ["api"] = new JArray(api.OrderBy(n => n, StringComparer.Ordinal)), ["enums"] = enums };
        }

        public void Enqueue(int droneId, JObject request, Action<JToken> success, Action<string, string> fail)
        {
            if (cancellation.IsCancellationRequested)
            {
                fail("Cancelled", "The run has stopped.");
                return;
            }
            var op = (string?)request["op"];
            if (op == null || !api.Contains(op))
            {
                fail("UnsupportedOperation", "This game operation is unavailable: " + op);
                return;
            }
            var kwargs = request["kwargs"] as JObject;
            if (kwargs != null && kwargs.Count != 0)
            {
                fail("InvalidArguments", "Game keyword arguments must be bound by the Scala API wrapper.");
                return;
            }
            var channel = channels.GetOrAdd(droneId, _ => new Channel());
            var command = new Command(request, success, fail);
            if (channel.Removed)
            {
                command.Fail("DroneRemoved", "This drone was removed by the game.");
                return;
            }
            if (!channel.Requests.TryAdd(command))
                command.Fail("QueueFull", "Too many pending game requests.");
            // Cancel may have drained the channel after the first token check but
            // before TryAdd. The command must still complete its host callback.
            else if (cancellation.IsCancellationRequested)
                command.Fail("Cancelled", "The run has stopped.");
            else if (channel.Removed)
            {
                command.Fail("DroneRemoved", "This drone was removed by the game.");
                Drain(channel, "DroneRemoved", "This drone was removed by the game.");
            }
            else
                Wake(droneId);
        }

        public void CompleteDrone(int droneId, JToken? returnValue = null)
        {
            if (cancellation.IsCancellationRequested)
                return;
            var channel = channels.GetOrAdd(droneId, _ => new Channel());
            if (channel.Removed)
                return;
            channel.Requests.TryAdd(new Command(returnValue));
            if (channel.Removed)
            {
                Drain(channel, "DroneRemoved", "This drone was removed by the game.");
                return;
            }
            Wake(droneId);
        }

        /// <summary>Called by the host's main-thread Update. It never waits on lockExecution.</summary>
        public string? PollFault()
        {
            if (fatalFault != null)
                return fatalFault;
            foreach (var pair in activeStates.ToArray())
            {
                var binding = pair.Value;
                var error = binding.State.currentExecuteException;
                if (error != null)
                {
                    FaultLocation = binding.Channel.Pending?.Request?["location"]?.DeepClone() as JObject;
                    string message = Localize(error.Message);
                    binding.Channel.Pending?.Fail("GameError", message);
                    binding.Channel.Pending = null;
                    return message;
                }
                // clear() may remove a state and move its caller to native slot zero.
                if (!binding.Execution.States.Contains(binding.State) && activeStates.TryRemove(pair.Key, out _))
                {
                    binding.Channel.Removed = true;
                    // Revoke the removed worker's authority before an error reply
                    // can make it report a fault or send another game request.
                    try
                    {
                        DroneRemoved?.Invoke(pair.Key);
                    }
                    catch (Exception ex) { log(ex.ToString()); }
                    binding.Channel.Pending?.Fail("DroneRemoved", "This drone was removed by the game.");
                    binding.Channel.Pending = null;
                    Drain(binding.Channel, "DroneRemoved", "This drone was removed by the game.");
                }
            }
            return null;
        }

        public void Cancel()
        {
            if (cancellation.IsCancellationRequested)
                return;
            cancellation.Cancel(); // Must happen before the host calls MainSim.StopMainExecution.
            foreach (var channel in channels.Values)
            {
                channel.Pending?.Fail("Cancelled", "The run has stopped.");
                channel.Pending = null;
                Drain(channel, "Cancelled", "The run has stopped.");
            }
        }

        public void Dispose()
        {
            Cancel();
        }

        private static void Drain(Channel channel, string code, string message)
        {
            while (channel.Requests.TryTake(out var queued))
                queued.Fail(code, message);
        }

        private void Wake(int connectionId)
        {
            if (!activeStates.TryGetValue(connectionId, out var binding))
                return;
            lock (binding.Execution.lockExecution)
            {
                if (binding.State.awaitedDroneId == ExternalWorkerWait)
                {
                    binding.State.awaitedDroneId = -1;
                    binding.State.OpCount = Math.Max(binding.State.OpCount, binding.Execution.GlobalOpCount);
                }
                if (workerPausedExecutions.TryRemove(binding.Execution, out _))
                    binding.Execution.sim.Paused = false;
            }
        }

        private IEnumerable<double> ExecuteCommands(Node node, int connectionId, ProgramState state, Execution execution)
        {
            var channel = channels.GetOrAdd(connectionId, _ => new Channel());
            activeStates[connectionId] = new StateBinding(state, execution, channel);
            state.CurrentExecutingNode = node;
            while (!cancellation.IsCancellationRequested)
            {
                if (!channel.Requests.TryTake(out var command))
                {
                    state.awaitedDroneId = ExternalWorkerWait;
                    // Close the race where a socket request arrived just before the sentinel was installed.
                    if (channel.Requests.Count != 0)
                    {
                        state.awaitedDroneId = -1;
                        continue;
                    }
                    if (execution.States.All(s => s == null || s.awaitedDroneId >= 0))
                    {
                        // Keep one zero-progress state runnable: native Execution's all-awaited
                        // fallback advances to its target time even though no action occurred.
                        state.awaitedDroneId = -1;
                        if (!execution.sim.Paused)
                        {
                            workerPausedExecutions[execution] = 0;
                            execution.sim.Paused = true;
                        }
                    }
                    yield return 0;
                    continue;
                }
                if (cancellation.IsCancellationRequested)
                {
                    command.Fail("Cancelled", "The run has stopped.");
                    yield break;
                }
                if (command.IsTerminal)
                {
                    state.ReturnValue = new ScalaResult(command.Result ?? JValue.CreateNull());
                    yield break;
                }
                channel.Pending = command;
                // Publish Pending before checking again: Cancel can race the
                // dequeue, and neither its drain nor its Pending read may see us.
                if (cancellation.IsCancellationRequested)
                {
                    command.Fail("Cancelled", "The run has stopped.");
                    channel.Pending = null;
                    yield break;
                }
                if (!StartCommand(command, state, execution, node))
                {
                    channel.Pending = null;
                    continue;
                }
                // A native side effect is applied by Execution between this yield and resumption.
                // Its own op accounting, failure cost, unlock checks and Power consumption remain authoritative.
                yield return 0;
                if (cancellation.IsCancellationRequested)
                    yield break;
                try
                {
                    command.Succeed(Encode(state.ReturnValue));
                }
                catch (Exception ex) { command.Fail("GameResultError", ex.Message); }
                channel.Pending = null;
            }
        }

        private bool StartCommand(Command command, ProgramState state, Execution execution, Node node)
        {
            try
            {
                string op = command.Request!.Value<string>("op") ?? throw new ArgumentException("Game operation missing.");
                execution.sim.farm.AssertUnlocked(op);
                var values = (command.Request["args"] as JArray ?? new JArray()).Select(Decode).ToList();
                if (op == "simulate")
                {
                    pendingSimulationEntry = (string?)command.Request["args"]?[0];
                    pendingSimulationGlobals = command.Request["args"]?[3]?.DeepClone();
                }
                state.ReturnValue = PyNone.Instance;
                state.CurrentExecutingNode = node;
                state.OpCount += BuiltinFunctions.Functions[op].binding(values, execution.sim, execution, state.DroneId);
                return true;
            }
            catch (Exception ex)
            {
                command.Fail("GameError", Localize(ex.Message));
                return false;
            }
        }

        private static string Localize(string value)
        {
            try
            {
                return Localizer.Localize(value);
            }
            catch { return value; }
        }

        private void PopulateConstants()
        {
            var directions = new Dictionary<string, IPyObject>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in new[] { "North", "East", "South", "West" })
                directions[name] = Scope.EvaluateConstant(name);
            constants["Direction"] = directions;
            foreach (string family in new[] { "Entities", "Grounds", "Items", "Unlocks", "Hats" })
            {
                var values = new Dictionary<string, IPyObject>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in (PyConstBag)Scope.EvaluateConstant(family))
                {
                    string symbolic = value.ToString() ?? throw new InvalidOperationException("Game constant has no symbolic name.");
                    int dot = symbolic.IndexOf('.');
                    values[dot >= 0 ? symbolic.Substring(dot + 1) : symbolic] = value;
                }
                constants[family] = values;
            }
        }

        private IPyObject Decode(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return PyNone.Instance;
            if (token.Type == JTokenType.Boolean)
                return new PyBool((bool)token);
            if (token.Type == JTokenType.String)
                return new PyString(token.Value<string>() ?? string.Empty);
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                double number = (double)token;
                if (double.IsNaN(number) || double.IsInfinity(number))
                    throw new ArgumentException("Non-finite game number.");
                return new PyNumber(number);
            }
            if (token is JArray list)
                return new PyList(list.Select(Decode).ToList());
            if (!(token is JObject tagged))
                throw new ArgumentException("Unsupported game argument.");
            switch ((string?)tagged["$"])
            {
                case "enum":
                    string family = tagged.Value<string>("family") ?? throw new ArgumentException("Enum family missing.");
                    string name = tagged.Value<string>("name") ?? throw new ArgumentException("Enum name missing.");
                    if (!constants.TryGetValue(family, out var members) || !members.TryGetValue(name, out var value))
                        throw new ArgumentException("Unknown game constant " + family + "." + name);
                    return value;
                case "tuple":
                    return new PyTuple(TaggedItems(tagged).Select(Decode).ToList());
                case "dict":
                    var dict = new Dictionary<IPyObject, PyObjectBox>();
                    foreach (JArray pair in TaggedItems(tagged))
                        dict.Add(Decode(pair[0]), new PyObjectBox(Decode(pair[1])));
                    return new PyDict(dict);
                case "set":
                    return new PySet(new HashSet<IPyObject>(TaggedItems(tagged).Select(Decode)));
                case "drone":
                    if (!handles.TryGetValue(tagged.Value<string>("id") ?? "", out var handle))
                        throw new ArgumentException("Unknown or stale drone handle.");
                    return handle;
                case "task":
                    return new PyFunction("scala_task", new TaskNode(this, window, tagged), null);
                case "scala":
                    return new ScalaResult(tagged);
                default:
                    throw new ArgumentException("Unknown game argument tag.");
            }
        }

        private static JArray TaggedItems(JObject tagged) => tagged["items"] as JArray ?? throw new ArgumentException("Tagged items must be an array.");

        private JToken Encode(IPyObject? value)
        {
            if (value == null || value is PyNone)
                return JValue.CreateNull();
            if (value is ScalaResult opaque)
                return opaque.Value.DeepClone();
            if (value is PyBool boolean)
                return new JValue(boolean.num != 0);
            if (value is PyNumber number)
            {
                double numeric = number.num;
                if (double.IsNaN(numeric) || double.IsInfinity(numeric))
                    throw new InvalidOperationException("The game returned a non-finite number.");
                // Native numbers share one double representation. Preserve exact
                // integral values as Scala ints so positions/sizes work in range/index.
                if (Math.Abs(numeric) <= 9007199254740992d && numeric == Math.Truncate(numeric))
                    return new JValue((long)numeric);
                return new JValue(numeric);
            }
            if (value is PyString text)
                return new JValue(text.str);
            if (value is PyGridDirection direction)
                return EnumValue("Direction", direction.dir.ToString());
            if (value is PyDroneHandle handle)
            {
                // Native generations restart for each simulated farm. Reference identity
                // keeps parent handles stable when a simulation creates the same generation.
                string key = handleIds.GetOrAdd(handle, _ => Interlocked.Increment(ref nextHandleId).ToString(System.Globalization.CultureInfo.InvariantCulture));
                handles[key] = handle;
                return new JObject { ["$"] = "drone", ["id"] = key };
            }
            if (value is PyTuple tuple)
                return new JObject { ["$"] = "tuple", ["items"] = new JArray(tuple.elements.Select(Encode)) };
            if (value is PyList list)
                return new JArray(list.list.Select(Encode));
            if (value is PySet set)
                return new JObject { ["$"] = "set", ["items"] = new JArray(set.set.Select(Encode)) };
            if (value is PyDict dict)
                return new JObject { ["$"] = "dict", ["items"] = new JArray(dict.dict.Select(p => new JArray(Encode(p.Key), Encode(p.Value.obj)))) };
            foreach (var family in constants)
                foreach (var constant in family.Value)
                    if (ReferenceEquals(constant.Value, value))
                        return EnumValue(family.Key, constant.Key);
            throw new InvalidOperationException("Unsupported native game result: " + value.GetType().FullName);
        }

        private static JObject EnumValue(string family, string name) => new JObject { ["$"] = "enum", ["family"] = family, ["name"] = name };

        private sealed class DroneHandleReferenceComparer : IEqualityComparer<PyDroneHandle>
        {
            public bool Equals(PyDroneHandle? left, PyDroneHandle? right) => ReferenceEquals(left, right);
            public int GetHashCode(PyDroneHandle handle) => RuntimeHelpers.GetHashCode(handle);
        }

        private sealed class Channel
        {
            public readonly BlockingCollection<Command> Requests = new BlockingCollection<Command>(64);
            public Command? Pending;
            public volatile bool Removed;
        }
        private sealed class StateBinding
        {
            public readonly ProgramState State;
            public readonly Execution Execution;
            public readonly Channel Channel;
            public StateBinding(ProgramState state, Execution execution, Channel channel)
            {
                State = state;
                Execution = execution;
                Channel = channel;
            }
        }
        private sealed class Command
        {
            public readonly JObject Request = null!;
            public readonly JToken? Result;
            public readonly bool IsTerminal;
            private readonly Action<JToken>? success;
            private readonly Action<string, string>? fail;
            private int completed;
            public Command(JObject request, Action<JToken> success, Action<string, string> fail)
            {
                Request = request;
                this.success = success;
                this.fail = fail;
            }
            public Command(JToken? result)
            {
                Result = result?.DeepClone();
                IsTerminal = true;
            }
            public void Succeed(JToken value)
            {
                if (Interlocked.Exchange(ref completed, 1) == 0)
                {
                    try
                    {
                        success?.Invoke(value);
                    }
                    catch { }
                }
            }
            public void Fail(string code, string message)
            {
                if (Interlocked.Exchange(ref completed, 1) == 0)
                {
                    try
                    {
                        fail?.Invoke(code, message);
                    }
                    catch { }
                }
            }
        }
        private sealed class CommandNode : Node
        {
            private readonly NativeGameSession owner;
            public CommandNode(NativeGameSession owner, CodeWindow window) : base(window, 0, 0) { this.owner = owner; }
            public override IEnumerable<double> Execute(ProgramState state, Execution execution, int depth) => owner.ExecuteCommands(this, 0, state, execution);
            public override Node DeepCopy(Dictionary<object, object> copies) => new CommandNode(owner, boxedParams.codeWindow);
        }
        private sealed class TaskNode : FunctionNode
        {
            private readonly NativeGameSession owner;
            private readonly JObject task;
            public TaskNode(NativeGameSession owner, CodeWindow window, JObject task) : base(new List<string>(), "scala_task", false, window, 0, 0)
            {
                this.owner = owner;
                this.task = (JObject)task.DeepClone();
                Vars = new HashSet<string>();
            }
            public override IEnumerable<double> Execute(ProgramState state, Execution execution, int depth)
            {
                int connection = Interlocked.Increment(ref owner.nextConnectionId);
                try
                {
                    owner.TaskStarting?.Invoke(connection, (JObject)task.DeepClone());
                }
                catch (Exception ex) { owner.fatalFault = "Could not start Scala drone: " + ex.Message; }
                foreach (var tick in owner.ExecuteCommands(this, connection, state, execution))
                    yield return tick;
            }
            public override Node DeepCopy(Dictionary<object, object> copies) => new TaskNode(owner, boxedParams.codeWindow, task);
        }
        private sealed class ProgramNode : Node
        {
            private readonly NativeGameSession owner;
            private readonly string entry;
            private readonly JToken globals;
            public ProgramNode(NativeGameSession owner, CodeWindow window, string entry, JToken globals) : base(window, 0, 0)
            {
                this.owner = owner;
                this.entry = entry;
                this.globals = globals.DeepClone();
            }
            public override IEnumerable<double> Execute(ProgramState state, Execution execution, int depth)
            {
                int connection = Interlocked.Increment(ref owner.nextConnectionId);
                var program = new JObject { ["$"] = "program", ["entry"] = entry, ["globals"] = globals.DeepClone() };
                try
                {
                    owner.TaskStarting?.Invoke(connection, program);
                }
                catch (Exception ex) { owner.fatalFault = "Could not start Scala simulation: " + ex.Message; }
                foreach (var tick in owner.ExecuteCommands(this, connection, state, execution))
                    yield return tick;
            }
            public override Node DeepCopy(Dictionary<object, object> copies) => new ProgramNode(owner, boxedParams.codeWindow, entry, globals);
        }
        private sealed class ScalaResult : IPyObject
        {
            public readonly JToken Value;
            public ScalaResult(JToken value)
            {
                Value = value.DeepClone();
            }
            public IPyObject DeepCopy(Dictionary<object, object> copies) => new ScalaResult(Value);
            public override string ToString() => "<Scala value>";
        }
    }
}
