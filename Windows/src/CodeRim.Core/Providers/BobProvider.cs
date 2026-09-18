using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public sealed partial class NativeProviders
{
    public static string BobRegion(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return "https://api.us-east.bob.ibm.com";
        var host = domain.StartsWith("api.", StringComparison.OrdinalIgnoreCase) ? domain : "api." + domain;
        if (host.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '-')) ||
            !host.EndsWith(".bob.ibm.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The IBM Bob regional endpoint is not trusted.");
        return "https://" + host;
    }
    private static string BobAuthorization(string credential)
    {
        try
        {
            var parts = credential.Split('.');
            if (parts.Length != 3) return "Apikey";
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')));
            return json.RootElement.ValueKind == JsonValueKind.Object ? "Bearer" : "Apikey";
        }
        catch (Exception error) when (error is FormatException or JsonException) { return "Apikey"; }
    }
    private static async Task<ProviderReading> FetchBob(Func<string, IReadOnlyDictionary<string, string>?, Task<JsonElement>> get)
    {
        var root = await get("https://api.us-east.bob.ibm.com/admin/v1/profile", null).ConfigureAwait(false);
        var instances = Get(root, "instances");
        if (instances.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var windows = new List<LimitWindow>(); var index = 0;
        static string Identifier(string? value)
        {
            if (value is not { Length: > 0 and <= 256 } || value is "." or ".." ||
                value.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_' or '.'))) throw new InvalidDataException();
            return value;
        }
        foreach (var instance in instances.EnumerateArray())
        {
            if (Text(instance, "user_id") is not { Length: > 0 } userId) continue;
            var user = Identifier(userId); var instanceId = Identifier(Text(instance, "instance_id"));
            var region = BobRegion(Text(instance, "region_domain")); var teams = Get(instance, "teams");
            if (teams.ValueKind != JsonValueKind.Array) continue;
            foreach (var team in teams.EnumerateArray())
            {
                if (++index > 64) throw new InvalidDataException("Too many IBM Bob teams.");
                var teamId = Identifier(Text(team, "id"));
                var budget = await get(region + "/admin/v1/teams/" + teamId + "/users/" + user,
                    new Dictionary<string, string> { ["x-instance-id"] = instanceId, ["x-team-id"] = teamId }).ConfigureAwait(false);
                var used = Number(budget, "usage"); var limit = Number(budget, "budget_limit") ?? Number(team, "budget_limit");
                if (used is >= 0) windows.Add(new(instanceId + "." + teamId, Text(team, "name") ?? teamId,
                    limit is > 0 ? used / limit * 100 : null, FlexibleDate(Get(instance, "refresh_at")), Unit: "Bobcoins",
                    DisplayValue: limit is >= 0 ? $"{used:N2} / {limit:N2} Bobcoins" : $"{used:N2} Bobcoins used"));
            }
        }
        return new("ibmbob", windows.Count > 0 ? ReadingState.Ready : ReadingState.Unavailable, windows, DateTimeOffset.Now,
            windows.Count > 0 ? null : "No Bobcoin allocation was returned for this account.");
    }
}
