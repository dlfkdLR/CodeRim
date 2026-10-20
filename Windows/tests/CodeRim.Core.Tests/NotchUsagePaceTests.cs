using System.Globalization;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Tests;

public sealed class NotchUsagePaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(75, 150, "25% deficit", true)]
    [InlineData(25, 150, "25% reserved", false)]
    [InlineData(50, 150, "0% reserved", false)]
    [InlineData(150, 150, "50% deficit", true)]
    [InlineData(0, 301, "0% reserved", false)]
    [InlineData(0.02, 300, "<0.1% deficit", true)]
    [InlineData(49.98, 150, "<0.1% reserved", false)]
    [InlineData(0.3, 300, "0.3% deficit", true)]
    public void MatchesReferenceAcrossCycleAndQuotaBoundaries(double used, int remaining, string summary, bool deficit)
    {
        var result = NotchUsagePace.For(new("limit", "Limit", used, Now.AddMinutes(remaining), 300), Now);
        Assert.NotNull(result); Assert.Equal(summary, result.Summary); Assert.Equal(deficit, result.IsDeficit);
    }

    [Theory]
    [InlineData(null, 300, 150)]
    [InlineData(-1d, 300, 150)]
    [InlineData(double.NaN, 300, 150)]
    [InlineData(double.PositiveInfinity, 300, 150)]
    [InlineData(10d, 0, 150)]
    [InlineData(10d, -1, 150)]
    [InlineData(10d, 300, 0)]
    [InlineData(10d, 300, -1)]
    public void UnknownInvalidOrExpiredWindowsHaveNoPace(double? used, int duration, int remaining) =>
        Assert.Null(NotchUsagePace.For(new("limit", "Limit", used, Now.AddMinutes(remaining), duration), Now));

    [Fact]
    public void MissingResetHasNoPace() => Assert.Null(NotchUsagePace.For(new("limit", "Limit", 10, DurationMinutes: 300), Now));

    [Fact]
    public void CopyUsesReferenceDecimalPointRegardlessOfLocale()
    {
        var before = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); Assert.Equal("12.3% deficit", new NotchUsagePace(12.3).Summary); }
        finally { CultureInfo.CurrentCulture = before; }
    }
}
