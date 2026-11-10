using System.Globalization;
using System.Text.Json;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Parsing;

public enum ParsedLineKind
{
    SessionMetadata,
    TaskStarted,
    Token,
    Ignored,
    Malformed
}

public sealed record ParsedLine(
    ParsedLineKind Kind,
    SessionMetadata? SessionMetadata = null,
    TaskStartedMetadata? TaskStarted = null,
    TokenObservation? Token = null,
    string? Diagnostic = null);

public static class CodexJsonlParser
{
    public const int MaximumLineBytes = 1_048_576;
    public const long MaximumTokenComponent = 1_000_000_000_000;
    private const int MaximumIdentifierLength = 256;

    public static ParsedLine Parse(ReadOnlyMemory<byte> line)
    {
        if (line.IsEmpty)
        {
            return new ParsedLine(ParsedLineKind.Ignored);
        }

        if (line.Length > MaximumLineBytes)
        {
            return Malformed("line exceeds the 1 MiB safety limit");
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return Malformed("missing event type");
            }

            return typeElement.GetString() switch
            {
                "session_meta" => ParseSessionMetadata(root),
                "event_msg" => ParseEventMessage(root),
                "turn_context" => new ParsedLine(ParsedLineKind.Ignored),
                _ => new ParsedLine(ParsedLineKind.Ignored)
            };
        }
        catch (JsonException)
        {
            return Malformed("invalid JSON");
        }
    }

    private static ParsedLine ParseSessionMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object)
        {
            return Malformed("session metadata is missing payload");
        }

        return new ParsedLine(
            ParsedLineKind.SessionMetadata,
            SessionMetadata: new SessionMetadata(
                SanitizedString(payload, "id", MaximumIdentifierLength),
                SanitizedString(payload, "forked_from_id", MaximumIdentifierLength),
                SanitizedString(payload, "parent_thread_id", MaximumIdentifierLength),
                TryInteger(payload, "subagent_history_start_ordinal"),
                TryTimestamp(root, "timestamp")));
    }

    private static ParsedLine ParseEventMessage(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("type", out var eventType)
            || eventType.ValueKind != JsonValueKind.String)
        {
            return new ParsedLine(ParsedLineKind.Ignored);
        }

        if (eventType.GetString() == "task_started")
        {
            var occurredAt = TryTimestamp(root, "timestamp");
            if (occurredAt is null)
            {
                return Malformed("task start is missing a valid timestamp");
            }

            return new ParsedLine(
                ParsedLineKind.TaskStarted,
                TaskStarted: new TaskStartedMetadata(
                    occurredAt.Value,
                    TryUnixTimestamp(payload, "started_at"),
                    TryInteger(root, "ordinal")));
        }

        if (eventType.GetString() != "token_count")
        {
            return new ParsedLine(ParsedLineKind.Ignored);
        }

        var timestamp = TryTimestamp(root, "timestamp");
        if (timestamp is null)
        {
            return Malformed("token event is missing a valid timestamp");
        }

        if (!payload.TryGetProperty("info", out var info)
            || info.ValueKind != JsonValueKind.Object)
        {
            return Malformed("token event is missing usage info");
        }

        var last = ParseUsage(info, "last_token_usage");
        var cumulative = ParseUsage(info, "total_token_usage");
        if (last is null && cumulative is null)
        {
            return Malformed("token event has no supported usage object");
        }

        return new ParsedLine(
            ParsedLineKind.Token,
            Token: new TokenObservation(
                timestamp.Value,
                TryInteger(root, "ordinal"),
                last,
                cumulative));
    }

    private static TokenUsage? ParseUsage(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !TryInteger(usage, "input_tokens", out var input)
            || !TryInteger(usage, "cached_input_tokens", out var cached)
            || !TryInteger(usage, "output_tokens", out var output))
        {
            return null;
        }

        var result = new TokenUsage(input, cached, output, TryInteger(usage, "cache_write_input_tokens"));
        return result.IsValid ? result : null;
    }

    private static long? TryInteger(JsonElement parent, string propertyName) =>
        TryInteger(parent, propertyName, out var value) ? value : null;

    private static bool TryInteger(JsonElement parent, string propertyName, out long value)
    {
        value = 0;
        if (!parent.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out value)
                && value >= 0
                && value <= MaximumTokenComponent;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            return text is { Length: <= 16 }
                && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value >= 0
                && value <= MaximumTokenComponent;
        }

        return false;
    }

    private static DateTimeOffset? TryTimestamp(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            element.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
    }

    private static DateTimeOffset? TryUnixTimestamp(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out var seconds)
            || !double.IsFinite(seconds)
            || seconds < 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? SanitizedString(JsonElement parent, string propertyName, int maximumLength)
    {
        if (!parent.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrEmpty(value)
            || value.Length > maximumLength
            || value.Contains('\0', StringComparison.Ordinal)
            ? null
            : value;
    }

    private static ParsedLine Malformed(string diagnostic) =>
        new(ParsedLineKind.Malformed, Diagnostic: diagnostic);
}
