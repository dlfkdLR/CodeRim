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
    public const long HighContextInputThreshold = TokenUsage.HighContextInputThreshold;
    public const string CatalogVersion = "2026-09-14";
    private static readonly Dictionary<string, ModelPrice> Prices = ProviderCatalog.ReadResource<ModelPrice[]>("pricing.json").ToDictionary(x => x.Model, StringComparer.Ordinal);
    public static CostSummary Estimate(IEnumerable<UsageEvent> events, IReadOnlySet<string>? excludingModels = null)
    {
        // A derived List may reimplement IEnumerable and yield rows even with an empty backing list.
        if (events is UsageEvent[] { Length: 0 }
            || events is List<UsageEvent> { Count: 0 } list && list.GetType() == typeof(List<UsageEvent>))
            return new CostSummary(0m, 0, []);
        decimal amount = 0;
        long excluded = 0;
        var models = new HashSet<string>(StringComparer.Ordinal);
        var any = false;
        foreach (var group in events.GroupBy(item => item.Model, StringComparer.Ordinal))
        {
#if CODERIM_RUST_ACCOUNTING
            if (group is ICollection<UsageEvent> { Count: >= 32 })
            {
                var summary = RustModelCost.Summarize(group);
                var total = summary.Total.ToUsage();
                var validRows = !summary.Flags.HasFlag(RustModelCost.Status.InvalidUsage) && total.IsValid;
                if (validRows && total.IsZero) continue;
                if (!validRows || !CoveredPrice(group.Key, total, excludingModels, out var nativePrice)
                    || nativePrice.HighContextInputMultiplier is not null && summary.Flags.HasFlag(RustModelCost.Status.UnknownContext))
                {
                    Exclude(group.Key, total, models, ref excluded);
                    continue;
                }
                any = true;
                bool firstHigh = summary.Flags.HasFlag(RustModelCost.Status.FirstHigh);
                if (firstHigh) amount += TierCost(summary.High.ToUsage(), nativePrice, true);
                if (summary.Flags.HasFlag(RustModelCost.Status.Standard)) amount += TierCost(summary.Standard.ToUsage(), nativePrice, false);
                if (!firstHigh && summary.Flags.HasFlag(RustModelCost.Status.High)) amount += TierCost(summary.High.ToUsage(), nativePrice, true);
                continue;
            }
#endif
            var usage = group.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage));
            var valid = usage.IsValid && group.All(item => item.Usage.IsValid);
            if (valid && usage.IsZero) continue;
            // Unknown large-request context remains a gap; a known request uses
            // its own pricing tier. Coverage stays model-wide for the range.
            if (!valid || !CoveredPrice(group.Key, usage, excludingModels, out var price)
                || (price.HighContextInputMultiplier is not null && group.Any(item =>
                    item.PricingContext is not (null or PricingContext.Standard or PricingContext.HighContext)
                    || item.PricingContext is null && item.Usage.InputTokens > HighContextInputThreshold)))
            {
                Exclude(group.Key, usage, models, ref excluded);
                continue;
            }
            any = true;
            foreach (var tier in group.GroupBy(item => item.PricingContext == PricingContext.HighContext))
            {
                var tierUsage = tier.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage));
                amount += TierCost(tierUsage, price, tier.Key);
            }
        }
        return new CostSummary(any || models.Count == 0 ? amount : null, excluded, models.Order(StringComparer.Ordinal).ToArray());
    }
    private static bool CoveredPrice(string name, TokenUsage usage, IReadOnlySet<string>? excludingModels,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ModelPrice? price)
    {
        price = null;
        return excludingModels?.Contains(name) != true && Prices.TryGetValue(name == "gpt-5.6" ? "gpt-5.6-sol" : name, out price)
            && (price.CacheWriteInputMultiplier is null || usage.CacheWriteInputTokens is not null || usage.InputTokens <= 0);
    }

    private static void Exclude(string name, TokenUsage usage, HashSet<string> models, ref long excluded)
    {
        excluded = new TokenUsage(excluded, 0, 0).Add(new TokenUsage(Math.Max(0, usage.TotalTokens), 0, 0)).InputTokens;
        models.Add(name);
    }

    private static decimal TierCost(TokenUsage usage, ModelPrice price, bool high)
    {
        var inputMultiplier = high ? price.HighContextInputMultiplier ?? 1 : 1;
        var outputMultiplier = high ? price.HighContextOutputMultiplier ?? 1 : 1;
        return ((usage.UncachedInputTokens * price.InputUSDPerMillionTokens
            + usage.CachedInputTokens * price.CachedInputUSDPerMillionTokens
            + (usage.CacheWriteInputTokens ?? 0) * price.InputUSDPerMillionTokens * (price.CacheWriteInputMultiplier ?? 1)) * inputMultiplier
            + usage.OutputTokens * price.OutputUSDPerMillionTokens * outputMultiplier) / 1_000_000m;
    }

    public static IReadOnlyList<AnalyticsRow> Group(IEnumerable<UsageEvent> events, string dimension) => events
        .GroupBy(x => dimension switch { "project" => x.ProjectId, "session" => x.SessionId[..Math.Min(12, x.SessionId.Length)], "day" => x.OccurredAt.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), _ => x.Model })
        .Select(group => { var cost = Estimate(group); return new AnalyticsRow(dimension == "project" ? group.First().Project + " · " + group.Key[..Math.Min(6, group.Key.Length)] : group.Key, group.Aggregate(TokenUsage.Zero, (sum, x) => sum.Add(x.Usage)).TotalTokens, cost.Amount, cost.IsPartial); })
        .OrderByDescending(x => x.Tokens).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
}
