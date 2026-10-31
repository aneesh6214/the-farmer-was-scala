using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FarmerScala.Runtime;

ProcessStartInfo Self(string mode)
{
    string executable = Environment.ProcessPath!;
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
    info.ArgumentList.Add(mode);
    return info;
}
if (args.Length > 0 && args[0] == "--job-grandchild") { Thread.Sleep(60000); return; }
if (args.Length > 0 && args[0] == "--job-child")
{
    using var descendant = Process.Start(Self("--job-grandchild"))!;
    Console.WriteLine(descendant.Id);
    Console.Out.Flush();
    Thread.Sleep(60000);
    return;
}

int checkCount = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checkCount++; Console.WriteLine("PASS " + message); }

(TcpListener Listener, TcpClient Peer, WorkerConnection Host) BackpressuredPair()
{
    var server = new TcpListener(IPAddress.Loopback, 0);
    server.Start();
    var peer = new TcpClient { ReceiveBufferSize = 1024 };
    peer.Connect((IPEndPoint)server.LocalEndpoint);
    var accepted = server.AcceptTcpClient();
    accepted.SendBufferSize = 1024;
    return (server, peer, new WorkerConnection(accepted));
}

Task<Exception?> BlockedWrite(WorkerConnection host)
{
    var packet = new Newtonsoft.Json.Linq.JObject { ["payload"] = new string('x', WorkerConnection.FrameLimit - 100) };
    return Task.Run<Exception?>(() =>
    {
        try
        {
            host.Send(packet);
            return null;
        }
        catch (Exception error) { return error; }
    });
}

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
using (var client = new TcpClient())
{
    client.Connect((IPEndPoint)listener.LocalEndpoint);
    using var host = new WorkerConnection(listener.AcceptTcpClient());
    using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    writer.WriteLine("{\"unicode\":\"café 🌱\",\"flag\":false,\"nothing\":null}");
    var data = host.Read()!;
    Check((string?)data["unicode"] == "café 🌱" && (bool?)data["flag"] == false && data["nothing"]!.Type == Newtonsoft.Json.Linq.JTokenType.Null, "UTF-8 Boolean and null roundtrip");
    host.Send(new Newtonsoft.Json.Linq.JObject { ["value"] = 42 });
    using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 4096, true);
    Check(reader.ReadLine() == "{\"value\":42}", "protocol output uses one independent JSON frame");
    writer.WriteLine("{\"x\":1,\"x\":2}");
    bool rejected = false;
    try
    {
        host.Read();
    }
    catch (Newtonsoft.Json.JsonException) { rejected = true; }
    Check(rejected, "duplicate protocol keys rejected");
}
listener.Stop();

var backpressure = BackpressuredPair();
using (backpressure.Peer)
using (backpressure.Host)
{
    try
    {
        var writing = BlockedWrite(backpressure.Host);
        Check(SpinWait.SpinUntil(() => backpressure.Peer.Available > 0, 1500) && !writing.IsCompleted,
            "non-reading peer backpressures a large host response");
        var closing = Stopwatch.StartNew();
        backpressure.Host.Dispose();
        closing.Stop();
        Check(closing.ElapsedMilliseconds < 500, "Dispose returns without waiting for the blocked writer lock");
        Exception? error = writing.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Check(error is IOException || error is SocketException || error is ObjectDisposedException,
            "closing connection interrupts the blocked writer");
        bool rejected = false;
        try
        {
            backpressure.Host.Send(new Newtonsoft.Json.Linq.JObject());
        }
        catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "a disposed connection rejects new writes");
    }
    finally { backpressure.Listener.Stop(); }
}

var timed = BackpressuredPair();
using (timed.Peer)
using (timed.Host)
{
    try
    {
        Exception? error = BlockedWrite(timed.Host).WaitAsync(TimeSpan.FromMilliseconds(WorkerConnection.SendTimeoutMilliseconds + 2500)).GetAwaiter().GetResult();
        Check(error is IOException || error is SocketException, "non-reading peer also has a bounded native send timeout");
    }
    finally { timed.Listener.Stop(); }
}

var blockedReader = BackpressuredPair();
using (blockedReader.Peer)
using (blockedReader.Host)
{
    try
    {
        using var entered = new ManualResetEventSlim();
        var reading = Task.Run(() =>
        {
            entered.Set();
            try
            {
                blockedReader.Host.Read();
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        });
        Check(entered.Wait(1500) && !reading.IsCompleted, "worker reader waits when the peer sends no frame");
        var closing = Stopwatch.StartNew();
        blockedReader.Host.Dispose();
        reading.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Check(closing.ElapsedMilliseconds < 1000, "closing connection promptly interrupts the blocked reader");
    }
    finally { blockedReader.Listener.Stop(); }
}

using var parent = new Process { StartInfo = Self("--job-child") };
Process? child = null;
using (var job = new ProcessJob())
{
    parent.Start();
    job.Add(parent);
    string? line = parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    child = Process.GetProcessById(int.Parse(line!));
    Check(!parent.HasExited && !child.HasExited, "parent and spawned child run inside job");
}
try
{
    Check(parent.WaitForExit(5000), "closing job terminates parent");
    Check(child!.WaitForExit(5000), "closing job terminates descendant");
}
finally
{
    if (!parent.HasExited)
        parent.Kill(true);
    if (child != null)
    {
        if (!child.HasExited)
            child.Kill(true);
        child.Dispose();
    }
}
Console.WriteLine(checkCount + " HostRuntime checks passed.");
