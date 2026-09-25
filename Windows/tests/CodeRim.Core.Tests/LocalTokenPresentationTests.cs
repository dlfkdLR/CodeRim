using System.Globalization;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class LocalTokenPresentationTests
{
    [Fact]
    public void MissingAndCompletedEmptyReadDoNotInventZero()
    {
        Assert.Equal("Loading…", LocalTokenPresentation.Text(null, TokenNumberStyle.Detailed));
        Assert.Equal("Unavailable", LocalTokenPresentation.Text(UsageSnapshot.Empty, TokenNumberStyle.Detailed));
        Assert.Equal("0 tokens", LocalTokenPresentation.Text(UsageSnapshot.Empty with { Quality = DataQuality.Exact }, TokenNumberStyle.Detailed));
    }

    [Theory]
    [InlineData(DataQuality.Exact, "526 tokens")]
    [InlineData(DataQuality.Partial, "526 tokens")]
    [InlineData(DataQuality.Stale, "526 tokens (stale)")]
    [InlineData(DataQuality.Unavailable, "Unavailable")]
    [InlineData(DataQuality.Error, "Unavailable")]
    public void QualityStatesKeepCachedInputInsideTheTotal(DataQuality quality, string expected)
    {
        var snapshot = UsageSnapshot.Empty with { Today = new(500, 400, 26), Quality = quality };
        Assert.Equal(expected, LocalTokenPresentation.Text(snapshot, TokenNumberStyle.Detailed));
    }

    [Fact]
    public void FailedReadRetainsTotalsAndTimestampButMarksThemStale()
    {
        var old = UsageSnapshot.Empty with { Today = new(500, 400, 26), Quality = DataQuality.Exact,
            UpdatedAt = new DateTimeOffset(2026, 9, 25, 1, 0, 0, TimeSpan.Zero) };
        var stale = LocalTokenPresentation.AfterFailure(old);
        Assert.Equal(old with { Quality = DataQuality.Stale }, stale);
        Assert.Equal("526 tokens (stale)", LocalTokenPresentation.Text(stale, TokenNumberStyle.Detailed));
        Assert.Equal(DataQuality.Exact, old.Quality);
    }

    [Fact]
    public void FirstReadFailureEndsLoadingWithoutPublishingZero()
    {
        var failed = LocalTokenPresentation.AfterFailure(null);
        Assert.Equal(DataQuality.Error, failed.Quality);
        Assert.Null(failed.UpdatedAt);
        Assert.Equal("Unavailable", LocalTokenPresentation.Text(failed, TokenNumberStyle.Compact));
    }

    [Fact]
    public void CompletedEmptyPartialInventoryIsNotMeasuredZero()
    {
        var aggregate = UsageScanner.Aggregate([], DateTimeOffset.Now, WeekStart.Monday, true);
        var snapshot = LocalTokenPresentation.CompletedRead(aggregate);
        Assert.Equal(DataQuality.Unavailable, snapshot.Quality);
        Assert.Equal("Unavailable", LocalTokenPresentation.Text(snapshot, TokenNumberStyle.Detailed));
    }

    [Fact]
    public void CompletedPartialHistoryKeepsKnownZeroTodayAndAllTime()
    {
        var now = DateTimeOffset.Now;
        UsageEvent[] events = [new("yesterday", now.AddDays(-1), new(100, 80, 10))];
        var aggregate = UsageScanner.Aggregate(events, now, WeekStart.Monday, true);
        var snapshot = LocalTokenPresentation.CompletedRead(aggregate);
        Assert.Equal(aggregate, snapshot);
        Assert.Equal(DataQuality.Partial, snapshot.Quality);
        Assert.Equal("0 tokens", LocalTokenPresentation.Text(snapshot, TokenNumberStyle.Detailed));
        Assert.Equal(110, snapshot.AllTime.TotalTokens);
    }

    [Theory]
    [InlineData("en-US", TokenNumberStyle.Detailed, "610,526 tokens")]
    [InlineData("de-DE", TokenNumberStyle.Detailed, "610.526 tokens")]
    [InlineData("en-US", TokenNumberStyle.Compact, "611K tokens")]
    public void FollowsSelectedNumberStyleAndCulture(string culture, TokenNumberStyle style, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var snapshot = UsageSnapshot.Empty with { Today = new(608595, 544279, 1931), Quality = DataQuality.Partial };
            Assert.Equal(expected, LocalTokenPresentation.Text(snapshot, style));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void CalendarReaggregationDropsYesterdaysTodayWithoutLosingHistory()
    {
        var now = DateTimeOffset.Now;
        var yesterday = now.AddDays(-1);
        UsageEvent[] events = [new("yesterday", yesterday, new(100, 80, 10))];
        var prior = UsageScanner.Aggregate(events, yesterday, WeekStart.Monday, false);
        var current = UsageScanner.Aggregate(events, now, WeekStart.Monday, false);
        Assert.Equal("110 tokens", LocalTokenPresentation.Text(prior, TokenNumberStyle.Detailed));
        Assert.Equal("0 tokens", LocalTokenPresentation.Text(current, TokenNumberStyle.Detailed));
        Assert.Equal(110, current.AllTime.TotalTokens);
    }
}
