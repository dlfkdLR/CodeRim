using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static JsonElement NotionRecord(JsonElement record)
    {
        for (var depth = 0; depth < 2 && Get(record, "value").ValueKind == JsonValueKind.Object; depth++) record = Get(record, "value");
        return record;
    }
    private static (string Id, string User, string? Plan) NotionWorkspace(JsonElement root, string? preferred)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Missing Notion workspaces.");
        var identified = root.EnumerateObject().Where(x => Text(NotionRecord(Get(Get(x.Value, "notion_user"), x.Name)), "id") == x.Name).ToArray();
        var all = root.EnumerateObject().ToArray();
        var account = identified.Length == 1 ? identified[0] : identified.Length == 0 && all.Length == 1 ? all[0] : throw new InvalidDataException("Notion account is ambiguous.");
        if (account.Name.Length > 256 || account.Name.Any(char.IsControl)) throw new InvalidDataException("Invalid Notion user ID.");
        var spaces = Get(account.Value, "space"); if (spaces.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Missing Notion workspace.");
        var options = spaces.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => (Id: Text(NotionRecord(x.Value), "id") ?? x.Name, Record: NotionRecord(x.Value))).ToArray();
        if (options.Length == 0) throw new InvalidDataException("No Notion workspace is available.");
        static string Normalize(string value) => value.Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var index = string.IsNullOrWhiteSpace(preferred) ? -1 : Array.FindIndex(options, x => Normalize(x.Id) == Normalize(preferred));
        // Never silently present another workspace under an explicit selection.
        if (!string.IsNullOrWhiteSpace(preferred) && index < 0) throw new InvalidDataException("The selected workspace is unavailable for this account.");
        if (index < 0) index = Array.FindIndex(options, x => Text(x.Record, "subscription_tier")?.ToLowerInvariant() is "business" or "enterprise");
        if (index < 0) index = 0;
        var selected = options[index]; var tier = Text(selected.Record, "subscription_tier"); var name = Text(selected.Record, "name");
        return (selected.Id, account.Name, name is null ? tier : tier is null ? name : name + " · " + tier);
    }
    private static ProviderReading ParseNotion(JsonElement root)
    {
        if (Text(root, "status")?.Equals("not_applicable", StringComparison.OrdinalIgnoreCase) == true)
            return new("notion", ReadingState.Ready, [], DateTimeOffset.UtcNow, Message: "This workspace plan has no AI allowance to report.");
        var windows = new List<LimitWindow>(); var rolling = Get(root, "window"); var billing = Get(root, "billingPeriodWindow");
        void Add(JsonElement value, string key, string label, DateTimeOffset? reset, int duration)
        {
            var used = Numeric(value, "used"); var limit = Numeric(value, "limit");
            if (used is not >= 0 || limit is not > 0) return;
            var scope = Text(value, "scope");
            windows.Add(new(key, label + (string.IsNullOrWhiteSpace(scope) ? "" : " · " + scope), Math.Clamp(used.Value / limit.Value * 100, 0, 100), reset, duration,
                Unit: "credits", DisplayValue: $"{used:N2} / {limit:N2} credits"));
        }
        var seconds = Numeric(root, "resetsInSeconds");
        var reset = seconds is >= 0 and < 315360000 ? DateTimeOffset.UtcNow.AddSeconds(seconds.Value) : (DateTimeOffset?)null;
        var period = Text(rolling, "window"); var minutes = 0;
        if (period is { Length: > 1 and <= 16 } && int.TryParse(period[..^1], out var size) && size is > 0 and <= 100000)
        {
            var multiplier = char.ToLowerInvariant(period[^1]) switch { 'm' => 1, 'h' => 60, 'd' => 1440, 'w' => 10080, _ => 0 };
            minutes = size * multiplier;
        }
        Add(rolling, "rolling", period ?? "Rolling allowance", reset, minutes);
        Add(billing, "billing", "Billing period", EpochDate(billing, "periodEndMs"), 0);
        return Metered("notion", windows);
    }
}
