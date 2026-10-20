using System.Globalization;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Tests;
public sealed class ResetCopyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    [Theory]
    [InlineData(3040, "Absolute", "Resets in 51 min")]
    [InlineData(3590, "Relative", "Resets in 1h 0m")]
    [InlineData(86400, "Relative", "Resets in 1 Day 0h")]
    [InlineData(2246400, "Absolute", "Resets Oct 15")]
    [InlineData(518400, "Absolute", "Resets Fri 12:00 PM")]
    [InlineData(0, "Absolute", "Resetting…")]
    public void UsesMacRoundingAndCalendarCopy(int seconds, string format, string expected) =>
        Assert.Equal(expected, ResetCopy.Text(Now.AddSeconds(seconds), format, Now, TimeZoneInfo.Utc, CultureInfo.InvariantCulture));

    [Fact]
    public void DateBoundaryUsesDisplayTimeZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Fixture", TimeSpan.FromHours(9), "Fixture", "Fixture");
        var now = Now.AddHours(2);
        Assert.Equal("Resets Sep 26", ResetCopy.Text(now.AddDays(6).AddHours(2), "Absolute", now, zone, CultureInfo.InvariantCulture));
        Assert.Equal("", ResetCopy.Text(null, "Relative", Now));
    }
}
