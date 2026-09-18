using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using System.Globalization;

namespace CodeRim.Core.Tests;

public sealed class UsageNormalizerTests
{
    private readonly DateTimeOffset timestamp = DateTimeOffset.Parse(
        "2026-08-27T01:02:03Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public void TotalCountsCachedInputOnlyAsPartOfInput()
    {
        var usage = new TokenUsage(1_200, 800, 300);

        Assert.Equal(1_500, usage.TotalTokens);
    }

    [Fact]
    public void UsesCumulativeIncreaseAndIgnoresRepeatedSnapshot()
    {
        var first = UsageNormalizer.Normalize(Observation(new TokenUsage(100, 60, 20)), UsageNormalizationState.Empty);
        var repeated = UsageNormalizer.Normalize(Observation(new TokenUsage(100, 60, 20)), first.State);
        var increased = UsageNormalizer.Normalize(Observation(new TokenUsage(130, 80, 25)), repeated.State);

        Assert.Equal(new TokenUsage(100, 60, 20), first.Delta);
        Assert.Null(repeated.Delta);
        Assert.Equal(new TokenUsage(30, 20, 5), increased.Delta);
    }

    [Fact]
    public void DoesNotGuessAnAmbiguousInitialBaseline()
    {
        var observation = new TokenObservation(
            timestamp,
            1,
            new TokenUsage(100, 80, 10),
            new TokenUsage(500, 300, 50));

        var result = UsageNormalizer.Normalize(observation, UsageNormalizationState.Empty);

        Assert.Null(result.Delta);
        Assert.Equal(DataQuality.Partial, result.State.Quality);
    }

    [Fact]
    public void CountsAValidatedCounterRestartAsPartial()
    {
        var previous = new UsageNormalizationState(
            new TokenUsage(1_000, 700, 100),
            timestamp.AddMinutes(-1),
            DataQuality.Exact);
        var fresh = new TokenUsage(50, 20, 10);

        var result = UsageNormalizer.Normalize(
            new TokenObservation(timestamp, 2, fresh, fresh),
            previous);

        Assert.Equal(fresh, result.Delta);
        Assert.Equal(DataQuality.Partial, result.State.Quality);
    }


    [Fact]
    public void MissingOptionalCacheWriteDoesNotLoseOrReplayTokens()
    {
        var firstUsage = new TokenUsage(100, 0, 20, 40);
        var first = UsageNormalizer.Normalize(Observation(firstUsage), UsageNormalizationState.Empty);
        var repeated = UsageNormalizer.Normalize(Observation(new(100, 0, 20)), first.State);
        Assert.Null(repeated.Delta);
        var second = UsageNormalizer.Normalize(new(timestamp.AddSeconds(1), 2, new(100, 0, 20), new(200, 0, 40)), first.State);
        var third = UsageNormalizer.Normalize(new(timestamp.AddSeconds(2), 3, new(100, 0, 20), new(300, 0, 60)), second.State);
        Assert.Equal(360, first.Delta!.Value.TotalTokens + second.Delta!.Value.TotalTokens + third.Delta!.Value.TotalTokens);
        Assert.Null(second.Delta.Value.CacheWriteInputTokens);
        Assert.Equal(DataQuality.Exact, third.State.Quality);
    }
    private TokenObservation Observation(TokenUsage usage) =>
        new(timestamp, 1, usage, usage);
}
