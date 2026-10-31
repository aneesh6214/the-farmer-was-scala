using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace FarmerScala.Worker;

/// <summary>Private authenticated newline JSON channel. No game or Unity objects enter the worker.</summary>
public sealed class WorkerTransport : IDisposable
{
    public const int MaxFrameBytes = 1024 * 1024;
    private readonly TcpClient client = new();
    private readonly object sendLock = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> pending = new();
    private readonly CancellationTokenSource closed = new();
    private Stream stream = Stream.Null;
    private long nextId;
    private readonly string run;
    private readonly string drone;
    public JsonObject Manifest { get; private set; } = new();
    public CancellationToken Cancellation => closed.Token;
    public event Action<string>? Control;

    public WorkerTransport(string run, string drone)
    {
        this.run = run;
        this.drone = drone;
    }

    public void Connect(int port, string token)
    {
        client.Connect(IPAddress.Loopback, port);
        client.NoDelay = true;
        client.ReceiveTimeout = 15000;
        stream = client.GetStream();
        Send(new JsonObject
        {
            ["kind"] = "hello",
            ["v"] = 1,
            ["token"] = token,
            ["run"] = run,
            ["drone"] = drone,
            ["pid"] = Environment.ProcessId
        });
        Manifest = Read() ?? throw new IOException("The game closed during the worker handshake.");
        Validate(Manifest);
        if ((string?)Manifest["kind"] != "hello" || (bool?)Manifest["ok"] != true)
            throw new IOException("The game rejected the worker handshake.");
        client.ReceiveTimeout = 0;
        _ = Task.Run(ReadLoop);
    }

    public JsonObject Request(string op, JsonArray args)
    {
        Cancellation.ThrowIfCancellationRequested();
        long id = Interlocked.Increment(ref nextId);
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, waiter))
            throw new IOException("Duplicate request identity.");
        try
        {
            Send(new JsonObject
            {
                ["kind"] = "request",
                ["v"] = 1,
                ["run"] = run,
                ["drone"] = drone,
                ["id"] = id,
                ["op"] = op,
                ["args"] = args.DeepClone()
            });
            return waiter.Task.WaitAsync(Cancellation).GetAwaiter().GetResult();
        }
        finally { pending.TryRemove(id, out _); }
    }

    public void Event(string name, JsonObject? fields = null)
    {
        var message = fields is null ? new JsonObject() : (JsonObject)fields.DeepClone();
        message["kind"] = "event";
        message["event"] = name;
        message["v"] = 1;
        message["run"] = run;
        message["drone"] = drone;
        Send(message);
    }

    private void ReadLoop()
    {
        try
        {
            while (!Cancellation.IsCancellationRequested)
            {
                var message = Read() ?? throw new IOException("The game closed the worker connection.");
                Validate(message);
                switch ((string?)message["kind"])
                {
                    case "pending":
                        break;
                    case "response":
                        long id = (long?)message["id"] ?? throw new IOException("Response ID missing.");
                        if (!pending.TryGetValue(id, out var waiter))
                            throw new IOException("Unknown response ID.");
                        waiter.TrySetResult(message);
                        break;
                    case "control":
                        Control?.Invoke((string?)message["action"] ?? "");
                        break;
                    default:
                        throw new IOException("Unexpected game message.");
                }
            }
        }
        catch (Exception e)
        {
            foreach (var waiter in pending.Values)
                waiter.TrySetException(e);
        }
        finally { closed.Cancel(); }
    }

    private void Validate(JsonObject message)
    {
        if ((int?)message["v"] != 1 || (string?)message["run"] != run || (string?)message["drone"] != drone)
            throw new IOException("Game message identity mismatch.");
    }

    private JsonObject? Read()
    {
        using var bytes = new MemoryStream();
        while (true)
        {
            int next = stream.ReadByte();
            if (next < 0)
            {
                if (bytes.Length == 0)
                    return null;
                throw new IOException("Truncated JSON message.");
            }
            if (next == '\n')
                break;
            if (bytes.Length >= MaxFrameBytes)
                throw new IOException("Game message exceeds the frame limit.");
            bytes.WriteByte((byte)next);
        }
        return JsonNode.Parse(new UTF8Encoding(false, true).GetString(bytes.ToArray())) as JsonObject
            ?? throw new IOException("Expected a JSON object.");
    }

    private void Send(JsonObject value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value.ToJsonString() + "\n");
        if (bytes.Length > MaxFrameBytes)
            throw new IOException("Worker message exceeds the frame limit.");
        lock (sendLock)
        {
            stream.Write(bytes);
            stream.Flush();
        }
    }

    public void Dispose()
    {
        closed.Cancel();
        client.Dispose();
        // The cancellation token remains valid until the JS engine has stopped.
    }
}
