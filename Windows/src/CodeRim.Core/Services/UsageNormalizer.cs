using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

public sealed record UsageNormalizationState(
    TokenUsage? CumulativeHighWaterMark,
    DateTimeOffset? LastObservedAt,
    DataQuality Quality)
{
    public static UsageNormalizationState Empty { get; } = new(null, null, DataQuality.Exact);
}

public sealed record UsageNormalizationResult(
    TokenUsage? Delta,
    UsageNormalizationState State,
    string? Diagnostic);

public static class UsageNormalizer
{
    public static UsageNormalizationResult Normalize(
        TokenObservation observation,
        UsageNormalizationState state)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(state);

        if (state.LastObservedAt is not null
            && observation.OccurredAt < state.LastObservedAt.Value)
        {
            return new UsageNormalizationResult(
                null,
                state,
                "out-of-order token snapshot ignored");
        }

        if (observation.CumulativeUsage is not { } cumulative)
        {
            return new UsageNormalizationResult(
                null,
                state with { Quality = DataQuality.Partial },
                "missing cumulative token usage");
        }

        if (state.CumulativeHighWaterMark is not { } previous)
        {
            if (observation.LastUsage != cumulative)
            {
                return new UsageNormalizationResult(
                    null,
                    new UsageNormalizationState(cumulative, observation.OccurredAt, DataQuality.Partial),
                    "initial cumulative baseline is unresolved");
            }

            return new UsageNormalizationResult(
                cumulative.IsZero ? null : cumulative,
                new UsageNormalizationState(cumulative, observation.OccurredAt, state.Quality),
                null);
        }

        if (cumulative.HasCounterDecrease(previous))
        {
            if (observation.LastUsage == cumulative)
            {
                return new UsageNormalizationResult(
                    cumulative.IsZero ? null : cumulative,
                    new UsageNormalizationState(cumulative, observation.OccurredAt, DataQuality.Partial),
                    "cumulative counter restarted");
            }

            return new UsageNormalizationResult(
                null,
                new UsageNormalizationState(
                    previous.ComponentWiseMaximum(cumulative),
                    observation.OccurredAt,
                    DataQuality.Partial),
                "cumulative token usage decreased or interleaved");
        }

        var delta = cumulative.SubtractFloorAtZero(previous);
        return new UsageNormalizationResult(
            delta.IsZero ? null : delta,
            new UsageNormalizationState(cumulative, observation.OccurredAt, state.Quality),
            null);
    }
}
