namespace CodeRim.Core.Domain;

internal static class UsageCalendar
{
    internal static DateTimeOffset LocalMidnight(DateTime day, TimeZoneInfo zone)
    {
        day = DateTime.SpecifyKind(day, DateTimeKind.Unspecified);
        // Some zones advance the clock at midnight. Start at the first valid local instant.
        while (zone.IsInvalidTime(day)) day = day.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(day) ? zone.GetAmbiguousTimeOffsets(day).Max() : zone.GetUtcOffset(day);
        return new(day, offset);
    }
}
