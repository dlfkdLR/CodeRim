using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

public sealed record CostSummary(decimal? Amount, long ExcludedTokens, IReadOnlyList<string> ExcludedModels)
{
    public bool IsPartial => ExcludedModels.Count > 0;
    public string Label => IsPartial ? "Estimated API cost subtotal" : "Estimated API cost";
}
public sealed record ModelPrice(string Model, decimal InputUSDPerMillionTokens, decimal CachedInputUSDPerMillionTokens,
    decimal OutputUSDPerMillionTokens, decimal? CacheWriteInputMultiplier, decimal? HighContextInputMultiplier, decimal? HighContextOutputMultiplier);
public sealed record AnalyticsRow(string Name, long Tokens, decimal? Cost, bool Partial);
public static class UsageAnalytics
{
    public const string CatalogVersion = "2026-09-14";
    private static readonly Dictionary<string, ModelPrice> Prices = ProviderCatalog.ReadResource<ModelPrice[]>("pricing.json").ToDictionary(x => x.Model, StringComparer.Ordinal);
    public static CostSummary Estimate(IEnumerable<UsageEvent> events, IReadOnlySet<string>? excludingModels = null)
    {
        decimal amount = 0;
        long excluded = 0;
        var models = new HashSet<string>(StringComparer.Ordinal);
        var any = false;
        foreach (var group in events.GroupBy(item => item.Model, StringComparer.Ordinal))
        {
            var usage = group.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage));
            var valid = usage.IsValid && group.All(item => item.Usage.IsValid);
            if (valid && usage.IsZero) continue;
            var model = group.Key == "gpt-5.6" ? "gpt-5.6-sol" : group.Key;
            // Until request context is unambiguous, large context usage is a gap, never a cheap estimate.
            // Coverage is model-wide, matching the displayed range subtotal.
            if (!valid || excludingModels?.Contains(group.Key) == true || !Prices.TryGetValue(model, out var price)
                || (price.CacheWriteInputMultiplier is not null && usage.CacheWriteInputTokens is null && usage.InputTokens > 0)
                || (price.HighContextInputMultiplier is not null && group.Any(item => item.Usage.InputTokens > 272_000)))
            {
                excluded = new TokenUsage(excluded, 0, 0).Add(new TokenUsage(Math.Max(0, usage.TotalTokens), 0, 0)).InputTokens;
                models.Add(group.Key);
                continue;
            }
            any = true;
            amount += (usage.UncachedInputTokens * price.InputUSDPerMillionTokens
                + usage.CachedInputTokens * price.CachedInputUSDPerMillionTokens
                + (usage.CacheWriteInputTokens ?? 0) * price.InputUSDPerMillionTokens * (price.CacheWriteInputMultiplier ?? 1)
                + usage.OutputTokens * price.OutputUSDPerMillionTokens) / 1_000_000m;
        }
        return new CostSummary(any || models.Count == 0 ? amount : null, excluded, models.Order(StringComparer.Ordinal).ToArray());
    }
    public static IReadOnlyList<AnalyticsRow> Group(IEnumerable<UsageEvent> events, string dimension) => events
        .GroupBy(x => dimension switch { "project" => x.ProjectId, "session" => x.SessionId[..Math.Min(12, x.SessionId.Length)], "day" => x.OccurredAt.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), _ => x.Model })
        .Select(group => { var cost = Estimate(group); return new AnalyticsRow(dimension == "project" ? group.First().Project + " · " + group.Key[..Math.Min(6, group.Key.Length)] : group.Key, group.Aggregate(TokenUsage.Zero, (sum, x) => sum.Add(x.Usage)).TotalTokens, cost.Amount, cost.IsPartial); })
        .OrderByDescending(x => x.Tokens).ToArray();
}
