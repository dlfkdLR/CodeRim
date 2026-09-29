using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

public sealed record MobileWindow(string Name, double? RemainingPercent, double? ResetsAt);
public sealed record MobileProvider(string Id, string Name, string State, IReadOnlyList<MobileWindow> Windows, long? TodayTokens, string LocalState, double? UpdatedAt);
public sealed record MobileSession(string ProviderID, string Phase, string Title, double? Since);
public sealed record MobileSnapshot(int SchemaVersion, double GeneratedAt, IReadOnlyList<MobileProvider> Providers, IReadOnlyList<MobileSession> Sessions);
public sealed record MobileToken(string Token, double ExpiresAt);
public sealed record MobileCredential(string Endpoint, string Token, double ExpiresAt, bool ShareTitles = false);
public sealed record MobilePairClaim(string Code, string Platform, string Name);

public static class MobileSnapshotBuilder
{
    public static MobileSnapshot Create(CompanionSnapshot snapshot, IReadOnlyList<SessionActivity> sessions, bool shareTitles, DateTimeOffset now)
    {
        var providers = snapshot.Providers.Where(p => p.Enabled).Take(100).Select(p =>
        {
            var reading = p.Limits.Evaluated(now);
            var tokens = p.LocalUsage?.Totals.GetValueOrDefault("today").TotalTokens;
            return new MobileProvider(p.Id, Short(p.Name, 32), State(reading.State), reading.Windows.Take(2).Select(w => new MobileWindow(Short(w.Name, 32),
                w.UsedPercent is { } used && double.IsFinite(used) && used is >= 0 and <= 100 ? 100 - used : null,
                Seconds(w.ResetsAt))).ToArray(), tokens is >= 0 and <= 9_007_199_254_740_991 ? tokens : null,
                p.LocalUsage?.State ?? "unavailable", Seconds(reading.UpdatedAt));
        }).ToArray();
        var enabled = providers.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var activity = sessions.Where(s => enabled.Contains(s.Provider)).Select(s => new MobileSession(s.Provider,
            s.State switch { "busy" => "working", "waiting" => "waiting", "idle" => "idle", _ => "unavailable" },
            shareTitles ? Short(s.Name, 60) : "", Seconds(s.Since)))
            .OrderBy(s => s.Phase switch { "waiting" => 0, "working" => 1, "idle" => 2, _ => 3 }).ThenByDescending(s => s.Since).Take(64).ToArray();
        return new(1, now.ToUnixTimeMilliseconds() / 1000d, providers, activity);
    }
    private static double? Seconds(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds() / 1000d;
    private static string Short(string value, int maximum) => string.Concat(value.EnumerateRunes().Take(maximum).Select(r => r.ToString()));
    private static string State(ReadingState state) => state switch {
        ReadingState.Ready => "ready", ReadingState.Stale => "stale", ReadingState.Loading => "loading",
        ReadingState.NeedsAuth => "needsAuth", ReadingState.Unsupported => "unsupported", _ => "unavailable" };
}

/// Internet HTTPS transport; no LAN discovery, desktop listener or provider credential transfer.
public sealed class MobileRelayClient : IDisposable
{
    private readonly HttpClient http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Uri Endpoint { get; }
    public MobileRelayClient(string endpoint, HttpMessageHandler? handler = null)
    {
        Endpoint = ValidateEndpoint(endpoint);
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
    }
    public static Uri ValidateEndpoint(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new ArgumentException("Enter an HTTPS server origin without a path or credentials.", nameof(value));
        return uri;
    }
    public async Task<MobileToken> PairAsync(string code, string name, CancellationToken cancellationToken)
    {
        var issued = await SendAsync<MobileToken>(HttpMethod.Post, "/v1/pairing/claim",
            new MobilePairClaim(code.Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal), "windows", name), null, cancellationToken).ConfigureAwait(false);
        return ValidateIssuedToken(issued, DateTimeOffset.UtcNow);
    }
    public async Task PublishAsync(MobileSnapshot snapshot, string token, CancellationToken cancellationToken)
        => _ = await SendAsync<JsonElement>(HttpMethod.Post, "/v1/snapshot", snapshot, token, cancellationToken).ConfigureAwait(false);
    public async Task DisconnectAsync(string token, CancellationToken cancellationToken)
        => _ = await SendAsync<JsonElement>(HttpMethod.Delete, "/v1/session", new { }, token, cancellationToken).ConfigureAwait(false);
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, string? token, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        if (bytes.Length > 262144) throw new InvalidDataException("Mobile snapshot exceeds the transfer limit.");
        using var request = new HttpRequestMessage(method, new Uri(Endpoint, path)) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (token is not null)
        {
            ValidateBearerToken(token);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400) throw new HttpRequestException("Relay redirects are not accepted.", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(262144, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidDataException("Empty relay response.");
    }
    public static MobileToken ValidateIssuedToken(MobileToken issued, DateTimeOffset now)
    {
        ValidateBearerToken(issued.Token);
        var nowSeconds = now.ToUnixTimeMilliseconds() / 1000d;
        var maximum = nowSeconds + TimeSpan.FromDays(366).TotalSeconds;
        if (!double.IsFinite(issued.ExpiresAt) || issued.ExpiresAt <= nowSeconds || issued.ExpiresAt > maximum)
            throw new InvalidDataException("Relay token expiry is invalid.");
        return issued;
    }
    private static void ValidateBearerToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length is < 32 or > 128 || token.Any(character =>
                character is not (>= 'A' and <= 'Z')
                    and not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '_' and not '-'))
            throw new InvalidDataException("Relay token format is invalid.");
    }
    public void Dispose() => http.Dispose();
}
