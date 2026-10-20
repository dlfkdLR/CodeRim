using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static JsonElement AntigravityAuth(string credential, Func<string, string?> setting)
    {
        var auth = JsonNode.Parse(credential) as JsonObject ?? throw new InvalidDataException("Invalid Antigravity login.");
        foreach (var (snake, camel) in new[] { ("access_token", "accessToken"), ("refresh_token", "refreshToken"), ("expiry_date", "expiresAt"),
            ("client_id", "clientId"), ("client_secret", "clientSecret"), ("project_id", "projectId") })
            if (auth[snake] is null && auth[camel] is { } value) auth[snake] = value.DeepClone();
        if (auth["client_id"] is null) auth["client_id"] = setting("ANTIGRAVITY_OAUTH_CLIENT_ID");
        if (auth["client_secret"] is null) auth["client_secret"] = setting("ANTIGRAVITY_OAUTH_CLIENT_SECRET");
        return JsonSerializer.SerializeToElement(auth);
    }
    private static async Task<ProviderReading> FetchAntigravity(Func<string, Task<JsonElement>> get)
    {
        ProviderReading? models = null;
        try
        {
            models = ParseAntigravity(await get("https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels").ConfigureAwait(false));
            if (models.Windows.Count == 0 || !models.Windows.All(x => x.UsedPercent <= 0.100000001)) return models;
        }
        catch (ProviderRequestException error) when (error.Status == HttpStatusCode.Forbidden) { }
        try
        {
            var verified = ParseGemini(await get("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota").ConfigureAwait(false));
            if (verified.Windows.Count == 0) return new("gemini", ReadingState.Ready, [], DateTimeOffset.UtcNow, "No verified remote quota was returned.");
            var originals = models?.Windows.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            return verified with { Id = "gemini", Windows = verified.Windows.Select(x => x with { Name = originals?.GetValueOrDefault(x.Id)?.Name ?? x.Name, ResetsAt = x.ResetsAt ?? originals?.GetValueOrDefault(x.Id)?.ResetsAt, DurationMinutes = 0 }).ToArray() };
        }
        catch (ProviderRequestException error) when (error.Status == HttpStatusCode.Forbidden)
        {
            return new("gemini", ReadingState.Ready, [], DateTimeOffset.UtcNow, "This account does not expose verified remote quota.");
        }
    }
    private static ProviderReading ParseAntigravity(JsonElement response)
    {
        var models = Get(response, "models");
        if (models.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Missing Antigravity models.");
        var windows = new List<LimitWindow>();
        foreach (var model in models.EnumerateObject())
        {
            var quota = Get(model.Value, "quotaInfo"); var remaining = Numeric(quota, "remainingFraction");
            if (remaining is not >= 0 or > 1) continue;
            windows.Add(new(model.Name, Text(model.Value, "displayName") ?? Text(model.Value, "label") ?? model.Name,
                (1 - remaining.Value) * 100, EpochDate(quota, "resetTime")));
        }
        if (windows.Count == 0) return new("gemini", ReadingState.Ready, [], DateTimeOffset.UtcNow, "No verified remote quota was returned.");
        return Metered("gemini", windows.OrderByDescending(x => x.UsedPercent).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray());
    }
}
