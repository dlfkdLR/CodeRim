using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class CostCoverageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 30, 0, TimeSpan.Zero);
    private static UsageEvent Event(string id, TokenUsage usage, string model = "gpt-5.6-sol", int daysAgo = 0) =>
        new(id, Now.AddDays(-daysAgo), usage, model);

    [Fact]
    public void MeasuredEmptyRangeHasZeroCostWithoutMissingMetadata()
    {
        var cost = UsageAnalytics.Estimate([]);
        Assert.Equal(0m, cost.Amount); Assert.False(cost.IsPartial); Assert.Empty(cost.ExcludedModels);
    }

    [Fact]
    public void ZeroUnknownModelDoesNotInvalidateMeasuredZero()
    {
        var cost = UsageAnalytics.Estimate([Event("zero", TokenUsage.Zero, "unpriced")]);
        Assert.Equal(0m, cost.Amount); Assert.Empty(cost.ExcludedModels);
    }

    [Fact]
    public void OutputOnlyUsageDoesNotRequireInputCacheMetadata()
    {
        var cost = UsageAnalytics.Estimate([Event("output", new(0, 0, 1000))]);
        Assert.Equal(0.02m, cost.Amount); Assert.False(cost.IsPartial);
    }

    [Fact]
    public void IncompleteModelIsExcludedAsAWholeWhileOtherModelsKeepTheirSubtotal()
    {
        var cost = UsageAnalytics.Estimate([
            Event("complete", new(1000, 0, 0, 0)), Event("missing", new(2000, 0, 0)),
            Event("other", new(1000, 0, 0, 0), "gpt-6-astra")]);
        Assert.Equal(0.01m, cost.Amount); Assert.True(cost.IsPartial);
        Assert.Equal(3000, cost.ExcludedTokens); Assert.Equal(["gpt-5.6-sol"], cost.ExcludedModels);
    }

    [Fact]
    public void ChartAndRangeUseTheSameModelCoverageAcrossDays()
    {
        UsageEvent[] events = [Event("complete", new(1000, 0, 0, 0), daysAgo: 1), Event("missing", new(2000, 0, 0)),
            Event("other", new(1000, 0, 0, 0), "gpt-6-astra")];
        var total = UsageAnalytics.Estimate(events);
        var buckets = AnalyticsTimeline.Build(events, AnalyticsRange.SevenDays, Now, TimeZoneInfo.Utc);
        Assert.Null(buckets[^2].Cost.Amount); Assert.Equal(1000, buckets[^2].Cost.ExcludedTokens);
        Assert.Equal(0.01m, buckets[^1].Cost.Amount); Assert.Equal(2000, buckets[^1].Cost.ExcludedTokens);
        Assert.Equal(total.Amount, buckets.Sum(x => x.Cost.Amount ?? 0));
        Assert.All(buckets.Take(5), x => { Assert.Equal(0m, x.Cost.Amount); Assert.False(x.Cost.IsPartial); });
    }

    [Fact]
    public void OutsideRangeMissingMetadataDoesNotExcludeCurrentModel()
    {
        var buckets = AnalyticsTimeline.Build([Event("outside", new(100, 0, 0), daysAgo: 7), Event("inside", new(1000, 0, 0, 0))],
            AnalyticsRange.SevenDays, Now, TimeZoneInfo.Utc);
        Assert.Equal(0.004m, buckets[^1].Cost.Amount); Assert.False(buckets[^1].Cost.IsPartial);
    }

    [Fact]
    public void InvalidOrUnknownHighContextMetadataCannotBecomeACompleteEstimate()
    {
        var invalid = UsageAnalytics.Estimate([Event("valid", new(100, 0, 0, 0)), Event("invalid", new(100, 200, 0, 0))]);
        Assert.Null(invalid.Amount); Assert.True(invalid.IsPartial);
        var high = UsageAnalytics.Estimate([Event("normal", new(100, 0, 0, 0)), Event("high", new(300000, 0, 0, 0))]);
        Assert.Null(high.Amount); Assert.Equal(300100, high.ExcludedTokens);
    }

    [Fact]
    public void SummedOrdinaryRequestsDoNotBecomeUnknownHighContextUsage()
    {
        var cost = UsageAnalytics.Estimate([Event("first", new(200000, 0, 0, 0)), Event("second", new(200000, 0, 0, 0))]);
        Assert.Equal(1.6m, cost.Amount); Assert.False(cost.IsPartial);
    }

    [Fact]
    public void AggregateCacheWriteUncertaintyMatchesTheModelSummary()
    {
        var cost = UsageAnalytics.Estimate([Event("input", new(1000, 0, 0, 0)), Event("output", new(0, 0, 1000))]);
        Assert.Null(cost.Amount); Assert.Equal(2000, cost.ExcludedTokens);
    }

    [Fact]
    public void ExplicitCoverageExclusionsDoNotInventMissingUsageInEmptyBuckets()
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal) { "gpt-5.6-sol" };
        var missing = UsageAnalytics.Estimate([Event("measured", new(100, 0, 0, 0))], excluded);
        Assert.Null(missing.Amount); Assert.Equal(100, missing.ExcludedTokens);
        var empty = UsageAnalytics.Estimate([], excluded);
        Assert.Equal(0m, empty.Amount); Assert.False(empty.IsPartial);
    }

    [Fact]
    public void ExcludedTokenTotalsSaturateWithoutWrappingNegative()
    {
        var cost = UsageAnalytics.Estimate([Event("first", new(long.MaxValue, 0, 1, 0), "unpriced-one"),
            Event("second", new(long.MaxValue, 0, 1, 0), "unpriced-two")]);
        Assert.Null(cost.Amount); Assert.Equal(long.MaxValue, cost.ExcludedTokens);
        Assert.Equal(2, cost.ExcludedModels.Count);
    }
}
