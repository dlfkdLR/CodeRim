using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class AnalyticsSnapshotCacheTests
{
    private static readonly DateTimeOffset Before = new(2026, 9, 28, 23, 59, 0, TimeSpan.Zero);
    private static AnalyticsSourceFrame Publication(DateTimeOffset? through = null, DataQuality quality = DataQuality.Exact,
        IReadOnlyList<UsageEvent>? events = null) => new(through ?? Before,
        new(new(200, 0, 0), new(300, 0, 0), new(900, 0, 0), new(1200, 0, 0), quality, Before.AddDays(-2))
            { RetainsPartialHistory = quality == DataQuality.Partial },
        events ?? [new("first", Before.AddDays(-6), new(100, 0, 0)), new("last", Before, new(200, 0, 0))]);

    private static void SameSnapshot(AnalyticsSourceFrame? expected, AnalyticsSourceFrame? actual)
    {
        Assert.NotNull(expected); Assert.NotNull(actual);
        Assert.Equal(expected.Through, actual.Through); Assert.Equal(expected.Local, actual.Local);
        Assert.Equal(expected.Events, actual.Events);
    }

    [Theory]
    [InlineData(DataQuality.Exact)]
    [InlineData(DataQuality.Partial)]
    [InlineData(DataQuality.Unavailable)]
    public void SuccessfulPublicationOwnsEventsAndUsesThroughRatherThanLastEvent(DataQuality quality)
    {
        var cache = new AnalyticsSnapshotCache();
        var original = new UsageEvent("old", Before.AddDays(-2), new(123, 40, 20, 15));
        var events = new List<UsageEvent> { original };
        var publication = Publication(quality: quality, events: events);
        var frame = Assert.IsType<AnalyticsSourceFrame>(cache.Read("7d", publication, quality));
        Assert.Equal(Before, frame.Through); Assert.Equal(Before.AddDays(-2), frame.Local.UpdatedAt);
        Assert.Equal(publication.Local, frame.Local); Assert.NotSame(events, frame.Events);
        events.Clear(); events.Add(new("replacement", Before, new(999, 0, 0)));
        Assert.Same(original, Assert.Single(frame.Events));
        SameSnapshot(frame, cache.Read("7d", publication, quality));
        if (frame.Events is IList<UsageEvent> list)
        {
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list[0] = events[0]);
        }
    }

    [Theory]
    [InlineData(DataQuality.Stale)]
    [InlineData(DataQuality.Error)]
    public void FailedReadAfterMidnightRetainsDomainThroughPayloadAndPartialQuality(DataQuality failure)
    {
        var cache = new AnalyticsSnapshotCache(); var publication = Publication(quality: DataQuality.Partial);
        var first = Assert.IsType<AnalyticsSourceFrame>(cache.Read("7d", publication, DataQuality.Partial));
        // Neither a metadata Changed before I/O nor the pending-start notification is a source publication.
        SameSnapshot(first, cache.Read("7d", publication, DataQuality.Partial));
        SameSnapshot(first, cache.Read("7d", publication, DataQuality.Partial, refreshing: true));
        Assert.Null(cache.Read("30d", publication, DataQuality.Partial, refreshing: true));
        var retained = Assert.IsType<AnalyticsSourceFrame>(cache.Read("7d", publication, failure));
        SameSnapshot(first, retained); Assert.Equal(DataQuality.Partial, retained.Local.Quality);
        var snapshot = SettingsUsageAnalytics.Build(retained, AnalyticsRange.SevenDays, TimeZoneInfo.Utc);
        Assert.Equal(Before, snapshot.Through); Assert.Equal(300, snapshot.Usage.TotalTokens);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero), snapshot.Days[0].Start);
        Assert.Equal(snapshot.Days[0], SettingsUsageAnalytics.DayAt(snapshot, publication.Events[0].OccurredAt, TimeZoneInfo.Utc));
        Assert.Null(cache.Read("30d", publication, failure));
        Assert.Null(new AnalyticsSnapshotCache().Read("7d", publication, failure));
    }

    [Fact]
    public void HiddenSuccessUpdatesEveryPreviouslyQueriedRangeEvenWhenNextNotificationIsFailure()
    {
        var cache = new AnalyticsSnapshotCache(); var a = Publication();
        cache.Read("7d", a, DataQuality.Exact); cache.Read("30d", a, DataQuality.Exact);
        var b = Publication(Before.AddMinutes(2));
        // The pane was elsewhere for B's success; the first rendered notification is a later error.
        var latest = Assert.IsType<AnalyticsSourceFrame>(cache.Read("7d", b, DataQuality.Error));
        Assert.Equal(b.Through, latest.Through); SameSnapshot(latest, cache.Read("30d", b, DataQuality.Stale));
        var snapshot = SettingsUsageAnalytics.Build(latest, AnalyticsRange.SevenDays, TimeZoneInfo.Utc);
        Assert.Equal(200, snapshot.Usage.TotalTokens);
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero), snapshot.Days[0].Start);
        Assert.Null(cache.Read("unqueried", b, DataQuality.Error));
    }

    [Fact]
    public void SameThroughNewPublicationReplacesPayloadButDoesNotAdmitFailedUnqueriedRange()
    {
        var cache = new AnalyticsSnapshotCache(); var a = Publication();
        var first = cache.Read("7d", a, DataQuality.Exact);
        var b = Publication(events: [new("new", Before, new(777, 0, 0))]);
        var changed = Assert.IsType<AnalyticsSourceFrame>(cache.Read("7d", b, DataQuality.Stale));
        Assert.NotSame(first, changed); Assert.Equal(777, Assert.Single(changed.Events).Usage.TotalTokens);
        Assert.Null(cache.Read("30d", b, DataQuality.Stale));
        SameSnapshot(changed, cache.Read("30d", b, DataQuality.Exact));
    }

    [Fact]
    public void ProviderInstancesNeverShareQueriesOrPayload()
    {
        var codex = new AnalyticsSnapshotCache(); var claude = new AnalyticsSnapshotCache();
        var a = Publication(); var b = Publication(events: [new("claude", Before, new(42, 0, 0), Provider: "claude")]);
        var first = codex.Read("7d", a, DataQuality.Exact);
        Assert.Null(claude.Read("7d", b, DataQuality.Error));
        var other = claude.Read("30d", b, DataQuality.Exact);
        SameSnapshot(first, codex.Read("7d", a, DataQuality.Stale));
        Assert.Null(codex.Read("30d", a, DataQuality.Stale));
        SameSnapshot(other, claude.Read("30d", b, DataQuality.Stale));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NullOrEpochChangeRevokesQueriesBeforeMaintenanceFailure(bool changeEpoch)
    {
        var cache = new AnalyticsSnapshotCache(); var a = Publication();
        cache.Read("7d", a, DataQuality.Exact); cache.Read("30d", a, DataQuality.Exact);
        var epoch = changeEpoch ? 1 : 0;
        Assert.Null(cache.Read("7d", null, DataQuality.Exact, epoch, refreshing: true));
        Assert.Null(cache.Read("30d", null, DataQuality.Error, epoch));
        var recovered = Publication(Before.AddMinutes(2), events: [new("new", Before.AddMinutes(1), new(9, 0, 0))]);
        Assert.Null(cache.Read("7d", recovered, DataQuality.Error, epoch));
        var result = Assert.IsType<AnalyticsSourceFrame>(cache.Read("7d", recovered, DataQuality.Exact, epoch));
        Assert.Equal(9, Assert.Single(result.Events).Usage.TotalTokens);
        Assert.Null(cache.Read("30d", recovered, DataQuality.Error, epoch));
    }

    [Fact]
    public void EpochChangeWithoutIntermediateNullAndExplicitClearAlsoRevokeOldQueries()
    {
        var cache = new AnalyticsSnapshotCache(); var a = Publication();
        cache.Read("7d", a, DataQuality.Exact, epoch: 2);
        var b = Publication(Before.AddMinutes(2));
        Assert.Null(cache.Read("7d", b, DataQuality.Error, epoch: 3));
        Assert.NotNull(cache.Read("7d", b, DataQuality.Exact, epoch: 3));
        cache.Clear();
        Assert.Null(cache.Read("7d", b, DataQuality.Error, epoch: 3));
        Assert.Null(cache.Read("7d", null, null, epoch: 3));
        Assert.NotNull(cache.Read("7d", b, DataQuality.Exact, epoch: 3));
    }
}
