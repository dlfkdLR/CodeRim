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
    [Fact]
    public void UnknownModelCostStaysUnavailableAndMixedPricingStaysPartial()
    {
        var now = DateTimeOffset.Parse("2026-03-02T12:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var buckets = AnalyticsTimeline.Build([Event("unknown", now.AddHours(-1), "unpriced-model"), Event("known", now), Event("mixed", now, "unpriced-model")], AnalyticsRange.Today, now, TimeZoneInfo.Utc);
        Assert.Null(buckets[^2].Cost.Amount); Assert.True(buckets[^2].Cost.IsPartial);
        Assert.NotNull(buckets[^1].Cost.Amount); Assert.True(buckets[^1].Cost.IsPartial);
    }
}
