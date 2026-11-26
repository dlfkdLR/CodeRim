using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.UsageCore.Tests;

public sealed class PeriodAggregationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(513)]
    public void FoldMatchesPeriodMembershipAndPreservesTheFirstNewestOffset(int count)
    {
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var today = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
        var week = today.AddDays(-3);
        var month = today.AddDays(-25);
        DateTimeOffset[] timestamps = [now.ToOffset(TimeSpan.FromHours(9)), now.ToOffset(TimeSpan.FromHours(-4)),
            now.AddTicks(1), today, today.AddTicks(-1), week, month, month.AddTicks(-1)];
        var events = Enumerable.Range(0, count).Select(i => new UsageEvent(i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            timestamps[i % timestamps.Length], new TokenUsage(i, 0, i == 256 ? long.MaxValue : 2, i == 42 ? null : 0))
            { Repaired = i == 256 }).ToArray();
        var snapshot = UsageAggregation.Aggregate(events, now, WeekStart.Monday, false, TimeZoneInfo.Utc);
        var visible = events.Where(e => e.OccurredAt <= now).ToArray();
        TokenUsage Sum(DateTimeOffset from) => visible.Where(e => e.OccurredAt >= from)
            .Aggregate(TokenUsage.Zero, (s, e) => s.Add(e.Usage));
        Assert.Equal(Sum(DateTimeOffset.MinValue), snapshot.AllTime);
        Assert.Equal(Sum(month), snapshot.Month);
        Assert.Equal(Sum(week), snapshot.Week);
        Assert.Equal(Sum(today), snapshot.Today);
        Assert.Equal(count > 256 ? DataQuality.Partial : count == 0 ? DataQuality.Unavailable : DataQuality.Exact, snapshot.Quality);
        Assert.Equal(count == 0 ? null : timestamps[0].Offset, snapshot.UpdatedAt?.Offset);
        Assert.Equal(count == 0 ? null : timestamps[0].UtcTicks, snapshot.UpdatedAt?.UtcTicks);
        Assert.False(snapshot.SourcesPartial);
        Assert.Equal(now, snapshot.Through);
    }

    [Fact]
    public void FutureAndHistoricalRepairsDoNotMarkTheCurrentWindow()
    {
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        UsageEvent[] events = [
            new("old", now.AddMonths(-1), new(3, 0, 0)) { Repaired = true },
            new("future", now.AddTicks(1), new(8, 0, 0)) { Repaired = true }
        ];
        var snapshot = UsageAggregation.Aggregate(events, now, WeekStart.Sunday, false, TimeZoneInfo.Utc, true);
        Assert.Equal(3, snapshot.AllTime.InputTokens);
        Assert.Equal(DataQuality.Exact, snapshot.Quality);
        Assert.True(snapshot.SourcesPartial);
        Assert.Equal(DataQuality.Partial, UsageAggregation.Aggregate([], now, WeekStart.Sunday, true, TimeZoneInfo.Utc).Quality);
    }
}
