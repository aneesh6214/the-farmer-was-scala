using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;

namespace FarmerScala.Worker;

/// <summary>Recreates a captured Scala.js closure in a fresh, isolated engine without replaying native actions.</summary>
public sealed class ReplayJournal
{
    // A task must fit inside the native channel's one MiB frame with JSON escaping overhead.
    public const int MaximumTaskBytes = 768 * 1024;
    public const int MaximumJournalBytes = 16 * 1024 * 1024;
    public const int MaximumEvents = 1_000_000;
    public const int MaximumStages = 256;
    private readonly JsonArray priorStages;
    private readonly JsonArray current = new();
    private JsonArray? replay;
    private int replayIndex;
    private int replayRepeatIndex;
    private int currentBytes;
    private int currentEvents;
    private string? lastEvent;
    private bool exhausted;
    public bool IsReplaying => replay is not null;
    public bool AtCheckpoint => replay is not null && replayIndex == replay.Count;

    public ReplayJournal(JsonArray? stages) => priorStages = stages ?? new();

    public static JsonArray? DecodeTask(JsonObject? task)
    {
        if (task is null)
            return null;
        if ((string?)task["runtime"] != "farmer-scala-replay-v2" || (string?)task["encoding"] != "gzip-base64url-json-v1")
            throw new IOException("Unsupported drone replay task format.");
        string encoded = (string?)task["journal"] ?? throw new IOException("Drone replay journal missing.");
        if (encoded.Length > MaximumTaskBytes)
            throw new IOException("Compressed drone replay journal is too large.");
        string padded = encoded.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        using var compressed = new MemoryStream(Convert.FromBase64String(padded));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = gzip.Read(buffer)) != 0)
        {
            if (decoded.Length + read > MaximumJournalBytes)
                throw new IOException("Decompressed drone replay journal exceeds the memory budget.");
            decoded.Write(buffer, 0, read);
        }
        JsonArray stages = JsonNode.Parse(decoded.ToArray()) as JsonArray ?? throw new IOException("Invalid replay journal.");
        if (stages.Count > MaximumStages)
            throw new IOException("Drone replay exceeds the nesting budget.");
        if ((int?)task["stageCount"] != stages.Count)
            throw new IOException("Drone replay stage count mismatch.");
        long events = 0;
        foreach (var stage in stages)
            foreach (var item in stage as JsonArray ?? throw new IOException("Invalid replay stage."))
            {
                int count = (int?)item?["repeat"] ?? 1;
                if (count < 1)
                    throw new IOException("Invalid replay event repetition count.");
                events += count;
                if (events > MaximumEvents)
                    throw new IOException("Drone replay exceeds the event budget.");
            }
        return stages;
    }

    public void BeginStage(int index)
    {
        replay = priorStages[index] as JsonArray ?? throw new IOException("Invalid drone replay stage.");
        replayIndex = 0;
        replayRepeatIndex = 0;
    }
    public void EndReplay()
    {
        replay = null;
        replayIndex = 0;
    }
    public int StageCount => priorStages.Count;

    public JsonObject? Replay(string kind, string? op = null, JsonNode? args = null)
    {
        if (replay is null)
            return null;
        if (replayIndex >= replay.Count)
            throw new ReplayMismatchException("Expected the captured spawn point, but the program requested " + (op ?? kind) + ".");
        var item = replay[replayIndex] as JsonObject ?? throw new IOException("Invalid drone replay event.");
        if ((string?)item["kind"] != kind || (string?)item["op"] != op || !JsonNode.DeepEquals(item["args"], args))
            throw new ReplayMismatchException("The program diverged while reconstructing a drone. No replayed game actions were executed.");
        replayRepeatIndex++;
        if (replayRepeatIndex >= ((int?)item["repeat"] ?? 1))
        {
            replayRepeatIndex = 0;
            replayIndex++;
        }
        return item;
    }

    public void Record(JsonObject value)
    {
        if (IsReplaying)
            throw new InvalidOperationException("Cannot record during replay.");
        if (exhausted)
            return;
        string serialized = value.ToJsonString();
        int bytes = Encoding.UTF8.GetByteCount(serialized);
        currentEvents++;
        bool repeat = serialized == lastEvent && current.Count != 0;
        if (currentEvents > MaximumEvents || (!repeat && currentBytes + bytes > MaximumJournalBytes))
        {
            exhausted = true;
            current.Clear();
            return;
        }
        if (repeat)
        {
            var prior = current[^1]!.AsObject();
            prior["repeat"] = ((int?)prior["repeat"] ?? 1) + 1;
            return;
        }
        current.Add(value.DeepClone());
        currentBytes += bytes;
        lastEvent = serialized;
    }

    public JsonObject Capture(string digest, string entry)
    {
        if (exhausted)
            throw new ReplayCapacityException("This drone cannot be captured after a very long or large action history. Start a new Play run or spawn workers earlier. The current runtime supports up to one million replay events and 16 MiB of journal data.");
        int stageCount = priorStages.Count + 1;
        if (stageCount > MaximumStages)
            throw new ReplayCapacityException("This drone exceeds 256 nested replay checkpoints. Start a new Play run before continuing the chain.");
        long events = currentEvents;
        foreach (var stage in priorStages)
            foreach (var item in stage!.AsArray())
                events += (int?)item?["repeat"] ?? 1;
        if (events > MaximumEvents)
            throw new ReplayCapacityException("This nested drone exceeds one million replay events. Start a new Play run or spawn workers earlier.");
        using var journalBytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(journalBytes))
        {
            writer.WriteStartArray();
            foreach (var stage in priorStages)
                stage!.WriteTo(writer);
            current.WriteTo(writer);
            writer.WriteEndArray();
        }
        byte[] raw = journalBytes.ToArray();
        if (raw.Length > MaximumJournalBytes)
            throw new ReplayCapacityException("This nested drone exceeds the 16 MiB replay journal memory budget.");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(raw);
        string encoded = Convert.ToBase64String(compressed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var task = new JsonObject
        {
            ["$"] = "task",
            ["runtime"] = "farmer-scala-replay-v2",
            ["artifactDigest"] = digest,
            ["entry"] = entry,
            ["encoding"] = "gzip-base64url-json-v1",
            ["journal"] = encoded,
            ["stageCount"] = stageCount,
            ["replayEvents"] = events
        };
        if (Encoding.UTF8.GetByteCount(task.ToJsonString()) > MaximumTaskBytes)
            throw new ReplayCapacityException("This drone's captured history exceeds the worker message budget. Start a new Play run or spawn workers earlier.");
        return task;
    }
}

public sealed class ReplayCheckpointException : Exception;
public sealed class ReplayMismatchException(string message) : Exception(message);
public sealed class ReplayCapacityException(string message) : Exception(message);
