using System.Globalization;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static void AnalyticsDateRegression(string directory)
    {
        // Read-only macOS Foundation DateFormatter medium/short probe, UTC,
        // 2026-10-09 15:04:05. No format string is copied from the Windows code.
        var date = DateTimeOffset.FromUnixTimeSeconds(1791558245);
        var references = new (string Locale, string Day, string Timestamp)[] {
            ("en-US", "Oct 9, 2026", "Oct 9, 2026 at 3:04\u202fPM"),
            ("en-GB", "9 Oct 2026", "9 Oct 2026 at 15:04"),
            ("de-DE", "09.10.2026", "09.10.2026, 15:04"),
            ("fr-FR", "9 oct. 2026", "9 oct. 2026 à 15:04"),
            ("ko-KR", "2026. 10. 9.", "2026. 10. 9. 오후 3:04"),
            ("ja-JP", "2026/10/09", "2026/10/09 15:04"),
            ("zh-CN", "2026年10月9日", "2026年10月9日 15:04"),
            ("pl-PL", "9 paź 2026", "9 paź 2026 o 15:04")
        };
        var checks = new List<string>();
        foreach (var reference in references)
        {
            var culture = CultureInfo.GetCultureInfo(reference.Locale);
            var day = AnalyticsDateText.TryFormat(date, AnalyticsDateStyle.Day, culture, TimeZoneInfo.Utc);
            var timestamp = AnalyticsDateText.TryFormat(date, AnalyticsDateStyle.DayAndTime, culture, TimeZoneInfo.Utc);
            Require(day == reference.Day && timestamp == reference.Timestamp,
                "Native analytics date differs from the Foundation reference: " + reference.Locale + " / " + day + " / " + timestamp);
            Require(AnalyticsDateText.Format(date, AnalyticsDateStyle.DayAndTime, culture, TimeZoneInfo.Utc) == reference.Timestamp,
                "Analytics date display did not use its native formatter");
            checks.Add(reference.Locale + " medium date and short timestamp");
        }
        var us = CultureInfo.GetCultureInfo("en-US");
        Require(AnalyticsDateText.Format(date, AnalyticsDateStyle.Time, us, TimeZoneInfo.Utc) == "3:04\u202fPM", "Today interval did not use localized short time");
        var east = TimeZoneInfo.CreateCustomTimeZone("fixture-east", TimeSpan.FromHours(9), "fixture-east", "fixture-east");
        var west = TimeZoneInfo.CreateCustomTimeZone("fixture-west", TimeSpan.FromHours(-8), "fixture-west", "fixture-west");
        Require(AnalyticsDateText.Format(new(2026, 1, 1, 18, 4, 0, TimeSpan.Zero), AnalyticsDateStyle.DayAndTime, us, east) == "Jan 2, 2026 at 3:04\u202fAM"
            && AnalyticsDateText.Format(new(2026, 1, 1, 2, 4, 0, TimeSpan.Zero), AnalyticsDateStyle.DayAndTime, us, west) == "Dec 31, 2025 at 6:04\u202fPM",
            "Analytics dates ignored a local day/year rollover");
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        Require(AnalyticsDateText.Format(new(2026, 3, 8, 6, 30, 0, TimeSpan.Zero), AnalyticsDateStyle.Time, us, eastern) == "1:30\u202fAM"
            && AnalyticsDateText.Format(new(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), AnalyticsDateStyle.Time, us, eastern) == "3:30\u202fAM",
            "Analytics date formatter used a stale offset across daylight saving");
        Require(AnalyticsDateText.Fallback(DateTimeOffset.MinValue, AnalyticsDateStyle.DayAndTime, CultureInfo.GetCultureInfo("ar-SA"), TimeZoneInfo.Utc) == "Date unavailable",
            "Unsupported fallback calendar date did not remain safe");
        var custom = (CultureInfo)us.Clone(); custom.DateTimeFormat.ShortTimePattern = "HH:mm";
        Require(AnalyticsDateText.TryFormat(date, AnalyticsDateStyle.DayAndTime, custom, TimeZoneInfo.Utc) is null
            && AnalyticsDateText.Format(date, AnalyticsDateStyle.Time, custom, TimeZoneInfo.Utc) == "15:04", "A user's 24-hour override was discarded");
        custom.DateTimeFormat.ShortDatePattern = "yyyy-MM-dd";
        Require(AnalyticsDateText.Format(date, AnalyticsDateStyle.DayAndTime, custom, TimeZoneInfo.Utc) == "2026-10-09 15:04", "A user's date format override was discarded");
        var japanese = (CultureInfo)CultureInfo.GetCultureInfo("ja-JP").Clone(); japanese.DateTimeFormat.Calendar = new JapaneseCalendar();
        Require(AnalyticsDateText.TryFormat(date, AnalyticsDateStyle.Day, japanese, TimeZoneInfo.Utc) is null
            && AnalyticsDateText.Format(date, AnalyticsDateStyle.DayAndTime, japanese, TimeZoneInfo.Utc) == date.ToString("g", japanese), "A user's selected calendar was discarded");
        checks.AddRange(["Localized Today interval time", "Local day/year rollover", "Offset changes across daylight saving", "Unsupported fallback calendar date", "User time/date/calendar overrides preserved"]);
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "windows-analytics-dates.json"), System.Text.Json.JsonSerializer.Serialize(new { completed = true, checks,
            currentCulture = CultureInfo.CurrentCulture.Name,
            currentCultureUsesNative = AnalyticsDateText.TryFormat(date, AnalyticsDateStyle.DayAndTime, CultureInfo.CurrentCulture, TimeZoneInfo.Local) is not null }, JsonOptions));
    }
}
