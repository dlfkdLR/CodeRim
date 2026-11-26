using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Parsing;

namespace CodeRim.UsageCore.Tests;

public sealed class RustNormalizerDifferentialTests
{
    [Fact]
    public void MatchesManagedTransitionsIncludingOriginalOffsetAndTickPrecision()
    {
        var timestamp = new DateTimeOffset(2026, 11, 25, 12, 34, 56, TimeSpan.FromHours(9)).AddTicks(7);
        TokenUsage?[] counts =
        [
            null, TokenUsage.Zero, new(0, 0, 0), new(-1, -1, 0), new(1, 2, -1, 3),
            new(12, 5, 2, 0), new(12, 5, 2, null), new(12, 9, 5, 4), new(20, 6, 10, 3)
        ];
        DateTimeOffset?[] stateTimes = [null, timestamp];
        DateTimeOffset[] observationTimes =
        [
            timestamp.AddTicks(-1).ToOffset(TimeSpan.FromHours(-5)),
            timestamp.ToOffset(TimeSpan.FromHours(-5)),
            timestamp.AddTicks(1).ToOffset(TimeSpan.FromHours(-5))
        ];

        foreach (var quality in Enum.GetValues<DataQuality>().Append((DataQuality)42))
        foreach (var prior in counts)
        foreach (var stateTime in stateTimes)
        foreach (var sampleTime in observationTimes)
        foreach (var cumulative in counts)
        foreach (var last in counts)
        {
            var state = new UsageNormalizationState(prior, stateTime, quality);
            var sample = new TokenObservation(sampleTime, null, last, cumulative);
            var expected = ManagedUsageNormalizer.Normalize(sample, state);
            var actual = UsageNormalizer.Normalize(sample, state);
            Assert.Equal(expected, actual);
            // DateTimeOffset.Equals compares the instant and ignores the original offset.
            Assert.Equal(expected.State.LastObservedAt?.Offset, actual.State.LastObservedAt?.Offset);
        }
    }

    [Fact]
    public void RebaselinesCorruptOverflowWithoutPublishingUsage()
    {
        var corrupt = new UsageNormalizationState(new TokenUsage(-1, 0, 0), null, DataQuality.Exact);
        var cumulative = new TokenUsage(long.MaxValue, 0, 0);
        var sample = new TokenObservation(DateTimeOffset.UnixEpoch, null, null, cumulative);
        var legacy = ManagedUsageNormalizer.Normalize(sample, corrupt);
        var candidate = UsageNormalizer.Normalize(sample, corrupt);
        Assert.Null(legacy.Delta);
        Assert.Null(legacy.Diagnostic);
        Assert.Equal(DataQuality.Exact, legacy.State.Quality);
        Assert.Null(candidate.Delta);
        Assert.Equal("cumulative token difference overflowed; baseline reset", candidate.Diagnostic);
        Assert.Equal(new UsageNormalizationState(cumulative, sample.OccurredAt, DataQuality.Partial), candidate.State);
        Assert.False(candidate.Repaired);
    }

    [Fact]
    public void CodexImporterStoresRepairedDeltaAndIgnoresRepeatedSnapshot()
    {
        var decoder = new UsageLogSession("rollout-fixture.jsonl", 10, "codex", null, false);
        var at = DateTimeOffset.UnixEpoch;
        foreach (var (input, cached, output) in new[] { (10, 2, 2), (11, 7, 3), (11, 7, 3) })
        {
            var counters = new { input_tokens = input, cached_input_tokens = cached, output_tokens = output };
            var row = JsonSerializer.SerializeToUtf8Bytes(new
            {
                timestamp = at,
                type = "event_msg",
                payload = new { type = "token_count", info = new { total_token_usage = counters, last_token_usage = counters } }
            });
            Assert.True(decoder.Consume(row, oversized: false));
            at = at.AddSeconds(1);
        }

        var result = decoder.Complete();
        Assert.True(result.Partial);
        Assert.False(result.ResourceLimitReached);
        Assert.Collection(result.Events,
            first =>
            {
                Assert.Equal(new TokenUsage(10, 2, 2), first.Usage);
                Assert.False(first.Repaired);
            },
            second =>
            {
                Assert.Equal(new TokenUsage(1, 1, 1), second.Usage);
                Assert.True(second.Repaired);
            });
    }

    [Fact]
    public void NullObservationKeepsPublicArgumentFailure()
    {
        Assert.Throws<ArgumentNullException>("observation",
            () => UsageNormalizer.Normalize(null!, UsageNormalizationState.Empty));
    }
}
