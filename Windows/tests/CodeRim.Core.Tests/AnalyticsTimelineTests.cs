using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;
public sealed class AnalyticsTimelineTests
{
    private static UsageEvent Event(string id, DateTimeOffset at, string model = "gpt-5.6-sol") =>
        new(id, at, new(100, 0, 10, 0), model, "Fixture", "session", "codex", "project");
    [Fact]
    public void RollingRangeCrossesMonthAndIncludesNowButExcludesFuture()
    {
        var now = new DateTimeOffset(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);
        var start = AnalyticsTimeline.Start(AnalyticsRange.SevenDays, now, TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 2, 24, 0, 0, 0, TimeSpan.Zero), start);
        var buckets = AnalyticsTimeline.Build([Event("before", start.AddTicks(-1)), Event("start", start), Event("now", now), Event("future", now.AddTicks(1))], AnalyticsRange.SevenDays, now, TimeZoneInfo.Utc);
        Assert.Equal(7, buckets.Count); Assert.Equal(220, buckets.Sum(b => b.Usage.TotalTokens));
        Assert.Equal(110, buckets[0].Usage.TotalTokens); Assert.Equal(110, buckets[^1].Usage.TotalTokens);
    }
    [Theory]
    [InlineData(2026, 3, 8, 23)]
    [InlineData(2026, 11, 1, 25)]
    public void TodayKeepsAllHoursAcrossDaylightSavingTime(int year, int month, int day, int hours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var localEnd = new DateTime(year, month, day, 23, 59, 59, DateTimeKind.Unspecified);
        var now = new DateTimeOffset(localEnd, zone.GetUtcOffset(localEnd));
        var buckets = AnalyticsTimeline.Build([], AnalyticsRange.Today, now, zone);
        Assert.Equal(hours, buckets.Count);
        Assert.True(buckets.Zip(buckets.Skip(1)).All(pair => pair.First.End == pair.Second.Start));
    }
    [Theory]
    [InlineData(11, 1, 25)]
    [InlineData(3, 1, 23)]
    public void CalendarSummaryAndTimelineShareRepeatedAndSkippedMidnight(int month, int day, int hours)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 11, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone("midnight-transition", TimeSpan.Zero,
            "midnight-transition", "standard", "daylight", [rule]);
        var date = new DateTime(2026, month, day);
        var through = new DateTimeOffset(date.AddHours(23).AddMinutes(59), zone.GetUtcOffset(date.AddHours(23)));
        var start = AnalyticsTimeline.Start(AnalyticsRange.Today, through, zone);
        var expectedStart = month == 11 ? new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedStart, start);
        UsageEvent[] events = [Event("before", start.AddTicks(-1)), Event("first", start.AddMinutes(30)), Event("second", start.AddMinutes(90))];
        var summary = UsageAggregation.Aggregate(events, through, WeekStart.Sunday, false, zone);
        var buckets = AnalyticsTimeline.Build(events, AnalyticsRange.Today, through, zone);
        Assert.Equal(hours, buckets.Count);
        Assert.Equal(220, summary.Today.TotalTokens);
        Assert.Equal(summary.Today, summary.Week);
        Assert.Equal(summary.Today, summary.Month);
        Assert.Equal(summary.Today.TotalTokens, buckets.Sum(b => b.Usage.TotalTokens));
        Assert.Equal(start, UsageAggregation.CurrentPeriodStart(through, WeekStart.Sunday, zone));
    }

    [Fact]
    public void UnknownModelCostStaysUnavailableAndMixedPricingStaysPartial()
    {
        var now = DateTimeOffset.Parse("2026-03-02T12:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var buckets = AnalyticsTimeline.Build([Event("unknown", now.AddHours(-1), "unpriced-model"), Event("known", now), Event("mixed", now, "unpriced-model")], AnalyticsRange.Today, now, TimeZoneInfo.Utc);
        Assert.Null(buckets[^2].Cost.Amount); Assert.True(buckets[^2].Cost.IsPartial);
        Assert.NotNull(buckets[^1].Cost.Amount); Assert.True(buckets[^1].Cost.IsPartial);
    }
    [Fact]
    public void DateScaleMatchesNativeReferenceForPartialTodayAndZeroLengthMidnight()
    {
        var start = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        var through = start.AddMinutes(150);
        Assert.Equal(0, AnalyticsTimeline.Position(start, start, through, 600));
        Assert.Equal(240, AnalyticsTimeline.Position(start.AddHours(1), start, through, 600));
        Assert.Equal(480, AnalyticsTimeline.Position(start.AddHours(2), start, through, 600));
        Assert.Equal(300, AnalyticsTimeline.Position(start, start, start, 600));
        Assert.Equal(288, AnalyticsTimeline.Position(start.AddHours(2), start, through, 360));
    }
    [Fact]
    public void DateScaleUsesActualElapsedTimeAcrossRepeatedDstHour()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var through = new DateTimeOffset(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);
        var buckets = AnalyticsTimeline.Build([], AnalyticsRange.SevenDays, through, zone);
        var positions = buckets.Select(b => AnalyticsTimeline.Position(b.Start, buckets[0].Start, through, 600)).ToArray();
        // Measured ChartProxy positions from the native macOS reference, not equal columns.
        double[] reference = [0, 91.71974522292993, 183.43949044585986, 275.1592356687898,
            366.8789808917197, 458.59872611464965, 554.140127388535];
        Assert.Equal(reference.Length, positions.Length);
        for (var i = 0; i < reference.Length; i++) Assert.Equal(reference[i], positions[i], 8);
        Assert.True(positions[6] - positions[5] > positions[5] - positions[4]);
    }
    [Fact]
    public void SelectionUsesNearestStartWithEarlierMidpointTieAndRangeRollover()
    {
        var start = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        var buckets = AnalyticsTimeline.Build([], AnalyticsRange.Today, start.AddMinutes(150), TimeZoneInfo.Utc);
        Assert.Equal(start, AnalyticsTimeline.Nearest(buckets, start.AddMinutes(30))!.Start);
        Assert.Equal(start.AddHours(1), AnalyticsTimeline.Nearest(buckets, start.AddMinutes(30).AddTicks(1))!.Start);
        Assert.Equal(start.AddHours(1), AnalyticsTimeline.Nearest(buckets, start.AddMinutes(55))!.Start);
        Assert.Equal(start, AnalyticsTimeline.Nearest(buckets, start.AddDays(-1))!.Start);
        Assert.Equal(start.AddHours(2), AnalyticsTimeline.Nearest(buckets, start.AddDays(1))!.Start);
        Assert.Null(AnalyticsTimeline.Nearest([], start));
    }

}
