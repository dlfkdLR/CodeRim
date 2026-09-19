using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

/// Read-only requests to fixed vendor endpoints. Redirects and cookie storage are disabled.
public sealed class HttpProviders : IDisposable
{
    private readonly HttpClient client;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> retryAfter = new(StringComparer.Ordinal);
    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(StringComparer.Ordinal)
        { "copilot", "glm", "ollama-local", "deepseek", "openrouter", "elevenlabs", "moonshot", "synthetic" };
    public HttpProviders(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeRim/2.1.6");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
    public void Dispose() => client.Dispose();
    public async Task<ProviderReading> FetchAsync(string id, string? secret, CancellationToken cancellationToken = default)
    {
        if (!Supported.Contains(id)) return new(id, ReadingState.Unsupported, [], Message: "The Windows connector for this provider is not available yet.");
        if (retryAfter.TryGetValue(id, out var retry) && retry > DateTimeOffset.Now) return new(id, ReadingState.Unavailable, [], Message: "Rate limited. Refresh resumes after " + retry.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture));
        if (id != "ollama-local" && string.IsNullOrWhiteSpace(secret)) return new(id, ReadingState.NeedsAuth, [], Message: "Connect this provider in Settings.");
        var endpoint = id switch
        {
            "copilot" => "https://api.github.com/copilot_internal/user",
            "glm" => "https://api.z.ai/api/monitor/usage/quota/limit",
            "deepseek" => "https://api.deepseek.com/user/balance",
            "openrouter" => "https://openrouter.ai/api/v1/key",
            "elevenlabs" => "https://api.elevenlabs.io/v1/user/subscription",
            "moonshot" => "https://api.moonshot.ai/v1/users/me/balance",
            "synthetic" => "https://api.synthetic.new/v2/quotas",
            _ => "http://127.0.0.1:11434/api/ps"
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (id == "elevenlabs") request.Headers.Add("xi-api-key", secret);
        else if (id == "glm") request.Headers.TryAddWithoutValidation("Authorization", secret);
        else if (id != "ollama-local") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(id, ReadingState.NeedsAuth, [], Message: "Sign in again or update the provider credential.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                retryAfter[id] = DateTimeOffset.Now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
                return new(id, ReadingState.Unavailable, [], Message: "Provider rate limit reached. Waiting before retrying.");
            }
            if (!response.IsSuccessStatusCode) return new(id, ReadingState.Error, [], Message: "The provider could not return a reading.");
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new IOException("Provider response is too large.");
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var bytes = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(bytes, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + count > 2 * 1024 * 1024) throw new IOException("Provider response is too large.");
                memory.Write(bytes, 0, count);
            }
            using var document = JsonDocument.Parse(memory.ToArray());
            var windows = Parse(id, document.RootElement);
            return new(id, windows.Count > 0 ? ReadingState.Ready : ReadingState.Unavailable, windows, DateTimeOffset.Now,
                windows.Count > 0 ? null : "No metered usage was returned.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(id, ReadingState.Error, [], Message: "Unable to refresh. Check the provider connection.");
        }
    }
    public static IReadOnlyList<LimitWindow> Parse(string id, JsonElement root)
    {
        if (id == "copilot") return Copilot(root);
        if (id == "glm") return Glm(root);
        var result = new List<LimitWindow>();
        switch (id)
        {
            case "ollama-local":
                var models = Get(root, "models");
                if (models.ValueKind != JsonValueKind.Array) break;
                result.Add(new("loaded", "Loaded models", UsedCount: models.GetArrayLength(), Unit: "models"));
                foreach (var model in models.EnumerateArray())
                {
                    var name = Text(model, "name") ?? Text(model, "model");
                    if (name is null) continue;
                    var size = Math.Max(Number(model, "size") ?? 0, Number(model, "size_vram") ?? 0) / 1048576;
                    result.Add(new(name, name, DisplayValue: size.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " MB"));
                }
                break;
            case "deepseek":
                var balances = Get(root, "balance_infos");
                if (balances.ValueKind != JsonValueKind.Array) break;
                foreach (var balance in balances.EnumerateArray())
                {
                    var currency = Text(balance, "currency"); var amount = Text(balance, "total_balance");
                    if (amount is not null) result.Add(new(currency ?? "balance", "Available balance", Unit: currency, DisplayValue: amount + " " + currency));
                }
                break;
            case "moonshot":
                var data = Get(root, "data");
                if (Number(data, "available_balance") is { } available) result.Add(new("balance", "Available balance", DisplayValue: available.ToString("N2", System.Globalization.CultureInfo.CurrentCulture) + " balance"));
                break;
            case "openrouter":
                var key = Get(root, "data");
                var usage = Number(key, "usage"); var limit = Number(key, "limit");
                var remaining = Number(key, "limit_remaining");
                var resetPeriod = Text(key, "limit_reset");
                var periodUsage = resetPeriod switch { "daily" => Number(key, "usage_daily"), "weekly" => Number(key, "usage_weekly"), "monthly" => Number(key, "usage_monthly"), _ => usage };
                var consumed = limit.HasValue && remaining.HasValue ? Math.Max(0, limit.Value - remaining.Value) : periodUsage;
                if (usage is not null) result.Add(new("spend", "Key spending", limit > 0 && consumed.HasValue ? consumed / limit * 100 : null, Unit: "USD", DisplayValue: "$" + usage.Value.ToString("N2", System.Globalization.CultureInfo.CurrentCulture)));
                foreach (var (field, label) in new[] { ("usage_daily", "Today"), ("usage_weekly", "This week"), ("usage_monthly", "This month") })
                    if (Number(key, field) is { } spent) result.Add(new(field, label, Unit: "USD", DisplayValue: "$" + spent.ToString("N2", System.Globalization.CultureInfo.CurrentCulture)));
                break;
            case "elevenlabs":
                var used = Count(root, "character_count"); var ceiling = Count(root, "character_limit");
                if (used is not null) result.Add(new("characters", "Subscription credits", ceiling > 0 ? (double)used / ceiling * 100 : null,
                    Date(Get(root, "next_character_count_reset_unix")), UsedCount: used, RemainingCount: ceiling.HasValue ? Math.Max(0, ceiling.Value - used.Value) : null, Unit: "credits"));
                break;
            case "synthetic":
                var weekly = Get(root, "weeklyTokenLimit");
                if (Number(weekly, "percentRemaining") is { } weeklyRemaining)
                    result.Add(new("weekly", "Weekly", Math.Clamp(100 - weeklyRemaining, 0, 100), Date(Get(weekly, "renewsAt")), 10080));
                foreach (var (field, label) in new[] { ("subscription", "Subscription"), ("search", "Search"), ("toolCallDiscounts", "Tool calls") })
                {
                    var quota = Get(root, field);
                    if (field == "search" && Get(quota, "hourly").ValueKind == JsonValueKind.Object) quota = Get(quota, "hourly");
                    var requests = Number(quota, "requests"); var allowed = Number(quota, "limit");
                    if (requests is not null) result.Add(new(field, label, allowed > 0 ? requests / allowed * 100 : null, Date(Get(quota, "renewsAt")), UsedCount: Count(quota, "requests"), Unit: "requests"));
                }
                break;
        }
        return result;
    }
}
