using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

/// Where the clock goes back from 01:00 to 00:00, midnight happens twice. Today and the chart start the day
/// at the same (first) midnight, so a record between the two is in both or neither.
public sealed class AmbiguousMidnightTests
{
    private static TimeZoneInfo Zone()
    {
        // -05:00 standard, -04:00 daylight; daylight time ends at 01:00 on the first Sunday of November.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 11, 1, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone("Fixture repeated midnight", TimeSpan.FromHours(-5), "Fixture", "Fixture", "Fixture DST", [rule]);
    }

    [Fact]
    public void TodayAndTheChartStartTheDayAtTheSameMidnight()
    {
        var zone = Zone();
        var day = new DateTime(2026, 11, 1); // 00:00–01:00 local happens at -04:00 and again at -05:00
        Assert.True(zone.IsAmbiguousTime(day));
        var record = new UsageEvent("between", new DateTimeOffset(2026, 11, 1, 4, 30, 0, TimeSpan.Zero), new(100, 0, 10), "gpt-5.6");
        var now = new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

        var chart = AnalyticsTimeline.Build([record], AnalyticsRange.Today, now, zone);
        var charted = chart.Aggregate(0L, (sum, bucket) => sum + bucket.Usage.TotalTokens);
        var today = UsageScanner.Aggregate([record], now, WeekStart.Monday, partial: false, zone).Today.TotalTokens;

        Assert.Equal(110, charted);
        Assert.Equal(charted, today);
    }
}
