using System.Globalization;
using CodeRim.Core.Services;

namespace CodeRim.Core.Domain;

/// <summary>The reference's numeric copy. Only visual bar widths are clamped, never an exceeded limit.</summary>
public static class LimitFormatting
{
    public static (string Used, string Left) Halves(double percent)
    {
        if (!double.IsFinite(percent) || percent < 0) return ("—", "—");
        if (percent is > 0 and < 1 or > 99 and < 100)
        {
            var left = Math.Max(0, 100 - percent);
            return (Small(percent), left > 99.9 ? ">99.9" : Small(left));
        }
        var rounded = Math.Round(percent, MidpointRounding.AwayFromZero);
        if (rounded >= 9223372036854775808d) return ("—", "—");
        var used = (long)rounded;
        return (used.ToString(CultureInfo.InvariantCulture), Math.Max(0, 100 - used).ToString(CultureInfo.InvariantCulture));
    }
    public static string Percent(double value)
    {
        if (!double.IsFinite(value) || value < 0) return "—";
        if (value is > 0 and < 1) return Small(value);
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded < 9223372036854775808d ? ((long)rounded).ToString(CultureInfo.InvariantCulture) : "—";
    }
    private static string Small(double value)
    {
        if (value <= 0) return "0";
        var tenths = Math.Round(value * 10, MidpointRounding.AwayFromZero) / 10;
        return tenths < 0.1 ? "<0.1" : tenths > 99.9 ? ">99.9" : tenths.ToString("0.0", CultureInfo.InvariantCulture);
    }
    public static string Summary(LimitWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.DisplayValue is { } display) return display;
        if (window.UsedPercent is { } used)
        {
            var halves = Halves(used);
            return halves.Used + "% Used · " + halves.Left + "% left";
        }
        return window.RemainingCount is { } remaining ? Compact(remaining) + " left"
            : window.UsedCount is { } count ? Compact(count) + " used" : "No reading";
    }
    public static string Summary(LimitWindow window, TokenNumberStyle style)
    {
        ArgumentNullException.ThrowIfNull(window);
        // The tooltip uses the user's count preference. Settings' summary keeps
        // the reference's compact counts; percentages and vendor text stay separate.
        if (window.UsedPercent is not null) return Summary(window);
        return window.RemainingCount is { } remaining ? TokenFormatter.Format(remaining, style) + " left"
            : window.UsedCount is { } count ? TokenFormatter.Format(count, style) + " used" : Summary(window);
    }
    private static string Compact(long count) => count < 10000 ? count.ToString(CultureInfo.InvariantCulture)
        : count < 1000000 ? (count / 1000).ToString(CultureInfo.InvariantCulture) + "k"
        : (count / 1000000d).ToString("0.0", CultureInfo.InvariantCulture) + "M";
}
