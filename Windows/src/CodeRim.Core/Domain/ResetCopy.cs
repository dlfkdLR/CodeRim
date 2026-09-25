using System.Globalization;
using System.Text;

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
        var pattern = (localReset.Date - localNow.Date).TotalDays >= 7 ? MonthDayFormat(culture) : "ddd h:mm tt";
        try { return "Resets " + localReset.ToString(pattern, culture); }
        catch (ArgumentOutOfRangeException) { return "Reset time unavailable"; }
        catch (FormatException) { return "Reset time unavailable"; }
    }

    // Foundation's MMM d template preserves locale-specific order and literals.
    // Use the matching local month/day pattern, abbreviating only format tokens.
    private static string MonthDayFormat(CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.MonthDayPattern;
        var result = new StringBuilder(pattern.Length);
        for (var index = 0; index < pattern.Length;)
        {
            var token = pattern[index++];
            result.Append(token);
            if (token is '\\' or '%' && index < pattern.Length) { result.Append(pattern[index++]); continue; }
            if (token is '\'' or '"')
            {
                while (index < pattern.Length)
                {
                    var quoted = pattern[index++]; result.Append(quoted);
                    if (quoted == '\\' && index < pattern.Length) result.Append(pattern[index++]);
                    else if (quoted == token) break;
                }
                continue;
            }
            var count = 1;
            while (index < pattern.Length && pattern[index] == token) { index++; count++; }
            var length = token == 'M' && count == 4 ? 3 : token == 'd' && count == 2 ? 1 : count;
            result.Append(token, length - 1);
        }
        // A single custom day/month token would otherwise become a standard
        // date format when passed to ToString (for example dd -> d).
        return result.Length == 1 && result[0] is 'd' or 'M' ? "%" + result : result.ToString();
    }
}
