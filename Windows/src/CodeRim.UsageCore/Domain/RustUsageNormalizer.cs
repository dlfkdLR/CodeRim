using System.Runtime.InteropServices;
using static CodeRim.Core.Domain.RustAccountingLibrary;

namespace CodeRim.Core.Domain;

internal static class RustUsageNormalizer
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawState(RawUsage HighWater, long At, long HasHighWater, long HasAt, long Quality);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawObservation(RawUsage Cumulative, RawUsage Last, long At, long HasCumulative, long HasLast);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawResult(RawState State, RawUsage Delta, long HasDelta, long Diagnostic, long Repaired);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct IncrementCounts(long Input, long Cached, long Output, long Written)
    {
        internal static IncrementCounts From(TokenUsage usage) => new(usage.InputTokens,
            usage.CachedInputTokens, usage.OutputTokens, usage.CacheWriteInputTokens.GetValueOrDefault());
    }

    static RustUsageNormalizer() => RustAccountingLibrary.EnsureResolverInstalled();

    // This leaf transition performs bounded fixed-width scalar arithmetic only. It cannot allocate,
    // block, call managed code, or retain an argument. Revisit this annotation if that contract changes.
    [SuppressGCTransition]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(BridgeName, EntryPoint = "cr_normalize_windows", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern RawResult Transition(RawState state, RawObservation observation);

    [SuppressGCTransition]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(BridgeName, EntryPoint = "cr_try_token_increment", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern IncrementCounts TryIncrement(IncrementCounts previous, IncrementCounts current, int bothWritesKnown);

    internal static UsageNormalizationResult Normalize(TokenObservation observation, in UsageNormalizationState state)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (state.CumulativeHighWaterMark is { } previous && observation.CumulativeUsage is { } current
            && (state.LastObservedAt is not { } lastAt || observation.OccurredAt >= lastAt))
        {
            var known = previous.CacheWriteInputTokens.HasValue && current.CacheWriteInputTokens.HasValue;
            var increment = TryIncrement(IncrementCounts.From(previous), IncrementCounts.From(current), known ? 1 : 0);
            if (increment.Input >= 0)
            {
                // For a valid row cached reads/writes are contained in input, so zero input
                // and zero output are sufficient to identify a row that must be omitted.
                TokenUsage? delta = increment.Input == 0 && increment.Output == 0 ? null
                    : new TokenUsage(increment.Input, increment.Cached, increment.Output, known ? increment.Written : null);
                return new UsageNormalizationResult(delta,
                    new UsageNormalizationState(current, observation.OccurredAt, state.Quality), null);
            }
        }
        return NormalizeFull(observation, state);
    }

    private static UsageNormalizationResult NormalizeFull(TokenObservation observation, in UsageNormalizationState state)
    {
        var prior = new RawState(RawUsage.From(state.CumulativeHighWaterMark), state.LastObservedAt?.UtcTicks ?? 0,
            state.CumulativeHighWaterMark.HasValue ? 1 : 0, state.LastObservedAt.HasValue ? 1 : 0, (long)state.Quality);
        var sample = new RawObservation(RawUsage.From(observation.CumulativeUsage), RawUsage.From(observation.LastUsage),
            observation.OccurredAt.UtcTicks, observation.CumulativeUsage.HasValue ? 1 : 0, observation.LastUsage.HasValue ? 1 : 0);
        var result = Transition(prior, sample);

        // Rejected observations retain the prior offset even when both instants have identical UTC ticks.
        DateTimeOffset? lastObserved = null;
        if (result.State.HasAt != 0)
        {
            var offset = result.Diagnostic is 1 or 2 ? state.LastObservedAt!.Value.Offset : observation.OccurredAt.Offset;
            lastObserved = new DateTimeOffset(result.State.At, TimeSpan.Zero).ToOffset(offset);
        }
        var quality = (DataQuality)result.State.Quality;
        // An opaque quality from a newer caller survives a transition that retained the input quality.
        if (quality == DataQuality.Error && !Enum.IsDefined(state.Quality)) quality = state.Quality;
        var next = new UsageNormalizationState(result.State.HasHighWater == 0 ? null : result.State.HighWater.ToUsage(),
            lastObserved, quality);
        return new UsageNormalizationResult(result.HasDelta == 0 ? null : result.Delta.ToUsage(), next, result.Diagnostic switch
        {
            0 => null,
            1 => "out-of-order token snapshot ignored",
            2 => "missing cumulative token usage",
            3 => "initial cumulative baseline is unresolved",
            4 => "cumulative counter restarted",
            5 => "cumulative token usage decreased or interleaved",
            6 => "inconsistent token delta clamped",
            7 => "inconsistent token usage clamped",
            8 => "cumulative token difference overflowed; baseline reset",
            _ => throw new InvalidDataException("Unrecognized Rust accounting diagnostic.")
        }) { Repaired = result.Repaired != 0 };
    }
}
