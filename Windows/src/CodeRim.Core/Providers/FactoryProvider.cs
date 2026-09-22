using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public sealed partial class NativeProviders
{
    private static bool FactoryModern(JsonElement billing) =>
        Get(billing, "usesTokenRateLimitsBilling").ValueKind == JsonValueKind.True && Get(billing, "limits").ValueKind == JsonValueKind.Object;
    private static string? FactoryUserId(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 256 || value.Any(char.IsControl)) throw new InvalidDataException("Invalid Factory user identifier.");
        return value;
    }
    private static string? FactorySubject(string bearer)
    {
        if (bearer.Length > 32768) return null;
        var parts = bearer.Split('.'); if (parts.Length != 3) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            // Used only as a server-validated lookup hint, never as proof of identity.
            var subject = Text(json.RootElement, "sub");
            return FactoryUserId(subject);
        }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }
    private static async Task<ProviderReading> FetchFactory(FactoryRequestContext context, Func<string, Task<JsonElement>> get, CancellationToken token)
    {
        Exception? last = null; ProviderRequestException? authError = null;
        foreach (var host in new[] { "https://api.factory.ai", "https://app.factory.ai", "https://auth.factory.ai" })
        {
            // At most one bearer removal and four cookie variants across this fetch.
            for (var attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    token.ThrowIfCancellationRequested(); context.AcceptedBearer = null;
                    var auth = await get(host + "/api/app/auth/me").ConfigureAwait(false);
                    var documents = new Dictionary<string, JsonElement> { ["auth"] = auth };
                    try { documents["limits"] = await get("https://api.factory.ai/api/billing/limits").ConfigureAwait(false); }
                    catch (ProviderRequestException error) when (error.Status == System.Net.HttpStatusCode.TooManyRequests) { throw; }
                    catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
                    { token.ThrowIfCancellationRequested(); }
                    if (!FactoryModern(documents.GetValueOrDefault("limits")))
                    {
                        var user = FactoryUserId(Text(Get(auth, "userProfile"), "id")) ?? FactorySubject(context.AcceptedBearer ?? "");
                        documents["usage"] = await get(host + "/api/organization/subscription/usage?useCache=true" + (user is null ? "" : "&userId=" + Uri.EscapeDataString(user))).ConfigureAwait(false);
                        var returnedUser = FactoryUserId(Text(documents["usage"], "userId"));
                        if (user is not null && returnedUser is not null && user != returnedUser) throw new InvalidDataException("Factory returned another user.");
                    }
                    return ParseFactory(documents);
                }
                catch (FactoryAuthenticationRetryException) { token.ThrowIfCancellationRequested(); }
                catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    if (error is ProviderRequestException { Status: System.Net.HttpStatusCode.TooManyRequests }) throw;
                    last = error;
                    if (error is ProviderRequestException { Status: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } rejected) authError ??= rejected;
                    break;
                }
            }
        }
        throw (Exception?)authError ?? last ?? new InvalidDataException("Factory returned no billing response.");
    }
    private static ProviderReading ParseFactory(IReadOnlyDictionary<string, JsonElement> documents)
    {
        var windows = new List<LimitWindow>();
        var auth = documents.GetValueOrDefault("auth"); var billing = documents.GetValueOrDefault("limits");
        var subscription = Get(Get(auth, "organization"), "subscription");
        var plan = Text(Get(Get(subscription, "orbSubscription"), "plan"), "name") ?? Text(subscription, "factoryTier");
        if (FactoryModern(billing))
        {
            foreach (var poolName in new[] { "standard", "core" })
            {
                var pool = Get(Get(billing, "limits"), poolName);
                if (pool.ValueKind != JsonValueKind.Object) continue;
                if (poolName == "core" && !pool.EnumerateObject().Any(x => Numeric(x.Value, "usedPercent") is > 0 || EpochDate(x.Value, "windowEnd").HasValue || Numeric(x.Value, "secondsRemaining").HasValue)) continue;
                foreach (var (key, label, minutes) in new[] { ("fiveHour", "5h limit", 300), ("weekly", "Weekly limit", 10080), ("monthly", "Monthly limit", 0) })
                {
                    var quota = Get(pool, key); if (Numeric(quota, "usedPercent") is not >= 0) continue;
                    var used = Numeric(quota, "usedPercent")!.Value;
                    var end = EpochDate(quota, "windowEnd"); var seconds = Numeric(quota, "secondsRemaining");
                    DateTimeOffset? reset = seconds is > 0 and < 315360000 ? DateTimeOffset.UtcNow.AddSeconds(seconds.Value) : end > DateTimeOffset.UtcNow ? end : null;
                    if (reset is null && end.HasValue && seconds is null) used = 0;
                    windows.Add(new(poolName + "." + key, (poolName == "core" ? "Core · " : "") + label, Math.Clamp(used, 0, 100), reset, minutes));
                }
            }
            if (Numeric(billing, "extraUsageBalanceCents") is >= 0 and var balance)
                windows.Add(new("extra", "Extra usage balance", Unit: "USD", DisplayValue: $"{balance / 100:N2} USD"));
        }
        else
        {
            var usage = Get(documents.GetValueOrDefault("usage"), "usage"); var start = EpochDate(usage, "startDate"); var end = EpochDate(usage, "endDate");
            foreach (var key in new[] { "standard", "premium" })
            {
                var pool = Get(usage, key); var used = Count(pool, "userTokens"); var limit = Numeric(pool, "totalAllowance");
                var ratio = Numeric(pool, "usedRatio"); double? percent = null;
                if (ratio is >= 0 and <= 1.001 && !(ratio == 0 && used > 0 && limit is > 0 and <= 1e12)) percent = Math.Clamp(ratio.Value * 100, 0, 100);
                else if (limit is > 0 and <= 1e12 && used is >= 0) percent = used / limit * 100;
                else if (ratio is >= 0 and <= 100) percent = ratio;
                if (!used.HasValue && !percent.HasValue) continue;
                windows.Add(new(key, key == "standard" ? "Standard tokens" : "Premium tokens", percent, end,
                    start.HasValue && end > start && (end.Value - start.Value).TotalMinutes < int.MaxValue ? (int)(end.Value - start.Value).TotalMinutes : 0,
                    UsedCount: used, Unit: "tokens"));
            }
        }
        return Metered("factory", windows, plan);
    }
}
