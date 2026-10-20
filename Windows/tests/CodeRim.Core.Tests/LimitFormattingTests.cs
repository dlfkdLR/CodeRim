using System.Globalization;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class LimitFormattingTests
{
    [Theory]
    [InlineData(0, "0", "100")]
    [InlineData(0.01, "<0.1", ">99.9")]
    [InlineData(0.06, "0.1", ">99.9")]
    [InlineData(0.3, "0.3", "99.7")]
    [InlineData(0.96, "1.0", "99.0")]
    [InlineData(9.5, "10", "90")]
    [InlineData(32.6, "33", "67")]
    [InlineData(99.7, "99.7", "0.3")]
    [InlineData(99.99, ">99.9", "<0.1")]
    [InlineData(100, "100", "0")]
    [InlineData(150, "150", "0")]
    [InlineData(-1, "—", "—")]
    [InlineData(double.NaN, "—", "—")]
    [InlineData(double.PositiveInfinity, "—", "—")]
    [InlineData(double.MaxValue, "—", "—")]
    public void PercentageHalvesPreserveSmallAndExceededValues(double value, string used, string left)
    {
        Assert.Equal((used, left), LimitFormatting.Halves(value));
        Assert.Equal(used + "% Used · " + left + "% left", LimitFormatting.Summary(new("test", "Test", value)));
    }
    [Theory]
    [InlineData(0.01, "<0.1")]
    [InlineData(0.06, "0.1")]
    [InlineData(99.7, "100")]
    [InlineData(150, "150")]
    [InlineData(double.NaN, "—")]
    public void RingLabelsKeepTheReferencesSingleNumberRule(double value, string expected) => Assert.Equal(expected, LimitFormatting.Percent(value));

    [Fact]
    public void VendorTextAndCountersDoNotInventPercentages()
    {
        Assert.Equal("12 USD", LimitFormatting.Summary(new("money", "Balance", 40, DisplayValue: "12 USD")));
        Assert.Equal("1 left", LimitFormatting.Summary(new("remaining", "Requests", RemainingCount: 1)));
        Assert.Equal("12k used", LimitFormatting.Summary(new("used", "Requests", UsedCount: 12345)));
        Assert.Equal("1.2M left", LimitFormatting.Summary(new("tokens", "Tokens", RemainingCount: 1200000)));
        Assert.Equal("No reading", LimitFormatting.Summary(new("unknown", "Unknown")));
    }
    [Fact]
    public void TooltipCountsRespectNumberStyleWithoutChangingSettingsSummaries()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var remaining = new LimitWindow("remaining", "Requests", RemainingCount: 12345, DisplayValue: "Vendor count");
            var used = new LimitWindow("used", "Requests", UsedCount: 12345);
            Assert.Equal("12,345 left", LimitFormatting.Summary(remaining, TokenNumberStyle.Detailed));
            Assert.Equal("12,345 used", LimitFormatting.Summary(used, TokenNumberStyle.Detailed));
            Assert.Equal(TokenFormatter.Format(12345, TokenNumberStyle.Compact) + " left", LimitFormatting.Summary(remaining, TokenNumberStyle.Compact));
            Assert.Equal("Vendor count", LimitFormatting.Summary(remaining));
            Assert.Equal("12k used", LimitFormatting.Summary(used));
            Assert.Equal("12 USD", LimitFormatting.Summary(new("money", "Balance", 40, DisplayValue: "12 USD"), TokenNumberStyle.Detailed));
            Assert.Equal("No reading", LimitFormatting.Summary(new("unknown", "Unknown"), TokenNumberStyle.Detailed));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
    [Fact]
    public void FractionalPercentagesDoNotChangeDecimalSeparatorWithSystemCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); Assert.Equal(("0.3", "99.7"), LimitFormatting.Halves(0.3)); }
        finally { CultureInfo.CurrentCulture = before; }
    }
}
