using System.Globalization;
using System.Runtime.InteropServices;

namespace CodeRim.Windows.Services;

internal enum AnalyticsDateStyle { Day, Time, DayAndTime }

/// <summary>ICU medium-date/short-time styles used by the Mac analytics reference.</summary>
internal static class AnalyticsDateText
{
    internal static string Format(DateTimeOffset date, AnalyticsDateStyle style, CultureInfo? culture = null, TimeZoneInfo? timeZone = null)
    {
        culture ??= CultureInfo.CurrentCulture; timeZone ??= TimeZoneInfo.Local;
        return TryFormat(date, style, culture, timeZone) ?? Fallback(date, style, culture, timeZone);
    }

    internal static string? TryFormat(DateTimeOffset date, AnalyticsDateStyle style, CultureInfo culture, TimeZoneInfo timeZone)
    {
        if (HasFormattingOverrides(culture, style)) return null;
        var (timeStyle, dateStyle) = style switch
        {
            AnalyticsDateStyle.Day => (-1, 2),
            AnalyticsDateStyle.Time => (3, -1),
            AnalyticsDateStyle.DayAndTime => (3, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(style))
        };
        // Supply the offset at this instant, rather than ICU's cached default
        // time zone. This also preserves the two sides of a daylight-saving fold.
        var offset = timeZone.GetUtcOffset(date);
        var zone = "GMT" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        var locale = culture.Name.Length == 0 ? "en_US_POSIX" : culture.Name;
        try
        {
            var status = 0;
            var formatter = Open(timeStyle, dateStyle, System.Text.Encoding.UTF8.GetBytes(locale + "\0"), zone, zone.Length, IntPtr.Zero, 0, ref status);
            if (formatter == IntPtr.Zero) return null;
            try
            {
                if (status > 0) return null; // ICU warnings are negative; errors positive.
                status = 0;
                var buffer = new char[256];
                var length = Render(formatter, date.ToUnixTimeMilliseconds(), buffer, buffer.Length, IntPtr.Zero, ref status);
                return status <= 0 && length > 0 && length < buffer.Length ? new string(buffer, 0, length) : null;
            }
            finally { Close(formatter); }
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static bool HasFormattingOverrides(CultureInfo culture, AnalyticsDateStyle style)
    {
        DateTimeFormatInfo standard;
        try { standard = CultureInfo.GetCultureInfo(culture.Name).DateTimeFormat; }
        catch (CultureNotFoundException) { return true; }
        var current = culture.DateTimeFormat;
        if (style != AnalyticsDateStyle.Time && (current.ShortDatePattern != standard.ShortDatePattern
            || current.DateSeparator != standard.DateSeparator || current.Calendar.GetType() != standard.Calendar.GetType()
            || current.Calendar is HijriCalendar hijri && standard.Calendar is HijriCalendar baseHijri && hijri.HijriAdjustment != baseHijri.HijriAdjustment)) return true;
        return style != AnalyticsDateStyle.Day && (current.ShortTimePattern != standard.ShortTimePattern
            || current.TimeSeparator != standard.TimeSeparator || current.AMDesignator != standard.AMDesignator || current.PMDesignator != standard.PMDesignator);
    }

    internal static string Fallback(DateTimeOffset date, AnalyticsDateStyle style, CultureInfo culture, TimeZoneInfo timeZone)
    {
        try
        {
            var local = TimeZoneInfo.ConvertTime(date, timeZone);
            return local.ToString(style switch { AnalyticsDateStyle.Day => "d", AnalyticsDateStyle.Time => "t", _ => "g" }, culture);
        }
        catch (ArgumentOutOfRangeException) { return "Date unavailable"; }
        catch (FormatException) { return "Date unavailable"; }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("icu.dll", EntryPoint = "udat_open", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern IntPtr Open(int timeStyle, int dateStyle, [In] byte[] locale,
        string timeZone, int timeZoneLength, IntPtr pattern, int patternLength, ref int status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("icu.dll", EntryPoint = "udat_format", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Render(IntPtr formatter, double date,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 3)] char[] buffer,
        int capacity, IntPtr position, ref int status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("icu.dll", EntryPoint = "udat_close", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Close(IntPtr formatter);
}
