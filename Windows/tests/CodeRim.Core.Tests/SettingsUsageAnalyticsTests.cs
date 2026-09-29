using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class SettingsUsageAnalyticsTests
{
    private static readonly string[] CachedTokenSeriesIds = ["input", "cached", "output"];
    private static readonly string[] CombinedTokenSeriesIds = ["input", "output"];
    private static readonly string[] ExpectedModelOrder = ["a", "z", "b", "c", "d", "e", "f"];
    private static readonly string[] ExpectedModelSeries = ["model:a", "model:z", "model:b", "model:c", "model:d", "other-models"];
    private static readonly string[] ExpectedSessionOrder = ["a", "b", "c"];
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static UsageEvent Event(string id, DateTimeOffset at, long input, long cached = 0, long output = 0,
        string model = "model-a", string session = "session-a", long? writes = 0) =>
        new(id, at, new(input, cached, output, writes), model, "Fixture", session);
    private static AnalyticsSourceFrame Frame(IReadOnlyList<UsageEvent> events, DateTimeOffset? through = null,
        DataQuality quality = DataQuality.Exact) => new(through ?? Now,
        new(new(999999, 0, 0), new(999999, 0, 0), new(999999, 0, 0), new(999999, 0, 0), quality, Now.AddDays(-20)), events);

    [Theory]
    [InlineData(AnalyticsRange.Today, 7)]
    [InlineData(AnalyticsRange.SevenDays, 7)]
    [InlineData(AnalyticsRange.ThirtyDays, 30)]
    public void CalendarRangeIncludesBothBoundaryEventsAndNeverUsesCalendarSummaryTotals(AnalyticsRange range, int days)
    {
        var start = new DateTimeOffset(Now.Date.AddDays(1 - days), TimeSpan.Zero);
        var frame = Frame([Event("before", start.AddTicks(-1), 1000), Event("start", start, 10),
            Event("through", Now, 20), Event("future", Now.AddTicks(1), 2000)]);
        var snapshot = SettingsUsageAnalytics.Build(frame, range, TimeZoneInfo.Utc);
        Assert.Equal(range == AnalyticsRange.Today ? AnalyticsRange.SevenDays : range, snapshot.Range);
        Assert.Equal(days, snapshot.Days.Count); Assert.Equal(start, snapshot.Days[0].Start);
        Assert.Equal(Now, snapshot.Through); Assert.Equal(30, snapshot.Usage.TotalTokens);
        Assert.Equal(10, snapshot.Days[0].Usage.TotalTokens); Assert.Equal(20, snapshot.Days[^1].Usage.TotalTokens);
        Assert.All(snapshot.Days, day => Assert.True(day.Start <= day.End));
        Assert.True(snapshot.Days[^1].End >= Now && snapshot.Days[^1].End < new DateTimeOffset(Now.Date.AddDays(1), TimeSpan.Zero));
        Assert.Equal(snapshot.Usage, snapshot.Days.Aggregate(TokenUsage.Zero, (sum, day) => sum.Add(day.Usage)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TokenSeriesConservesInputIncludingCacheWritesAndOptionalMetadata(bool cached)
    {
        var snapshot = SettingsUsageAnalytics.Build(Frame([Event("a", Now, 100, 40, 20, writes: 30),
            Event("b", Now.AddDays(-1), 50, 10, 5, writes: null)]), AnalyticsRange.SevenDays, TimeZoneInfo.Utc);
        var rows = SettingsUsageAnalytics.Series(snapshot, SettingsUsageGrouping.TokenType, cached);
        Assert.Equal(cached ? CachedTokenSeriesIds : CombinedTokenSeriesIds, rows.Select(x => x.Id));
        Assert.Equal(cached ? new long[] { 100, 50, 25 } : new long[] { 150, 25 }, rows.Select(x => x.Tokens));
        Assert.Equal(175, rows.Sum(x => x.Tokens)); Assert.Null(snapshot.Usage.CacheWriteInputTokens);
        foreach (var day in snapshot.Days)
            Assert.Equal(day.Usage.TotalTokens, SettingsUsageAnalytics.Series(snapshot, SettingsUsageGrouping.TokenType, cached, day).Sum(x => x.Tokens));
        Assert.Equal(60, SettingsUsageAnalytics.Series(snapshot, SettingsUsageGrouping.TokenType, true, snapshot.Days[^1])[0].Tokens);
    }

    [Fact]
    public void ModelsUseRangeTopFiveStableIdsOtherAndZeroForMissingDay()
    {
        UsageEvent[] events = [Event("z", Now.AddDays(-1), 500, model: "z"), Event("a", Now, 500, model: "a"),
            Event("b", Now, 400, model: "b"), Event("c", Now, 300, model: "c"), Event("d", Now, 200, model: "d"),
            Event("e", Now, 100, model: "e"), Event("f", Now.AddDays(-1), 50, model: "f")];
        var snapshot = SettingsUsageAnalytics.Build(Frame(events.Reverse().ToArray()), AnalyticsRange.SevenDays, TimeZoneInfo.Utc);
        Assert.Equal(ExpectedModelOrder, snapshot.Models.Select(x => x.Id));
        var series = SettingsUsageAnalytics.Series(snapshot, SettingsUsageGrouping.Model, true);
        Assert.Equal(ExpectedModelSeries, series.Select(x => x.Id));
        Assert.Equal(150, series[^1].Tokens); Assert.Equal("Other models", series[^1].Title);
        Assert.Equal(snapshot.Usage.TotalTokens, series.Sum(x => x.Tokens));
        foreach (var day in snapshot.Days)
        {
            var daily = SettingsUsageAnalytics.Series(snapshot, SettingsUsageGrouping.Model, false, day);
            Assert.Equal(series.Select(x => x.Id), daily.Select(x => x.Id));
            Assert.Equal(day.Usage.TotalTokens, daily.Sum(x => x.Tokens));
        }
        var last = SettingsUsageAnalytics.Series(snapshot, SettingsUsageGrouping.Model, true, snapshot.Days[^1]);
        Assert.Equal(0, last.Single(x => x.Id == "model:z").Tokens);
        Assert.Equal(100, last.Single(x => x.Id == "other-models").Tokens);
        Assert.Equal(100d / 1500, (double)last[^1].Tokens / last.Sum(x => x.Tokens), 12);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(5)]
    public void AtMostFiveModelsDoesNotInventOtherSeries(int count)
    {
        var events = Enumerable.Range(0, count).Select(i => Event("e" + i, Now, 10, model: "m" + i)).ToArray();
        var snapshot = SettingsUsageAnalytics.Build(Frame(events), AnalyticsRange.SevenDays, TimeZoneInfo.Utc);
        var series = SettingsUsageAnalytics.Series(snapshot, SettingsUsageGrouping.Model, true);
        Assert.Equal(count, series.Count); Assert.DoesNotContain(series, x => x.Id == "other-models");
    }

    [Fact]
    public void SessionsRankByTokensThenOrdinalIdNotRecencyAndKeepRangedLastActivity()
    {
        UsageEvent[] events = [Event("a1", Now.AddDays(-5), 400, session: "a"), Event("a2", Now.AddDays(-4), 100, session: "a"),
            Event("b1", Now, 500, session: "b"), Event("c1", Now, 1, session: "c"),
            Event("outside", Now.AddDays(-8), 9000, session: "a"), Event("future", Now.AddTicks(1), 5000, session: "c")];
        var snapshot = SettingsUsageAnalytics.Build(Frame(events.Reverse().ToArray()), AnalyticsRange.SevenDays, TimeZoneInfo.Utc);
        Assert.Equal(ExpectedSessionOrder, snapshot.Sessions.Select(x => x.Id));
        Assert.Equal(new long[] { 500, 500, 1 }, snapshot.Sessions.Select(x => x.Usage.TotalTokens));
        Assert.Equal(Now.AddDays(-4), snapshot.Sessions[0].LastActivityAt);
        Assert.Equal(Now, snapshot.Sessions[2].LastActivityAt);
        Assert.Equal(snapshot.Usage.TotalTokens, snapshot.Sessions.Sum(x => x.Usage.TotalTokens));
    }

    [Fact]
    public void DaySelectionUsesLocalDateEvenAfterThroughButRejectsTheNextDate()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Fixture +09", TimeSpan.FromHours(9), "Fixture", "Fixture");
        var snapshot = SettingsUsageAnalytics.Build(Frame([Event("a", Now, 10)]), AnalyticsRange.SevenDays, zone);
        Assert.Equal(snapshot.Days[^1], SettingsUsageAnalytics.DayAt(snapshot, Now.AddHours(2), zone));
        Assert.Equal(snapshot.Days[^1], SettingsUsageAnalytics.DayAt(snapshot, Now.ToOffset(TimeSpan.FromHours(-7)), zone));
        Assert.Null(SettingsUsageAnalytics.DayAt(snapshot, snapshot.Days[0].Start.AddTicks(-1), zone));
        Assert.Null(SettingsUsageAnalytics.DayAt(snapshot, new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.FromHours(9)), zone));
        Assert.Null(SettingsUsageAnalytics.DayAt(snapshot, null, zone));
    }

    [Theory]
    [InlineData(2026, 3, 8, 23)]
    [InlineData(2026, 11, 1, 25)]
    public void CalendarBucketsPreserveRealDstMidnightsWithoutTwentyFourHourDrift(int year, int month, int day, int hours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var next = new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Unspecified).AddDays(1);
        var through = new DateTimeOffset(next, zone.GetUtcOffset(next));
        var snapshot = SettingsUsageAnalytics.Build(Frame([], through), AnalyticsRange.SevenDays, zone);
        Assert.Equal(7, snapshot.Days.Count);
        var target = snapshot.Days.Single(x => TimeZoneInfo.ConvertTime(x.Start, zone).Day == day);
        Assert.Equal(hours, (target.End - target.Start).TotalHours);
        Assert.All(snapshot.Days, x => Assert.Equal(0, TimeZoneInfo.ConvertTime(x.Start, zone).Hour));
        Assert.True(snapshot.Days.Zip(snapshot.Days.Skip(1)).All(x => x.First.End == x.Second.Start));
    }

    [Fact]
    public void MidnightGapStartsAtFirstValidLocalInstantAndPartialQualityIsPreserved()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        var through = new DateTimeOffset(2018, 11, 5, 12, 0, 0, TimeSpan.FromHours(-2));
        var snapshot = SettingsUsageAnalytics.Build(Frame([Event("transition", through.AddDays(-1), 10)], through, DataQuality.Partial), AnalyticsRange.SevenDays, zone);
        var transition = snapshot.Days.Single(x => TimeZoneInfo.ConvertTime(x.Start, zone).Date == new DateTime(2018, 11, 4));
        Assert.Equal(1, TimeZoneInfo.ConvertTime(transition.Start, zone).Hour);
        Assert.Equal(23, (transition.End - transition.Start).TotalHours);
        Assert.Equal(DataQuality.Partial, snapshot.Quality); Assert.Equal(10, snapshot.Usage.TotalTokens);
    }

    [Theory]
    [InlineData(DataQuality.Exact)] [InlineData(DataQuality.Partial)]
    public void EmptyOrEntirelyOutOfRangeUsageIsUnavailableRatherThanKnownZero(DataQuality quality)
    {
        foreach (UsageEvent[] events in new UsageEvent[][] { [], [Event("zero", Now, 0)], [Event("old", Now.AddDays(-40), 100)] })
        {
            var result = SettingsUsageAnalytics.Build(Frame(events, quality: quality), AnalyticsRange.SevenDays, TimeZoneInfo.Utc);
            Assert.True(result.Usage.IsZero); Assert.Equal(DataQuality.Unavailable, result.Quality);
            Assert.Equal(7, result.Days.Count);
        }
    }

    [Fact]
    public void RetainedPartialHistoryFlagOverridesOtherwiseExactSourceQuality()
    {
        var frame = Frame([Event("a", Now, 10)]);
        frame = frame with { Local = frame.Local with { RetainsPartialHistory = true } };
        Assert.Equal(DataQuality.Partial, SettingsUsageAnalytics.Build(frame, AnalyticsRange.SevenDays, TimeZoneInfo.Utc).Quality);
    }

}
