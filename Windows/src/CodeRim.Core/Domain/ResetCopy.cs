using System.Globalization;

namespace CodeRim.Core.Domain;

/// <summary>Same rounding and calendar-day boundary as macOS ResetCopy.</summary>
public static class ResetCopy
{
    public static string Text(DateTimeOffset? reset, string format, DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        if (reset is null) return "";
        var seconds = (reset.Value - now).TotalSeconds;
        if (seconds <= 0) return "Resetting…";
        var minutes = Math.Max(1, (long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero));
        if (format == "Relative")
        {
            var hours = minutes / 60; var days = hours / 24;
            return days > 0 ? $"Resets in {days} {(days == 1 ? "Day" : "Days")} {hours % 24}h"
                : hours > 0 ? $"Resets in {hours}h {minutes % 60}m" : $"Resets in {minutes} min";
        }
        if (minutes < 60) return $"Resets in {minutes} min";
        zone ??= TimeZoneInfo.Local; culture ??= CultureInfo.CurrentCulture;
        var localReset = TimeZoneInfo.ConvertTime(reset.Value, zone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        return "Resets " + localReset.ToString((localReset.Date - localNow.Date).TotalDays >= 7 ? "MMM d" : "ddd h:mm tt", culture);
    }
}
