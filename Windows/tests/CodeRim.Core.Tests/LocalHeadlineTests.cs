using CodeRim.Core.Domain;

namespace CodeRim.Core.Tests;

public sealed class LocalHeadlineTests
{
    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public void RingAndAlertsFollowTheMostUsedLocalLimit(string id)
    {
        var now = DateTimeOffset.UtcNow;
        var reading = new ProviderReading(id, ReadingState.Ready, [new("session", "5 hours", 20, now.AddHours(1)), new("weekly", "Weekly", 85, now.AddDays(1))], now);
        Assert.Equal("weekly", reading.Headline!.Id);
        var tracker = new ThresholdTracker(); Assert.Equal([80], tracker.Observe(reading, now));
        Assert.Empty(tracker.Observe(reading, now));
        Assert.Equal([100], tracker.Observe(reading with { Windows = [reading.Windows[0], reading.Windows[1] with { UsedPercent = 100 }] }, now));
    }
    [Fact]
    public void ProviderDefinedOrderingAndNonpercentageFallbackArePreserved()
    {
        var first = new LimitWindow("first", "Primary", 10); var next = new LimitWindow("next", "Secondary", 95);
        Assert.Equal(first, new ProviderReading("cursor", ReadingState.Ready, [first, next]).Headline);
        var reset = new LimitWindow("reset", "Reset credits", RemainingCount: 2);
        Assert.Equal(next, new ProviderReading("codex", ReadingState.Ready, [reset, first, next]).Headline);
        Assert.Equal(reset, new ProviderReading("codex", ReadingState.Ready, [reset]).Headline);
        Assert.Null(new ProviderReading("codex", ReadingState.Ready, []).Headline);
    }
}
