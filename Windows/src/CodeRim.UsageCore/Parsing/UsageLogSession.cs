using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Parsing;

/// Per-source accounting state; input is bounded bytes, never a filesystem path to open.
internal sealed class UsageLogSession
{
    private readonly int maximumEvents;
    private readonly string provider;
    private readonly byte[]? projectKey;
    private readonly bool requireCompleteSources;
    private readonly string sourceName;
    private readonly List<UsageEvent> events = [];
    private readonly List<AttachmentObservation> attachments = [];
    private string? parentSession;
    private bool partial, uncountedUsage, resourceLimitReached;
    private string sessionId;
    private UsageNormalizationState state = UsageNormalizationState.Empty;
    private bool inheritsHistory;
    private bool historyReplayComplete = true;
    private long? inheritedHistoryEndOrdinal;
    private DateTimeOffset? sessionStartedAt;
    private bool sawSessionMetadata;
    private string model = "unknown", project = "Unknown project", projectId = "unknown";
    private DateTimeOffset? projectObservedAt;
    private bool sawCompleteRecord;

    internal UsageLogSession(string path, int maximumEvents, string provider, byte[]? projectKey, bool requireCompleteSources)
    {
        this.maximumEvents = maximumEvents; this.provider = provider;
        this.projectKey = projectKey; this.requireCompleteSources = requireCompleteSources;
        sessionId = HashIdentifier(SessionIdentifierFromFilename(path) ?? path);
        sourceName = Path.GetFileNameWithoutExtension(path);
    }

    internal bool Consume(byte[]? bytes, bool oversized)
    {
        if (oversized)
        {
            partial = uncountedUsage = true;
            return true;
        }

        var line = bytes!;
        if (requireCompleteSources && line.AsSpan().IndexOfAnyExcept((byte)' ', (byte)'\t', (byte)'\r') < 0) return true;
        if (requireCompleteSources)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || JsonFields.Text(document.RootElement, "type") is null)
                { partial = uncountedUsage = true; return true; }
                sawCompleteRecord = true;
            }
            catch (System.Text.Json.JsonException) { partial = uncountedUsage = true; return true; }
        }
        if (provider == "claude")
        {
            if (ClaudeJsonlParser.Parse(line, projectKey, sourceName) is { } claudeEvent)
            {
                if (events.Count + attachments.Count >= maximumEvents) return ReachedLimit();
                events.Add(claudeEvent);
            }
            else if (requireCompleteSources)
            {
                using var document = System.Text.Json.JsonDocument.Parse(line);
                var root = document.RootElement; var message = JsonFields.Object(root, "message");
                if (JsonFields.Text(root, "type") == "assistant" && !JsonFields.Flag(root, "isApiErrorMessage")
                    && (JsonFields.Text(message, "model")?.StartsWith("claude-", StringComparison.Ordinal) == true
                        || JsonFields.Object(message, "usage").ValueKind != System.Text.Json.JsonValueKind.Undefined)) partial = uncountedUsage = true;
            }
            return true;
        }
        if (line.AsSpan().IndexOf("\"input_image\""u8) >= 0)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(line);
                var root = document.RootElement; var payload = JsonFields.Object(root, "payload");
                if (JsonFields.Text(root, "type") == "response_item" && JsonFields.Text(payload, "type") == "message"
                    && JsonFields.Text(payload, "role") == "user" && payload.TryGetProperty("content", out var content)
                    && content.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var images = content.EnumerateArray().Where(x => JsonFields.Text(x, "type") == "input_image").ToArray();
                    if (images.Length > 0 && DateTimeOffset.TryParse(JsonFields.Text(root, "timestamp"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var timestamp))
                    {
                        long? ordinal = root.TryGetProperty("ordinal", out var ordinalValue)
                            && ordinalValue.ValueKind == System.Text.Json.JsonValueKind.Number
                            && ordinalValue.TryGetInt64(out var number) ? number : null;
                        if (inheritsHistory && !historyReplayComplete)
                        {
                            if (inheritedHistoryEndOrdinal is not null && ordinal > inheritedHistoryEndOrdinal
                                || inheritedHistoryEndOrdinal is null && sessionStartedAt is not null && timestamp >= sessionStartedAt)
                                historyReplayComplete = true;
                        }
                        if (!inheritsHistory || historyReplayComplete)
                        {
                            if (events.Count + attachments.Count >= maximumEvents) return ReachedLimit();
                            // Stable across copied/reformatted logs. The fallback keeps
                            // only a digest of image observations, never image/text data.
                            var identityKey = ordinal?.ToString(CultureInfo.InvariantCulture)
                                ?? HashIdentifier(string.Join("|", images.Select(x =>
                                    ImageIdentity(x))));
                            attachments.Add(new AttachmentObservation(HashIdentifier($"images|{sessionId}|{timestamp:O}|{identityKey}"), timestamp, images.Length));
                        }
                    }
                    else if (images.Length > 0) partial = uncountedUsage = true;
                }
            }
            catch (System.Text.Json.JsonException) { partial = uncountedUsage = true; }
        }
        if (line.AsSpan().IndexOf("\"turn_context\""u8) >= 0 || line.AsSpan().IndexOf("\"session_meta\""u8) >= 0)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(line);
                var root = document.RootElement; var type = JsonFields.Text(root, "type");
                if (type == "turn_context" || type == "session_meta" && !sawSessionMetadata)
                {
                    var payload = JsonFields.Object(root, "payload");
                    model = JsonFields.Text(payload, "model") ?? model;
                    if (JsonFields.Text(payload, "cwd") is { } cwd)
                    {
                        project = JsonFields.Folder(cwd); projectId = ClaudeJsonlParser.ProjectIdentity(cwd, projectKey);
                        projectObservedAt = DateTimeOffset.TryParse(JsonFields.Text(root, "timestamp"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var observed) ? observed : null;
                    }
                }
            }
            catch (System.Text.Json.JsonException) { partial = uncountedUsage = true; }
        }
        if (!requireCompleteSources && !ContainsRelevantMarker(line))
        {
            return true;
        }

        var parsed = CodexJsonlParser.Parse(line);
        switch (parsed.Kind)
        {
            case ParsedLineKind.SessionMetadata:
                if (sawSessionMetadata)
                {
                    if (inheritsHistory)
                    {
                        historyReplayComplete = false;
                    }
                    break;
                }

                sawSessionMetadata = true;
                var metadata = parsed.SessionMetadata!;
                if (metadata.Id is not null)
                {
                    sessionId = HashIdentifier(metadata.Id);
                }

                parentSession = metadata.ParentThreadId is null ? null : HashIdentifier(metadata.ParentThreadId);
                inheritsHistory = metadata.InheritsHistory;
                sessionStartedAt = metadata.OccurredAt;
                inheritedHistoryEndOrdinal = metadata.SubagentHistoryStartOrdinal;
                historyReplayComplete = !metadata.InheritsHistory;
                break;

            case ParsedLineKind.TaskStarted:
                if (!inheritsHistory || historyReplayComplete)
                {
                    break;
                }

                var task = parsed.TaskStarted!;
                if (inheritedHistoryEndOrdinal is not null
                    && task.Ordinal is not null
                    && task.Ordinal.Value > inheritedHistoryEndOrdinal.Value)
                {
                    historyReplayComplete = true;
                }
                else if (inheritedHistoryEndOrdinal is null
                         && sessionStartedAt is not null
                         && task.StartedAt is not null
                         && Math.Floor(task.StartedAt.Value.ToUnixTimeMilliseconds() / 1000d)
                         >= Math.Floor(sessionStartedAt.Value.ToUnixTimeMilliseconds() / 1000d))
                {
                    historyReplayComplete = true;
                }
                break;

            case ParsedLineKind.Token:
                var observation = parsed.Token!;
                var isInheritedReplay = false;
                if (inheritsHistory && !historyReplayComplete)
                {
                    if (inheritedHistoryEndOrdinal is not null
                        && observation.Ordinal is not null
                        && observation.Ordinal.Value > inheritedHistoryEndOrdinal.Value)
                    {
                        historyReplayComplete = true;
                    }
                    else
                    {
                        isInheritedReplay = true;
                    }
                }

                var priorCumulative = state.CumulativeHighWaterMark;
                var priorObservedAt = state.LastObservedAt;
                var normalized = UsageNormalizer.Normalize(observation, state);
                // A repeated cumulative snapshot can supply cache metadata
                // for the last reported event without adding any new tokens.
                if (!isInheritedReplay && normalized.Delta is null && events.Count > 0
                    && (priorObservedAt is null || observation.OccurredAt >= priorObservedAt)
                    && observation.CumulativeUsage is { } cumulative && priorCumulative is { } previous
                    && cumulative.InputTokens == previous.InputTokens && cumulative.CachedInputTokens == previous.CachedInputTokens
                    && cumulative.OutputTokens == previous.OutputTokens
                    && observation.LastUsage is { CacheWriteInputTokens: not null } enriched
                    && events[^1] is { } last && last.SessionId == sessionId && last.Usage.CacheWriteInputTokens is null
                    && last.Usage.InputTokens == enriched.InputTokens && last.Usage.CachedInputTokens == enriched.CachedInputTokens
                    && last.Usage.OutputTokens == enriched.OutputTokens && enriched.IsValid)
                    events[^1] = last with { Usage = enriched };
                state = normalized.State;
                partial |= state.Quality == DataQuality.Partial;
                uncountedUsage |= state.Quality == DataQuality.Partial && !normalized.Repaired && normalized.Diagnostic is not null;
                if (isInheritedReplay || normalized.Delta is not { } delta || delta.IsZero && !normalized.Repaired)
                {
                    break;
                }

                if (events.Count + attachments.Count >= maximumEvents)
                {
                    return ReachedLimit();
                }

                events.Add(new UsageEvent(
                    EventKey(sessionId, observation),
                    observation.OccurredAt,
                    delta, model, project, sessionId, "codex", projectId) {
                        Repaired = normalized.Repaired,
                        PricingContext = delta.InputTokens <= TokenUsage.HighContextInputThreshold ? PricingContext.Standard
                            : observation.LastUsage == delta ? PricingContext.HighContext : null });
                break;

            case ParsedLineKind.Malformed:
                partial = uncountedUsage = true;
                break;
        }
        return true;
    }

    private bool ReachedLimit() { resourceLimitReached = true; return false; }

    internal UsageDecodedSource Complete() => resourceLimitReached ? new([], true, true, null, false) :
        new(events, partial || requireCompleteSources && !sawCompleteRecord, false,
            provider == "codex" ? new SessionDetails(sessionId, parentSession, attachments) { StartedAt = sessionStartedAt,
                ProjectName = projectId == "unknown" ? null : project, ProjectObservedAt = projectObservedAt } : null,
            uncountedUsage || requireCompleteSources && !sawCompleteRecord);

    private static bool ContainsRelevantMarker(byte[] line)
    {
        var bytes = line.AsSpan();
        return bytes.IndexOf("\"token_count\""u8) >= 0
            || bytes.IndexOf("\"session_meta\""u8) >= 0
            || bytes.IndexOf("\"task_started\""u8) >= 0;
    }

    private static string EventKey(string sessionId, TokenObservation observation)
    {
        var material = string.Join(
            '|',
            "event-v2",
            $"session:{sessionId}",
            $"time:{observation.OccurredAt.UtcDateTime.Ticks}",
            $"ordinal:{observation.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? "none"}",
            $"last:{UsageIdentity(observation.LastUsage)}",
            $"cumulative:{UsageIdentity(observation.CumulativeUsage)}");
        return HashIdentifier(material);
    }

    private static string UsageIdentity(TokenUsage? usage) => usage is null
        ? "none"
        : $"{usage.Value.InputTokens},{usage.Value.CachedInputTokens},{usage.Value.OutputTokens}";

    internal static string? SessionIdentifierFromFilename(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (stem.Length < 36)
        {
            return null;
        }

        var suffix = stem[^36..];
        return Guid.TryParse(suffix, out var identifier)
            ? identifier.ToString("D")
            : null;
    }

    private static string ImageIdentity(System.Text.Json.JsonElement image)
    {
        foreach (var name in new[] { "image_url", "url" })
            if (image.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == System.Text.Json.JsonValueKind.String) return value.GetString()!;
                if (value.ValueKind == System.Text.Json.JsonValueKind.Object
                    && value.TryGetProperty("url", out var nested)
                    && nested.ValueKind == System.Text.Json.JsonValueKind.String) return nested.GetString()!;
            }
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream)) WriteCanonical(image, writer);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(System.Text.Json.JsonElement value, System.Text.Json.Utf8JsonWriter writer)
    {
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(property.Value, writer); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(item, writer);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static string HashIdentifier(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

}

internal sealed record UsageDecodedSource(IReadOnlyList<UsageEvent> Events, bool Partial, bool ResourceLimitReached,
    SessionDetails? Details, bool UncountedUsage);
