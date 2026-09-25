using System.Globalization;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Tests;

public sealed class ResetCopyLocaleTests
{
    private static readonly DateTimeOffset Reset = DateTimeOffset.FromUnixTimeSeconds(1791586800);

    // Compared with Foundation DateFormatter's MMM d localized template on Mac.
    [Theory]
    [InlineData("en-US", "Oct 9")]
    [InlineData("en-GB", "9 Oct")]
    [InlineData("de-DE", "9. Okt.")]
    [InlineData("fr-FR", "9 oct.")]
    [InlineData("ko-KR", "10월 9일")]
    [InlineData("ja-JP", "10月9日")]
    [InlineData("zh-CN", "10月9日")]
    [InlineData("pl-PL", "9 paź")]
    public void DistantResetsUseLocalMonthDayOrder(string locale, string expected) =>
        Assert.Equal("Resets " + expected, ResetCopy.Text(Reset, "Absolute", Reset.AddDays(-14), TimeZoneInfo.Utc, CultureInfo.GetCultureInfo(locale)));

    [Fact]
    public void CustomDatePatternLiteralsAreNotRewritten()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.MonthDayPattern = "'dd MMMM' dd 'M' MMMM";
        Assert.Equal("Resets dd MMMM 9 M Oct", ResetCopy.Text(Reset, "Absolute", Reset.AddDays(-14), TimeZoneInfo.Utc, culture));
    }

    [Fact]
    public void EscapedDateTokensStayLiteral()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.MonthDayPattern = "\\d dd \\M MMMM";
        Assert.Equal("Resets d 9 M Oct", ResetCopy.Text(Reset, "Absolute", Reset.AddDays(-14), TimeZoneInfo.Utc, culture));
    }

    [Fact]
    public void PercentConsumesExactlyOneCustomToken()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.MonthDayPattern = "%MMMM dd";
        Assert.Equal("Resets 10Oct 9", ResetCopy.Text(Reset, "Absolute", Reset.AddDays(-14), TimeZoneInfo.Utc, culture));
    }

    [Theory]
    [InlineData("dd", "9")]
    [InlineData("d", "9")]
    [InlineData("M", "10")]
    public void SingleCustomTokenDoesNotBecomeAStandardFormat(string pattern, string expected)
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.MonthDayPattern = pattern;
        Assert.Equal("Resets " + expected, ResetCopy.Text(Reset, "Absolute", Reset.AddDays(-14), TimeZoneInfo.Utc, culture));
    }

    [Fact]
    public void MalformedUserDatePatternDoesNotCrashTheView()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.MonthDayPattern = "'unclosed";
        Assert.Equal("Reset time unavailable", ResetCopy.Text(Reset, "Absolute", Reset.AddDays(-14), TimeZoneInfo.Utc, culture));
    }

    [Fact]
    public void DateOutsideTheSelectedCalendarsRangeDoesNotCrashTheView()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("ar-SA").Clone();
        culture.DateTimeFormat.Calendar = new UmAlQuraCalendar();
        Assert.Equal("Reset time unavailable", ResetCopy.Text(DateTimeOffset.MaxValue, "Absolute", Reset, TimeZoneInfo.Utc, culture));
    }
}
