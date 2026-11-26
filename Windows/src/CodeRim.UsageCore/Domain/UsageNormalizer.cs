namespace CodeRim.Core.Domain;

public readonly record struct UsageNormalizationState(
    TokenUsage? CumulativeHighWaterMark,
    DateTimeOffset? LastObservedAt,
    DataQuality Quality)
{
    public static UsageNormalizationState Empty { get; } = new(null, null, DataQuality.Exact);
}

/// <param name="Repaired">The delta is the closest valid value to an inconsistent one. Stored with the
/// event, so the period stays partial after the source log that showed it is gone.</param>
public readonly record struct UsageNormalizationResult(
    TokenUsage? Delta,
    UsageNormalizationState State,
    string? Diagnostic)
{
    public bool Repaired { get; init; }
}

public static class UsageNormalizer
{
    public static UsageNormalizationResult Normalize(TokenObservation observation, in UsageNormalizationState state)
#if CODERIM_RUST_ACCOUNTING
        => RustUsageNormalizer.Normalize(observation, state);
#else
        => ManagedUsageNormalizer.Normalize(observation, state);
#endif
}
