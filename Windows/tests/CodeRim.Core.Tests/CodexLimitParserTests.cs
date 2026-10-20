using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class CodexLimitParserTests
{
    private static IReadOnlyList<LimitWindow> Parse(string json)
    {
        using var document = JsonDocument.Parse(json); return ProviderParsers.Codex(document.RootElement);
    }

    [Fact]
    public void RemainingOnlyQuotaIsReadAndExplicitUsedTakesPrecedence()
    {
        var windows = Parse("""{"rateLimits":{"primary":{"remainingPercent":80,"windowDurationMins":300},"secondary":{"usedPercent":50,"remainingPercent":80,"windowDurationMins":10080}}}""");
        Assert.Equal(2, windows.Count); Assert.Equal(20, windows[0].UsedPercent); Assert.Equal(50, windows[1].UsedPercent);
        Assert.Equal("codex.primary", windows[0].Id); Assert.Equal("codex.secondary", windows[1].Id);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(150, 100)]
    [InlineData(0.3, 0.3)]
    public void CodexAccountPercentagesFollowTheClampedReference(double used, double expected)
    {
        var payload = JsonSerializer.Serialize(new { rateLimits = new { primary = new { usedPercent = used, windowDurationMins = 300 } } });
        Assert.Equal(expected, Assert.Single(Parse(payload)).UsedPercent);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("299.5")]
    [InlineData("true")]
    [InlineData("\"300\"")]
    [InlineData("2147483648")]
    public void UnsupportedDurationsDoNotInventAccountWindows(string duration)
        => Assert.Empty(Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":20,\"windowDurationMins\":" + duration + "}}}"));

    [Fact]
    public void MissingDurationIsNotAZeroMinuteQuota()
        => Assert.Empty(Parse("""{"rateLimits":{"primary":{"usedPercent":20}}}"""));

    [Fact]
    public void RepeatedWindowIsDeduplicatedButDifferentResetsAndBucketsRemain()
    {
        var repeated = Parse("""{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":1900000000},"secondary":{"usedPercent":30,"windowDurationMins":300,"resetsAt":1900000000}}}""");
        Assert.Single(repeated); Assert.Equal(20, repeated[0].UsedPercent);
        var distinct = Parse("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":1900000000},"secondary":{"usedPercent":30,"windowDurationMins":300,"resetsAt":1900000010}},"other":{"primary":{"usedPercent":40,"windowDurationMins":300,"resetsAt":1900000000}}}}""");
        Assert.Equal(3, distinct.Count);
    }

    [Fact]
    public void ResetEpochUsesSecondsIncludingZeroAndLongRangeDates()
    {
        var windows = Parse("""{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":0},"secondary":{"usedPercent":30,"windowDurationMins":10080,"resetsAt":16725225600}}}""");
        Assert.Equal(DateTimeOffset.UnixEpoch, windows[0].ResetsAt);
        Assert.Equal(new DateTimeOffset(2500, 1, 1, 0, 0, 0, TimeSpan.Zero), windows[1].ResetsAt);
    }

    [Theory]
    [InlineData("-1", 0L)]
    [InlineData("3.0", 3L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void ResetCreditCountsRemainExactNonnegativeIntegers(string value, long expected)
    {
        var credit = Assert.Single(Parse("{\"rateLimitResetCredits\":{\"availableCount\":" + value + "}}"));
        Assert.Equal(expected, credit.RemainingCount); Assert.Null(credit.UsedPercent);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("9223372036854775808")]
    [InlineData("null")]
    public void InvalidCreditCountIsNotReplacedByALowerPriorityField(string value)
        => Assert.Empty(Parse("{\"rateLimitResetCredits\":{\"availableCount\":" + value + ",\"count\":3}}"));
}
