using System.Text.Json;
using CodeRim.Core.Parsing;

namespace CodeRim.Core.Services;

public sealed record SessionActivity(string Id, string Provider, string Name, string State, DateTimeOffset Since);
public static class ActivityReader
{
    public const int MaximumTailBytes = 8 * 1024 * 1024;
    public static SessionActivity? ReadCodex(string path, DateTimeOffset now) => Read(path, now, false);
    public static SessionActivity? ReadClaude(string path, DateTimeOffset now) => Read(path, now, true);
    private static SessionActivity? Read(string path, DateTimeOffset now, bool claude)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - MaximumTailBytes);
            stream.Position = start;
            var bytes = new byte[(int)(stream.Length - start)];
            var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            var end = Array.LastIndexOf(bytes, (byte)'\n', read - 1, read);
            while (end > 0)
            {
                var previous = Array.LastIndexOf(bytes, (byte)'\n', end - 1, end);
                var offset = previous + 1;
                if (previous < 0 && start > 0) break;
                bool running; DateTimeOffset time;
                if (end - offset <= CodexJsonlParser.MaximumLineBytes && (claude ? TryClaude(bytes.AsMemory(offset, end - offset), out running, out time) : TryCodex(bytes.AsMemory(offset, end - offset), out running, out time)))
                {
                    if (time > now.AddMinutes(1) || now - time > (running ? TimeSpan.FromHours(6) : TimeSpan.FromSeconds(90))) return null;
                    return new SessionActivity(ClaudeJsonlParser.Hash(Path.GetFileName(path)), claude ? "claude" : "codex", claude ? "Claude session" : "Codex task", running ? "busy" : "idle", time);
                }
                end = previous;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        return null;
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
