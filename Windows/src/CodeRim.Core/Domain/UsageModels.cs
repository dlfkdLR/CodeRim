namespace CodeRim.Core.Domain;

public enum DataQuality
{
    Exact,
    Partial,
    Stale,
    Unavailable,
    Error
}

public enum WeekStart
{
    Sunday,
    Monday
}

public sealed record UsageSnapshot(
    TokenUsage Today,
    TokenUsage Week,
    TokenUsage Month,
    TokenUsage AllTime,
    DataQuality Quality,
    DateTimeOffset? UpdatedAt)
{
    // A failed refresh changes freshness, not the completeness of retained data.
    public bool RetainsPartialHistory { get; init; }

    public static UsageSnapshot Empty { get; } = new(
        TokenUsage.Zero,
        TokenUsage.Zero,
        TokenUsage.Zero,
        TokenUsage.Zero,
        DataQuality.Unavailable,
        null);
}

public sealed record SessionMetadata(
    string? Id,
    string? ForkedFromId,
    string? ParentThreadId,
    long? SubagentHistoryStartOrdinal,
    DateTimeOffset? OccurredAt)
{
    public bool InheritsHistory => ForkedFromId is not null
        || ParentThreadId is not null
        || SubagentHistoryStartOrdinal is not null;
}

public sealed record TaskStartedMetadata(
    DateTimeOffset OccurredAt,
    DateTimeOffset? StartedAt,
    long? Ordinal);

public sealed record TokenObservation(
    DateTimeOffset OccurredAt,
    long? Ordinal,
    TokenUsage? LastUsage,
    TokenUsage? CumulativeUsage);

public enum PricingContext { Standard, HighContext }

public sealed record UsageEvent(
    string EventKey,
    DateTimeOffset OccurredAt,
    TokenUsage Usage,
    string Model = "unknown",
    string Project = "Unknown project",
    string SessionId = "unknown",
    string Provider = "codex",
    string ProjectId = "unknown")
{
    // Pricing metadata stays in the local database, not exported event JSON.
    [System.Text.Json.Serialization.JsonIgnore]
    public PricingContext? PricingContext { get; init; }

    // Transient import evidence. Durable relationships live in session_links;
    // this is not a new field in exported numeric history.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ImportParentSessionId { get; init; }
}

public sealed record AttachmentObservation(string Id, DateTimeOffset OccurredAt, int Count);
public sealed record SessionDetails(string Id, string? ParentId, IReadOnlyList<AttachmentObservation> Attachments)
{
    public DateTimeOffset? StartedAt { get; init; }
    public string? ProjectName { get; init; }
    public DateTimeOffset? ProjectObservedAt { get; init; }
}

public sealed record ScanResult(
    UsageSnapshot Snapshot,
    int SourceCount,
    string StatusMessage,
    bool HasMoreWork)
{
    public IReadOnlyList<UsageEvent> Events { get; init; } = [];
    public IReadOnlyList<SessionDetails> Sessions { get; init; } = [];
}
