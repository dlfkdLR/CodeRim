using System.Text.Json;
using System.Security.Cryptography;
using CodeRim.Core.Parsing;

namespace CodeRim.Core.Services;

public sealed record SessionActivity(string Id, string Provider, string Name, string State, DateTimeOffset Since)
{
    public string? CodexThreadId { get; init; }
    public int? ProcessId { get; init; }
    public DateTimeOffset? ProcessStartedAt { get; init; }
    public Uri? CodexThreadUri => Provider == "codex" && Guid.TryParseExact(CodexThreadId, "D", out var id)
        ? new Uri("codex://threads/" + id.ToString("D")) : null;
}
public static class ActivityReader
{
    // Kept for source compatibility. It no longer limits how long a running turn is visible.
    public const int MaximumTailBytes = 8 * 1024 * 1024;
    private sealed record Boundary(bool Running, DateTimeOffset Time);
    private sealed record Snapshot(long Length, DateTime Created, long Complete,
        byte[] Digest, Boundary? Event, UsageScanner.FileIdentity? Identity);
    private static readonly Dictionary<string, Snapshot> Cache = new(StringComparer.Ordinal);
    private static readonly object CacheLock = new();
    public static SessionActivity? ReadCodex(string path, DateTimeOffset now, CancellationToken cancellationToken = default) => Read(path, now, false, cancellationToken);
    public static SessionActivity? ReadClaude(string path, DateTimeOffset now, CancellationToken cancellationToken = default) => Read(path, now, true, cancellationToken);
    private static byte[] Bytes(FileStream stream, long position, int count)
    {
        stream.Position = position; var bytes = new byte[count]; stream.ReadExactly(bytes); return bytes;
    }
    private static SessionActivity? Read(string path, DateTimeOffset now, bool claude, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length; var identity = UsageScanner.FileIdentity.TryRead(stream.SafeFileHandle);
            var created = info.CreationTimeUtc;
            var key = (claude ? "claude:" : "codex:") + Path.GetFullPath(path);
            Snapshot? previous;
            lock (CacheLock) Cache.TryGetValue(key, out previous);
            // Verify all old bytes before reusing an append cursor: local tools can rewrite a
            // completed record and append in one update, even while preserving timestamps.
            var digests = Hashes(stream, length, Math.Min(previous?.Length ?? 0, length), cancellationToken);
            var reusable = previous is not null && length >= previous.Length && identity == previous.Identity
                && created == previous.Created && digests.Previous.AsSpan().SequenceEqual(previous.Digest);
            Boundary? boundary; long complete;
            if (reusable && previous!.Length == length) { boundary = previous.Event; complete = previous.Complete; }
            else
            {
                var result = Scan(stream, reusable ? previous!.Complete : 0, length, claude, cancellationToken);
                boundary = result.Event ?? (reusable ? previous!.Event : null);
                complete = result.Complete ?? (reusable ? previous!.Complete : 0);
            }
            // Allow concurrent append after this frozen prefix, but reject rewritten,
            // truncated or replaced input. No unbounded line is materialized.
            info.Refresh();
            if (stream.Length < length || !info.Exists || info.Length < length || info.CreationTimeUtc != created
                || UsageScanner.FileIdentity.TryRead(path) != identity
                || !(reusable && previous!.Length == length)
                    && !Hashes(stream, length, 0, cancellationToken).Full.AsSpan().SequenceEqual(digests.Full)) return null;
            lock (CacheLock)
            {
                if (Cache.Count >= 256 && !Cache.ContainsKey(key)) Cache.Clear();
                Cache[key] = new(length, created, complete, digests.Full, boundary, identity);
            }
            if (boundary is null || boundary.Time > now.AddMinutes(1)) return null;
            // Codex uses file freshness to bound orphaned starts, not the age of a long live turn.
            if (!claude && (now - info.LastWriteTimeUtc > TimeSpan.FromHours(6)
                || !boundary.Running && now - boundary.Time > TimeSpan.FromSeconds(90))) return null;
            return new SessionActivity(ClaudeJsonlParser.Hash(Path.GetFileName(path)), claude ? "claude" : "codex",
                claude ? "Claude session" : "Codex task", boundary.Running ? "busy" : "idle", boundary.Time)
            { CodexThreadId = !claude && Path.GetFileNameWithoutExtension(path) is { Length: >= 36 } name ? name[^36..] : null };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or OverflowException) { return null; }
    }
    private static (byte[] Full, byte[] Previous) Hashes(FileStream stream, long length, long previousLength, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; stream.Position = 0; long position = 0;
        byte[] previous = [];
        foreach (var limit in new[] { previousLength, length })
        {
            while (position < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, limit - position);
                stream.ReadExactly(buffer.AsSpan(0, count)); hash.AppendData(buffer, 0, count); position += count;
            }
            if (limit == previousLength) previous = hash.GetCurrentHash();
        }
        return (hash.GetHashAndReset(), previous);
    }
    private static (Boundary? Event, long? Complete) Scan(FileStream stream, long lower, long length, bool claude, CancellationToken cancellationToken)
    {
        long? lineEnd = null; long? complete = null;
        Boundary? Decode(long start, long end)
        {
            if (end - start > CodexJsonlParser.MaximumLineBytes || end <= start) return null;
            var bytes = Bytes(stream, start, (int)(end - start));
            bool running; DateTimeOffset time;
            if (!(claude ? TryClaude(bytes, out running, out time) : TryCodex(bytes, out running, out time))) return null;
            return new(running, time);
        }
        for (var position = length; position > lower;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = Math.Max(lower, position - 65536); var block = Bytes(stream, start, (int)(position - start));
            for (var i = block.Length - 1; i >= 0; i--)
            {
                if (block[i] != (byte)'\n') continue;
                var absolute = start + i;
                if (lineEnd is { } end && Decode(absolute + 1, end) is { } found) return (found, complete);
                complete ??= absolute + 1; lineEnd = absolute;
            }
            position = start;
        }
        return (lineEnd is { } finish ? Decode(lower, finish) : null, complete);
    }
    public static bool TryClaude(ReadOnlyMemory<byte> line, out bool running, out DateTimeOffset time)
    {
        running = false; time = default;
        if (line.Length > CodexJsonlParser.MaximumLineBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(line); var root = document.RootElement;
            if (root.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True) return false;
            var type = JsonFields.Text(root, "type"); var message = JsonFields.Object(root, "message");
            switch (type)
            {
                case "continued-in": break;
                case "system" when JsonFields.Text(root, "subtype") == "turn_duration": break;
                case "assistant": running = JsonFields.Text(message, "stop_reason") == "tool_use"; break;
                case "user":
                    running = true;
                    if (message.TryGetProperty("content", out var content))
                    {
                        const string interruption = "[Request interrupted by user";
                        if (content.ValueKind == JsonValueKind.String) running = !content.GetString()!.StartsWith(interruption, StringComparison.Ordinal);
                        else if (content.ValueKind == JsonValueKind.Array) running = !content.EnumerateArray().Any(x => JsonFields.Text(x, "text")?.StartsWith(interruption, StringComparison.Ordinal) == true);
                    }
                    break;
                default: return false;
            }
            return DateTimeOffset.TryParse(JsonFields.Text(root, "timestamp"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out time);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }
    public static bool TryCodex(ReadOnlyMemory<byte> line, out bool running, out DateTimeOffset time)
    {
        running = false; time = default;
        if (line.Length > CodexJsonlParser.MaximumLineBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var payload = JsonFields.Object(root, "payload");
            var type = JsonFields.Text(payload, "type");
            if (JsonFields.Text(root, "type") != "event_msg" || type is not ("task_started" or "task_complete" or "turn_aborted")) return false;
            running = type == "task_started";
            return DateTimeOffset.TryParse(JsonFields.Text(root, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out time);
        }
        catch (JsonException) { return false; }
    }
}
