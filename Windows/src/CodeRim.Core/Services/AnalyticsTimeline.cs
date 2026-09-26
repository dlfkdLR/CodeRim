using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

public enum AnalyticsRange { Today, SevenDays, ThirtyDays }
public sealed record AnalyticsBucket(DateTimeOffset Start, DateTimeOffset End, TokenUsage Usage, CostSummary Cost);
public static class AnalyticsTimeline
{
    // A Date scale uses elapsed time, including partial final intervals and DST.
    // Swift Charts centers the single value of a zero-length midnight domain.
    public static double Position(DateTimeOffset value, DateTimeOffset start, DateTimeOffset through, double width) =>
        through <= start ? width / 2 : width * Math.Clamp((double)(value - start).Ticks / (through - start).Ticks, 0, 1);

    // Swift Charts resolves a raw selected date to the closest bucket start.
    // Keep the earlier bucket for an exact tie and preserve the raw date in the view.
    public static AnalyticsBucket? Nearest(IReadOnlyList<AnalyticsBucket> buckets, DateTimeOffset selected)
    {
        AnalyticsBucket? nearest = null;
        var distance = long.MaxValue;
        foreach (var bucket in buckets)
        {
            var candidate = Math.Abs((bucket.Start - selected).Ticks);
            if (candidate >= distance) continue;
            nearest = bucket; distance = candidate;
        }
        return nearest;
    }
    public static DateTimeOffset Start(AnalyticsRange range, DateTimeOffset now, TimeZoneInfo zone)
    {
        var days = range switch { AnalyticsRange.SevenDays => 6, AnalyticsRange.ThirtyDays => 29, _ => 0 };
        return LocalMidnight(TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(-days), zone);
    }
    private static DateTimeOffset LocalMidnight(DateTime day, TimeZoneInfo zone)
    {
        day = DateTime.SpecifyKind(day, DateTimeKind.Unspecified);
        // Some zones advance the clock at midnight. Start at the first valid local instant.
        while (zone.IsInvalidTime(day)) day = day.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(day) ? zone.GetAmbiguousTimeOffsets(day).Max() : zone.GetUtcOffset(day);
        return new(day, offset);
    }
    public static IReadOnlyList<AnalyticsBucket> Build(IEnumerable<UsageEvent> source, AnalyticsRange range,
        DateTimeOffset now, TimeZoneInfo zone)
    {
        var start = Start(range, now, zone);
        var events = source.Where(e => e.OccurredAt >= start && e.OccurredAt <= now).OrderBy(e => e.OccurredAt).ToArray();
        var excludedModels = UsageAnalytics.Estimate(events).ExcludedModels.ToHashSet(StringComparer.Ordinal);
        var output = new List<AnalyticsBucket>(); var index = 0;
        for (var cursor = start; cursor <= now;)
        {
            var next = range == AnalyticsRange.Today ? cursor.AddHours(1)
                : LocalMidnight(TimeZoneInfo.ConvertTime(cursor, zone).Date.AddDays(1), zone);
            var end = next <= now ? next : now == DateTimeOffset.MaxValue ? now : now.AddTicks(1);
            var rows = new List<UsageEvent>();
            while (index < events.Length && events[index].OccurredAt < end) rows.Add(events[index++]);
            var usage = rows.Aggregate(TokenUsage.Zero, (sum, e) => sum.Add(e.Usage));
            // Missing coverage is a gap; an actually empty measured interval is zero.
            output.Add(new(cursor, end, usage, UsageAnalytics.Estimate(rows, excludedModels)));
            if (next <= cursor || next > now) break;
            cursor = next;
        }
        return output;
    }
}
