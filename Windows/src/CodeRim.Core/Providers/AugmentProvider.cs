using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static ProviderReading ParseAugment(JsonElement credits, JsonElement subscription)
    {
        var used = Numeric(credits, "usageUnitsConsumedThisBillingCycle"); var remaining = Numeric(credits, "usageUnitsRemaining");
        var limit = Numeric(credits, "usageUnitsAvailable");
        if (limit is not > 0 && used is >= 0 && remaining is >= 0) limit = used + remaining;
        if (limit.HasValue && !double.IsFinite(limit.Value)) throw new InvalidDataException("Invalid Augment allowance.");
        if (!used.HasValue && limit is > 0 && remaining is >= 0) used = Math.Max(0, limit.Value - remaining.Value);
        if (used is not >= 0 || limit is not > 0 || remaining is < 0) throw new InvalidDataException("Missing Augment credit balance.");
        return Metered("augment", [new("credits", "Credits", Math.Clamp(used.Value / limit.Value * 100, 0, 100),
            EpochDate(subscription, "billingPeriodEnd"), Unit: "credits", DisplayValue: $"{used:N0} / {limit:N0} credits" + (remaining.HasValue ? $" · {remaining:N0} remaining" : ""))],
            Text(subscription, "planName"));
    }
}
