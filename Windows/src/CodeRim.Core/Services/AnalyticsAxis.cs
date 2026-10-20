namespace CodeRim.Core.Services;

public enum AnalyticsAxisUnit { Second, Minute, Hour, Day }
public sealed record AnalyticsAxisTick(DateTimeOffset Date, AnalyticsAxisUnit Unit);

/// <summary>Date ticks for the three analytics ranges, measured against the fixed Mac reference.</summary>
public static class AnalyticsAxis
{
    public static IReadOnlyList<AnalyticsAxisTick> Build(AnalyticsRange range, DateTimeOffset start,
        DateTimeOffset through, TimeZoneInfo zone, DayOfWeek firstWeekday = DayOfWeek.Sunday)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (!Enum.IsDefined(range)) throw new ArgumentOutOfRangeException(nameof(range));
        if (!Enum.IsDefined(firstWeekday)) throw new ArgumentOutOfRangeException(nameof(firstWeekday));
        if (through <= start) return [];
        var output = new List<AnalyticsAxisTick>();
        if (range == AnalyticsRange.Today)
        {
            // Native Swift Charts date-axis observations, including each transition
            // boundary. Seconds/minutes/hours use elapsed time through a DST fold.
            var seconds = (through - start).TotalSeconds;
            var step = seconds switch
            {
                < 6 => 1, < 30 => 5, < 75 => 15, < 150 => 30,
                < 360 => 60, < 1800 => 300, < 4500 => 900, < 9000 => 1800,
                < 21600 => 3600, < 54000 => 10800, _ => 21600
            };
            var unit = step < 60 ? AnalyticsAxisUnit.Second : step < 3600 ? AnalyticsAxisUnit.Minute : AnalyticsAxisUnit.Hour;
            for (var cursor = start; cursor <= through && output.Count < 128;)
            {
                output.Add(new(cursor, unit));
                if ((through - cursor).TotalSeconds < step) break;
                cursor = cursor.AddSeconds(step);
            }
            return output;
        }
        var local = TimeZoneInfo.ConvertTime(start, zone).Date;
        var days = range == AnalyticsRange.SevenDays ? 2 : 7;
        if (range == AnalyticsRange.ThirtyDays)
        {
            var advance = ((int)firstWeekday - (int)local.DayOfWeek + 7) % 7;
            if (local > DateTime.MaxValue.AddDays(-advance)) return output;
            local = local.AddDays(advance);
        }
        for (var count = 0; count < 128; count++)
        {
            var tick = AnalyticsTimeline.LocalMidnight(local, zone);
            if (tick > through) break;
            if (tick >= start) output.Add(new(tick, AnalyticsAxisUnit.Day));
            if (local > DateTime.MaxValue.AddDays(-days)) break;
            local = local.AddDays(days);
        }
        return output;
    }
}
