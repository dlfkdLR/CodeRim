using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

public sealed record CostSummary(decimal? Amount, long ExcludedTokens, IReadOnlyList<string> ExcludedModels)
{
    public bool IsPartial => ExcludedTokens > 0;
    public string Label => IsPartial ? "Estimated API cost subtotal" : "Estimated API cost";
}
public sealed record ModelPrice(string Model, decimal InputUSDPerMillionTokens, decimal CachedInputUSDPerMillionTokens,
    decimal OutputUSDPerMillionTokens, decimal? CacheWriteInputMultiplier, decimal? HighContextInputMultiplier, decimal? HighContextOutputMultiplier);
public sealed record AnalyticsRow(string Name, long Tokens, decimal? Cost, bool Partial);
public static class UsageAnalytics
{
    private static readonly Dictionary<string, ModelPrice> Prices = ProviderCatalog.ReadResource<ModelPrice[]>("pricing.json").ToDictionary(x => x.Model, StringComparer.Ordinal);
    public static CostSummary Estimate(IEnumerable<UsageEvent> events)
    {
        decimal amount = 0;
        long excluded = 0;
        var models = new HashSet<string>(StringComparer.Ordinal);
        var any = false;
        foreach (var item in events)
        {
            var model = item.Model == "gpt-5.6" ? "gpt-5.6-sol" : item.Model;
            // Until request context is unambiguous, large context usage is a gap, never a cheap estimate.
            if (!item.Usage.IsValid || !Prices.TryGetValue(model, out var price)
                || (price.CacheWriteInputMultiplier is not null && item.Usage.CacheWriteInputTokens is null)
                || (price.HighContextInputMultiplier is not null && item.Usage.InputTokens > 272_000))
            {
                excluded = new TokenUsage(excluded, 0, 0).Add(new TokenUsage(item.Usage.TotalTokens, 0, 0)).InputTokens;
                models.Add(item.Model);
                continue;
            }
            any = true;
            amount += (item.Usage.UncachedInputTokens * price.InputUSDPerMillionTokens
                + item.Usage.CachedInputTokens * price.CachedInputUSDPerMillionTokens
                + (item.Usage.CacheWriteInputTokens ?? 0) * price.InputUSDPerMillionTokens * (price.CacheWriteInputMultiplier ?? 1)
                + item.Usage.OutputTokens * price.OutputUSDPerMillionTokens) / 1_000_000m;
        }
        return new CostSummary(any ? amount : null, excluded, models.Order(StringComparer.Ordinal).ToArray());
    }
    public static IReadOnlyList<AnalyticsRow> Group(IEnumerable<UsageEvent> events, string dimension) => events
        .GroupBy(x => dimension switch { "project" => x.ProjectId, "session" => x.SessionId[..Math.Min(12, x.SessionId.Length)], "day" => x.OccurredAt.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), _ => x.Model })
        .Select(group => { var cost = Estimate(group); return new AnalyticsRow(dimension == "project" ? group.First().Project + " · " + group.Key[..Math.Min(6, group.Key.Length)] : group.Key, group.Aggregate(TokenUsage.Zero, (sum, x) => sum.Add(x.Usage)).TotalTokens, cost.Amount, cost.IsPartial); })
        .OrderByDescending(x => x.Tokens).ToArray();
}
