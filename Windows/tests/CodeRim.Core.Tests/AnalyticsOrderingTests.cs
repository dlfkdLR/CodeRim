using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class AnalyticsOrderingTests
{
    [Fact]
    public void EqualModelTotalsKeepTheSameOrderAfterEventsAreReordered()
    {
        var now = DateTimeOffset.UnixEpoch;
        UsageEvent[] events = [new("z", now, new(10, 0, 0, 0), "z-model"),
            new("a", now, new(10, 0, 0, 0), "a-model"), new("large", now, new(20, 0, 0, 0), "large-model")];
        string[] expected = ["large-model", "a-model", "z-model"];
        Assert.Equal(expected, UsageAnalytics.Group(events, "model").Select(row => row.Name));
        Assert.Equal(expected, UsageAnalytics.Group(events.Reverse(), "model").Select(row => row.Name));
    }
}
