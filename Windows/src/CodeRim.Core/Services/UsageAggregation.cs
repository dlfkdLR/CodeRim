using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

/// Calendar calculations independent of file discovery and scanning.
public static class UsageAggregation
{
    /// The earliest start of the current week and month: the periods a snapshot reports as current.
    public static DateTimeOffset CurrentPeriodStart(DateTimeOffset now, WeekStart weekStart, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var dayOffset = weekStart == WeekStart.Monday ? ((int)localNow.DayOfWeek + 6) % 7 : (int)localNow.DayOfWeek;
        var week = LocalStartUtc(localNow.Date.AddDays(-dayOffset), timeZone);
        var month = LocalStartUtc(new DateTime(localNow.Year, localNow.Month, 1), timeZone);
        return week < month ? week : month;
    }

    public static UsageSnapshot Aggregate(
        IReadOnlyList<UsageEvent> events,
        DateTimeOffset now,
        WeekStart weekStart,
        bool partial,
        TimeZoneInfo? timeZone = null,
        bool? sourcesPartial = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var todayStart = LocalStartUtc(localNow.Date, timeZone);
        var dayOffset = weekStart == WeekStart.Monday
            ? ((int)localNow.DayOfWeek + 6) % 7
            : (int)localNow.DayOfWeek;
        var weekStartUtc = LocalStartUtc(localNow.Date.AddDays(-dayOffset), timeZone);
        var monthStartUtc = LocalStartUtc(
            new DateTime(localNow.Year, localNow.Month, 1),
            timeZone);

        var today = TokenUsage.Zero;
        var week = TokenUsage.Zero;
        var month = TokenUsage.Zero;
        var allTime = TokenUsage.Zero;
        DateTimeOffset? newest = null;

        foreach (var usageEvent in events)
        {
            if (usageEvent.OccurredAt > now)
            {
                continue;
            }

            allTime = allTime.Add(usageEvent.Usage);
            if (usageEvent.OccurredAt >= monthStartUtc)
            {
                month = month.Add(usageEvent.Usage);
            }

            if (usageEvent.OccurredAt >= weekStartUtc)
            {
                week = week.Add(usageEvent.Usage);
            }

            if (usageEvent.OccurredAt >= todayStart)
            {
                today = today.Add(usageEvent.Usage);
            }

            if (newest is null || usageEvent.OccurredAt > newest.Value)
            {
                newest = usageEvent.OccurredAt;
            }
        }

        // A record in the current week or month that was marked when it was read or saved makes them partial,
        // even after the file that held it is gone.
        var current = weekStartUtc < monthStartUtc ? weekStartUtc : monthStartUtc;
        var marked = events.Any(e => e.Repaired && e.OccurredAt >= current && e.OccurredAt <= now);
        var quality = newest is null
            ? partial ? DataQuality.Partial : DataQuality.Unavailable
            : partial || marked ? DataQuality.Partial : DataQuality.Exact;
        return new UsageSnapshot(today, week, month, allTime, quality, newest) { SourcesPartial = sourcesPartial ?? partial, Through = now, TimeZoneIdentifier = timeZone.Id };
    }

    /// The day's first instant, the same one the charts start the day at: a midnight that happens twice
    /// (the clock goes back at 01:00) starts at its first occurrence, not the standard-time one
    /// `ConvertTimeToUtc` would pick, so Today and the chart's day hold the same records.
    private static DateTimeOffset LocalStartUtc(DateTime localDate, TimeZoneInfo timeZone)
        => AnalyticsTimeline.LocalMidnight(localDate, timeZone).ToUniversalTime();

}
