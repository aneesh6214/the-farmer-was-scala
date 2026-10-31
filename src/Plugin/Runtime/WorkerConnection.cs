using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FarmerScala.Runtime;

internal sealed class WorkerConnection : IDisposable
{
    internal const int FrameLimit = 1024 * 1024;
    internal const int SendTimeoutMilliseconds = 2000;
    private readonly TcpClient client;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly object writeLock = new object();
    private int disposed;
    public WorkerConnection(TcpClient client)
    {
        this.client = client;
        client.NoDelay = true;
        // A paused/uncooperative Scala extension can prevent its receiver from
        // reading. Never let a large response block the native scheduler forever.
        client.SendTimeout = SendTimeoutMilliseconds;
        var stream = client.GetStream();
        reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, true);
        writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
    }
    public JObject? Read()
    {
        var line = new StringBuilder();
        while (true)
        {
            int c = reader.Read();
            if (c < 0)
            {
                if (line.Length == 0)
                    return null;
                throw new IOException("Truncated worker frame");
            }
            if (c == '\n')
                break;
            if (line.Length >= FrameLimit)
                throw new IOException("Worker frame exceeded limit");
            line.Append((char)c);
        }
        if (Encoding.UTF8.GetByteCount(line.ToString()) > FrameLimit)
            throw new IOException("Worker frame exceeded byte limit");
        return JObject.Parse(line.ToString(), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
    }
    public void Send(JObject value)
    {
        string frame = value.ToString(Formatting.None);
        if (Encoding.UTF8.GetByteCount(frame) > FrameLimit)
            throw new IOException("Host frame exceeded limit");
        lock (writeLock)
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(WorkerConnection));
            writer.WriteLine(frame);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        // Close first, without writeLock: the current writer may hold it while
        // blocked in the socket. Closing also wakes Serve's blocking reader.
        // These leave-open text wrappers own no separate native handles and must
        // not be disposed concurrently with their active read/write operations.
        try
        {
            client.Client?.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        client.Close();
    }
}
