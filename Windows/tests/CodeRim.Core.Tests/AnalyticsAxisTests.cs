using System.Text.Json;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class AnalyticsAxisTests
{
    [Fact]
    public void TickDatesMatchNativeReferenceIncludingTransitionsCalendarStartAndDst()
    {
        using var stream = typeof(AnalyticsAxisTests).Assembly.GetManifestResourceStream("CodeRim.Core.Tests.Fixtures.AnalyticsAxisReference.json");
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var rows = document.RootElement.GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal(106, rows.Length);
        foreach (var row in rows)
        {
            var range = Enum.Parse<AnalyticsRange>(row.GetProperty("range").GetString()!);
            var start = DateTimeOffset.FromUnixTimeSeconds(row.GetProperty("start").GetInt64());
            var seconds = row.GetProperty("seconds").GetDouble();
            var zone = TimeZoneInfo.FindSystemTimeZoneById(row.GetProperty("zone").GetString()!);
            var expected = row.GetProperty("ticks").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            var actual = AnalyticsAxis.Build(range, start, start.AddSeconds(seconds), zone, (DayOfWeek)row.GetProperty("firstWeekday").GetInt32());
            Assert.True(expected.SequenceEqual(actual.Select(tick => (tick.Date - start).TotalSeconds)),
                $"Native axis mismatch: {range}, {start:O}, {seconds}s, {zone.Id}");
        }
    }

    [Fact]
    public void EmptyAndReverseDomainsHaveNoTicksAndMaximumDateDoesNotOverflow()
    {
        var start = DateTimeOffset.MaxValue.AddSeconds(-1);
        Assert.Empty(AnalyticsAxis.Build(AnalyticsRange.Today, start, start, TimeZoneInfo.Utc));
        Assert.Empty(AnalyticsAxis.Build(AnalyticsRange.SevenDays, start, start.AddSeconds(-1), TimeZoneInfo.Utc));
        Assert.Equal(2, AnalyticsAxis.Build(AnalyticsRange.Today, start, DateTimeOffset.MaxValue, TimeZoneInfo.Utc).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalyticsAxis.Build((AnalyticsRange)999, start, start, TimeZoneInfo.Utc));
    }

    [Fact]
    public void TickCoordinatesAreIndependentOfTheHourlyBuckets()
    {
        var start = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(30);
        Assert.Single(AnalyticsTimeline.Build([], AnalyticsRange.Today, end, TimeZoneInfo.Utc));
        var ticks = AnalyticsAxis.Build(AnalyticsRange.Today, start, end, TimeZoneInfo.Utc);
        Assert.Equal(new double[] { 0, 300, 600 }, ticks.Select(tick => AnalyticsTimeline.Position(tick.Date, start, end, 600)));
        Assert.All(ticks, tick => Assert.Equal(AnalyticsAxisUnit.Minute, tick.Unit));
        end = start.AddMinutes(150);
        ticks = AnalyticsAxis.Build(AnalyticsRange.Today, start, end, TimeZoneInfo.Utc);
        Assert.Equal(new double[] { 0, 240, 480 }, ticks.Select(tick => AnalyticsTimeline.Position(tick.Date, start, end, 600)));
        Assert.All(ticks, tick => Assert.Equal(AnalyticsAxisUnit.Hour, tick.Unit));
    }
}
