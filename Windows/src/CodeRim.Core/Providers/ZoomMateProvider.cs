using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static async Task<ProviderReading> FetchZoomMate(bool bootstrap, Action<string> setBearer, Func<string, Task<JsonElement>> get, CancellationToken token)
    {
        async Task<JsonElement> Request(string path)
        {
            try { return await get("https://ai.zoom.us" + path).ConfigureAwait(false); }
            catch (ProviderRequestException error) when (error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { throw; }
            catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or OperationCanceledException)
            { token.ThrowIfCancellationRequested(); return await get("https://zoommate.zoom.us" + path).ConfigureAwait(false); }
        }
        if (bootstrap)
        {
            var login = await Request("/ai-computer/api/v1/login/?continue=https%3A%2F%2Fzoommate.zoom.us%2F").ConfigureAwait(false);
            var bearer = Text(Get(login, "data"), "nak");
            if (bearer is not { Length: > 0 and <= 32768 } || bearer.Any(char.IsControl)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            setBearer(bearer);
        }
        return ParseZoomMate(await Request("/ai-computer/api/v1/credits/status").ConfigureAwait(false));
    }
    private static ProviderReading ParseZoomMate(JsonElement root)
    {
        var quota = Get(Get(root, "data"), "credit_status");
        if (quota.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Missing ZoomMate credits.");
        var used = Numeric(quota, "used_credit"); var cap = Numeric(quota, "budget_cap"); var remaining = Numeric(quota, "remaining_credit");
        var unlimited = Get(quota, "is_unlimited").ValueKind == JsonValueKind.True;
        var start = EpochDate(quota, "cycle_start_date"); var end = EpochDate(quota, "cycle_end_date");
        var duration = start.HasValue && end > start && (end.Value - start.Value).TotalMinutes < int.MaxValue ? (int)(end.Value - start.Value).TotalMinutes : 0;
        var windows = new List<LimitWindow>();
        if (unlimited || used is >= 0 || remaining is >= 0)
        {
            windows.Add(new("credits", "Credits", !unlimited && cap is > 0 && used is >= 0 ? Math.Clamp(used.Value / cap.Value * 100, 0, 100) : null,
                unlimited || cap is not > 0 ? null : end, duration, Unit: "credits",
                DisplayValue: unlimited ? "Unlimited" : used.HasValue && cap is > 0 ? $"{used:N2} / {cap:N2} credits"
                    : remaining.HasValue ? $"{remaining:N2} credits remaining" : $"{used:N2} credits used"));
        }
        if (Numeric(quota, "overage_credit") is > 0 and var overage) windows.Add(new("overage", "Overage", Unit: "credits", DisplayValue: $"{overage:N2} credits"));
        return Metered("zoommate", windows);
    }
}
