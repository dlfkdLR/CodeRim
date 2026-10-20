using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static string KiroEndpoint(string? profile)
    {
        if (profile is not { Length: > 0 and <= 1024 } || profile.Any(x => char.IsControl(x) || char.IsWhiteSpace(x))) throw new InvalidDataException("Set the Kiro profile ARN.");
        var parts = profile.Split(':', 6);
        if (parts.Length != 6 || parts[0] != "arn" || parts[1] != "aws" || parts[2] != "codewhisperer"
            || !parts[5].StartsWith("profile/", StringComparison.Ordinal) || parts[5].Length <= 8) throw new InvalidDataException("Invalid Kiro profile ARN.");
        return parts[3] switch { "us-east-1" => "https://codewhisperer.us-east-1.amazonaws.com/",
            "eu-central-1" => "https://q.eu-central-1.amazonaws.com/", _ => throw new InvalidDataException("Unsupported Kiro profile region.") };
    }
    private static ProviderReading ParseKiro(JsonElement root)
    {
        var rows = Get(root, "usageBreakdownList");
        var credits = rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray().Where(x => Text(x, "resourceType") == "CREDIT").ToArray() : [];
        if (credits.Length != 1) throw new InvalidDataException("Kiro did not report a single credit balance.");
        var row = credits[0]; var total = Numeric(row, "currentUsageWithPrecision"); var maximum = Numeric(row, "usageLimitWithPrecision");
        var overage = Numeric(row, "currentOveragesWithPrecision") ?? 0; var bonus = Get(row, "bonuses");
        var hasBonus = bonus.ValueKind == JsonValueKind.Array && bonus.GetArrayLength() > 0;
        if (total is not >= 0 || maximum is not >= 0 || overage < 0 || total < overage || !hasBonus && total - overage > maximum)
            throw new InvalidDataException("Kiro returned inconsistent credit amounts.");
        var reset = Numeric(row, "nextDateReset") ?? Numeric(root, "nextDateReset");
        if (reset is not >= 1000000000 or > 4102444800) throw new InvalidDataException("Missing Kiro billing reset.");
        var end = DateTimeOffset.FromUnixTimeSeconds((long)reset.Value); var used = total!.Value - overage;
        var windows = new List<LimitWindow>
        {
            new("credits", hasBonus ? "Plan and bonus credits" : "Plan credits",
                !hasBonus && maximum > 0 ? used / maximum * 100 : null, end, Unit: "credits",
                DisplayValue: hasBonus ? $"{used:N2} used · {maximum:N2} plan allowance" : $"{used:N2} / {maximum:N2} credits")
        };
        var status = Text(Get(root, "overageConfiguration"), "overageStatus");
        var cap = status == "ENABLED" ? Numeric(row, "overageCapWithPrecision") : null;
        if (cap < 0) throw new InvalidDataException("Invalid Kiro overage cap.");
        if (cap.HasValue || overage > 0)
            windows.Add(new("overage", "Overage credits", cap > 0 ? Math.Clamp(overage / cap.Value * 100, 0, 100) : null, end,
                Unit: "credits", DisplayValue: cap.HasValue ? $"{overage:N2} / {cap:N2} credits" : $"{overage:N2} credits used"));
        if (Numeric(row, "overageCharges") is >= 0 and var charge)
            windows.Add(new("charges", "Overage charges", null, end, Unit: Text(row, "currency") ?? "USD", DisplayValue: $"{charge:N2} {Text(row, "currency") ?? "USD"}"));
        return Metered("kiro", windows);
    }
}
