using CodeRim.Core.Domain;

namespace CodeRim.Core.Tests;

public sealed class ProviderFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("codex", 299, false, ReadingState.Ready)]
    [InlineData("codex", 300, false, ReadingState.Stale)]
    [InlineData("codex", 1, true, ReadingState.Ready)]
    [InlineData("claude", 300, false, ReadingState.Ready)]
    [InlineData("claude", 301, false, ReadingState.Ready)]
    [InlineData("claude", 900, false, ReadingState.Ready)]
    [InlineData("claude", 901, false, ReadingState.Stale)]
    [InlineData("claude", -300, false, ReadingState.Ready)]
    [InlineData("claude", -301, false, ReadingState.Stale)]
    [InlineData("claude", 299, true, ReadingState.Stale)]
    [InlineData("openrouter", 300, false, ReadingState.Ready)]
    [InlineData("openrouter", 301, false, ReadingState.Stale)]
    [InlineData("openrouter", -61, false, ReadingState.Stale)]
    [InlineData("openrouter", 1, true, ReadingState.Stale)]
    public void FreshnessUsesTheProvidersOwnClockPolicy(string provider, int ageSeconds, bool expired, ReadingState expected)
    {
        var reading = new ProviderReading(provider, ReadingState.Ready,
            [new("quota", "Quota", 20, expired ? Now : Now.AddHours(1), 300)], Now.AddSeconds(-ageSeconds));
        Assert.Equal(expected, reading.Evaluated(Now).State);
        Assert.Equal(ReadingState.Ready, reading.State);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("openrouter")]
    public void FreshnessDoesNotOverflowAtTheDateRangeBoundary(string provider)
    {
        var reading = new ProviderReading(provider, ReadingState.Ready, [], DateTimeOffset.MaxValue);
        Assert.Equal(ReadingState.Ready, reading.Evaluated(DateTimeOffset.MaxValue).State);
        Assert.Equal(ReadingState.Stale, reading.Evaluated(DateTimeOffset.MinValue).State);
    }

    [Theory]
    [InlineData(ReadingState.Stale)]
    [InlineData(ReadingState.Error)]
    [InlineData(ReadingState.Disabled)]
    [InlineData(ReadingState.NeedsAuth)]
    public void FreshTimestampCannotResurrectAnExplicitFailure(ReadingState state)
    {
        var reading = new ProviderReading("claude", state, [], Now);
        Assert.Same(reading, reading.Evaluated(Now));
        Assert.Equal(state, reading.Evaluated(Now).State);
    }
}
