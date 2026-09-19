using System.Globalization;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public sealed partial class NativeProviders
{
    public static IReadOnlyList<(string Key, string Label)> Settings(string id) => id switch
    {
        "mimo" => [("MIMO_API_URL", "MiMo API base URL (default https://platform.xiaomimimo.com/api/v1)")],
        "kimi" => [("KIMI_CODE_BASE_URL", "Kimi Code API base URL (default https://api.kimi.com)")],
        "wayfinder" => [("WAYFINDER_GATEWAY_URL", "Gateway URL (default http://127.0.0.1:8088)")],
        "fireworks" => [("FIREWORKS_ACCOUNT_SLUG", "Account slug")],
        "llmproxy" => [("LLM_PROXY_BASE_URL", "Proxy base URL (HTTPS, or HTTP on this PC)")],
        "litellm" => [("LITELLM_BASE_URL", "Proxy base URL (HTTPS, or HTTP on this PC)")],
        _ => []
    };
    private static readonly HashSet<string> ManagementIds = new(StringComparer.Ordinal) { "llmproxy", "litellm", "zenmux", "warp", "wayfinder" };
    public static string ManagementBase(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidDataException("Set an HTTPS endpoint, or an HTTP endpoint on this PC.");
        return uri.AbsoluteUri.TrimEnd('/');
    }
    private static async Task<Dictionary<string, JsonElement>> ManagementPayloads(string id, Func<string, string?> setting, Func<string, Task<JsonElement>> get)
    {
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (id == "wayfinder")
        {
            var baseUrl = ManagementBase(setting("WAYFINDER_GATEWAY_URL") is { Length: > 0 } configured ? configured : "http://127.0.0.1:8088");
            documents["main"] = await get(baseUrl + "/healthz").ConfigureAwait(false);
            documents["models"] = await get(baseUrl + "/router/models").ConfigureAwait(false);
            documents["savings"] = await get(baseUrl + "/v1/savings?period=30d").ConfigureAwait(false);
        }
        else if (id == "zenmux")
            documents["main"] = await get("https://zenmux.ai/api/v1/management/subscription/detail").ConfigureAwait(false);
        else if (id == "warp")
            documents["main"] = await get("https://app.warp.dev/graphql/v2?op=GetRequestLimitInfo").ConfigureAwait(false);
        else
        {
            var key = id == "litellm" ? "LITELLM_BASE_URL" : "LLM_PROXY_BASE_URL";
            var baseUrl = ManagementBase(setting(key) ?? "");
            if (baseUrl.EndsWith("/v1", StringComparison.Ordinal)) baseUrl = baseUrl[..^3];
            if (id == "llmproxy") documents["main"] = await get(baseUrl + "/v1/quota-stats").ConfigureAwait(false);
            else
            {
                documents["main"] = await get(baseUrl + "/key/info").ConfigureAwait(false);
                var info = Get(documents["main"], "info");
                if (Text(info, "user_id") is { Length: > 0 } user)
                {
                    documents["user"] = await get(baseUrl + "/user/info?user_id=" + Uri.EscapeDataString(user)).ConfigureAwait(false);
                    var returned = Text(Get(documents["user"], "user_info"), "user_id") ?? Text(documents["user"], "user_id");
                    if (returned is not null && returned != user) throw new InvalidDataException("The proxy returned another user.");
                }
                else if (Text(info, "team_id") is { Length: > 0 } team)
                {
                    documents["team"] = await get(baseUrl + "/team/info?team_id=" + Uri.EscapeDataString(team)).ConfigureAwait(false);
                    var returned = Text(Get(documents["team"], "team_info"), "team_id") ?? Text(documents["team"], "team_id");
                    if (returned is not null && returned != team) throw new InvalidDataException("The proxy returned another team.");
                }
            }
        }
        return documents;
    }
    private static ProviderReading ParseManagement(string id, IReadOnlyDictionary<string, JsonElement> payloads)
    {
        var root = payloads.GetValueOrDefault("main"); var windows = new List<LimitWindow>(); string? plan = null;
        void Money(string key, string label, double? value)
        { if (value is >= 0 && double.IsFinite(value.Value)) windows.Add(new(key, label, Unit: "USD", DisplayValue: value.Value.ToString("N2", CultureInfo.CurrentCulture) + " USD")); }
        void Budget(string key, string label, JsonElement info)
        {
            var used = Number(info, "spend"); var limit = Number(info, "max_budget");
            if (used is >= 0)
                windows.Add(new(key, label, limit is > 0 ? used / limit * 100 : null, Date(Get(info, "budget_reset_at")),
                    Unit: "USD", DisplayValue: used.Value.ToString("N2", CultureInfo.CurrentCulture) + " USD used"));
        }
        if (id == "wayfinder")
        {
            var savings = payloads.GetValueOrDefault("savings"); var models = payloads.GetValueOrDefault("models");
            if (Text(root, "status") is not { } status) throw new InvalidDataException();
            plan = Get(root, "offline").ValueKind == JsonValueKind.True ? "Offline mode" : Get(models, "dry_run").ValueKind == JsonValueKind.True ? "Dry run" : "Local gateway";
            windows.Add(new("gateway", "Gateway", DisplayValue: status));
            if (Count(savings, "requests") is >= 0 and var requests) windows.Add(new("requests", "Last 30 days", UsedCount: requests, Unit: "requests"));
            if (Count(savings, "tokens") is >= 0 and var tokens) windows.Add(new("tokens", "Gateway tokens", UsedCount: tokens, Unit: "tokens"));
            if (Get(savings, "priced").ValueKind == JsonValueKind.True) Money("saved", "Saved vs highest-cost route", Number(savings, "saved"));
            else if (Number(savings, "saved_pct") is >= 0 and var saved) windows.Add(new("saved", "Saved vs highest-cost route", DisplayValue: $"{saved:0.##}%"));
        }
        else if (id == "zenmux")
        {
            if (Get(root, "success").ValueKind != JsonValueKind.True) throw new InvalidDataException();
            var data = Get(root, "data"); plan = Text(Get(data, "plan"), "tier");
            foreach (var (key, label, minutes) in new[] { ("quota_5_hour", "5h limit", 300), ("quota_7_day", "Weekly limit", 10080) })
            {
                var quota = Get(data, key);
                if (Number(quota, "usage_percentage") is >= 0 and var fraction)
                    windows.Add(new(key, label, fraction * 100, Date(Get(quota, "resets_at")), minutes,
                        Unit: "flows", DisplayValue: $"{Number(quota, "used_flows"):N0} / {Number(quota, "max_flows"):N0} flows"));
            }
        }
        else if (id == "litellm")
        {
            var info = Get(root, "info"); var user = Get(payloads.GetValueOrDefault("user"), "user_info");
            Budget("personal", "Personal budget", user);
            var teamId = Text(info, "team_id"); var teams = Get(payloads.GetValueOrDefault("user"), "teams");
            if (teams.ValueKind == JsonValueKind.Array && teamId is not null)
                foreach (var team in teams.EnumerateArray().Where(x => Text(x, "team_id") == teamId).Take(1)) Budget("team", Text(team, "team_alias") ?? "Team budget", team);
            Budget("team", "Team budget", Get(payloads.GetValueOrDefault("team"), "team_info"));
            Money("key", "Key spend", Number(info, "spend")); plan = Text(info, "key_name");
        }
        else if (id == "llmproxy")
        {
            var providers = Get(root, "providers"); var quotas = new List<JsonElement>();
            if (providers.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            foreach (var provider in providers.EnumerateObject())
            {
                var groups = Get(provider.Value, "quota_groups");
                if (groups.ValueKind == JsonValueKind.Array) quotas.AddRange(groups.EnumerateArray());
                else if (groups.ValueKind == JsonValueKind.Object) quotas.AddRange(groups.EnumerateObject().Select(x => x.Value));
            }
            var remaining = quotas.Select(x => Number(x, "remaining_percent")).Where(x => x is >= 0 and <= 100).Min();
            var reset = quotas.Select(x => Date(Get(x, "reset_time"))).Where(x => x > DateTimeOffset.Now).Min();
            if (remaining.HasValue) windows.Add(new("quota", "Lowest remaining quota", 100 - remaining, reset));
            var summary = Get(root, "summary");
            var requests = Count(summary, "total_requests") ?? providers.EnumerateObject().Sum(x => Count(x.Value, "total_requests") ?? 0);
            windows.Add(new("requests", "Proxy requests", UsedCount: requests, Unit: "requests"));
            Money("cost", "Approximate proxy cost", Number(summary, "approx_cost"));
        }
        else if (id == "warp")
        {
            if (Get(root, "errors").ValueKind == JsonValueKind.Array && Get(root, "errors").GetArrayLength() > 0) throw new InvalidDataException();
            var user = Get(Get(Get(root, "data"), "user"), "user"); var quota = Get(user, "requestLimitInfo");
            var used = Count(quota, "requestsUsedSinceLastRefresh"); var limit = Number(quota, "requestLimit");
            if (used.HasValue) windows.Add(new("requests", "Monthly requests",
                Get(quota, "isUnlimited").ValueKind == JsonValueKind.True ? null : limit is > 0 ? used / limit * 100 : null,
                FlexibleDate(Get(quota, "nextRefreshTime")), UsedCount: used, Unit: "requests",
                DisplayValue: Get(quota, "isUnlimited").ValueKind == JsonValueKind.True ? "Unlimited · " + used + " requests used" : null));
            var grants = new List<JsonElement>(); var personal = Get(user, "bonusGrants");
            if (personal.ValueKind == JsonValueKind.Array) grants.AddRange(personal.EnumerateArray());
            var workspaces = Get(user, "workspaces");
            if (workspaces.ValueKind == JsonValueKind.Array)
                foreach (var workspace in workspaces.EnumerateArray())
                {
                    var items = Get(Get(workspace, "bonusGrantsInfo"), "grants");
                    if (items.ValueKind == JsonValueKind.Array) grants.AddRange(items.EnumerateArray());
                }
            var active = grants.Where(x => Date(Get(x, "expiration")) is not { } expires || expires > DateTimeOffset.Now).ToArray();
            if (active.Length > 0)
            {
                var total = active.Sum(x => Count(x, "requestCreditsGranted") ?? 0); var left = active.Sum(x => Count(x, "requestCreditsRemaining") ?? 0);
                windows.Add(new("bonus", "Bonus credits", total > 0 ? (total - left) * 100d / total : null, RemainingCount: left, Unit: "credits"));
            }
        }
        return new(id, windows.Count > 0 ? ReadingState.Ready : ReadingState.Unavailable, windows, DateTimeOffset.Now,
            windows.Count > 0 ? null : "No metered usage was returned for this account.", plan);
    }
    private static string WarpBody()
    {
        const string query = "query GetRequestLimitInfo($requestContext: RequestContext!) { user(requestContext: $requestContext) { __typename ... on UserOutput { user { requestLimitInfo { isUnlimited nextRefreshTime requestLimit requestsUsedSinceLastRefresh } bonusGrants { requestCreditsGranted requestCreditsRemaining expiration } workspaces { bonusGrantsInfo { grants { requestCreditsGranted requestCreditsRemaining expiration } } } } } } }";
        return JsonSerializer.Serialize(new { query, operationName = "GetRequestLimitInfo",
            variables = new { requestContext = new { clientContext = new { }, osContext = new { category = "Windows", name = "Windows", version = Environment.OSVersion.Version.ToString() } } } });
    }
}
