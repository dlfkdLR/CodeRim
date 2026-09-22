using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public sealed partial class NativeProviders
{
    // Published frontend SDK identifier from the pinned Groq console adapter; not a secret.
    private const string GroqPublicToken = "public-token-live-58df57a9-a1f5-4066-bc0c-2ff942db684f";
    private static readonly Uri GroqConsoleOrigin = new("https://console.groq.com/");
    public static string? GroqEnvironmentCredential(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var session = environment("GROQ_SESSION_TOKEN")?.Trim();
        var jwt = environment("GROQ_SESSION_JWT")?.Trim();
        return string.IsNullOrEmpty(session) && string.IsNullOrEmpty(jwt) ? null
            : JsonSerializer.Serialize(new { session_token = session, session_jwt = jwt });
    }
    private static bool GroqConsoleSelected(string credential, Func<Uri, string?>? cookies) =>
        cookies is not null || credential.Equals("Session", StringComparison.OrdinalIgnoreCase) || credential.StartsWith('{') || credential.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)
        || credential.StartsWith("Session ", StringComparison.OrdinalIgnoreCase) || credential.Contains("stytch_session", StringComparison.Ordinal)
        || credential.Count(c => c == '.') == 2;
    private static (string? Token, string? Jwt) GroqSession(string credential, Func<Uri, string?>? cookies)
    {
        string Unquote(string value) => value.Length >= 2 && value[0] == value[^1] && (value[0] == (char)34 || value[0] == (char)39) ? value[1..^1].Trim() : value;
        credential = Unquote(credential.Trim());
        string? session = null, jwt = null;
        if (cookies is not null || credential.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) || credential.Contains("stytch_session=", StringComparison.Ordinal) || credential.Contains("stytch_session_jwt=", StringComparison.Ordinal))
        {
            var header = cookies is null ? credential : cookies(GroqConsoleOrigin);
            if (header?.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) == true) header = header[7..].Trim();
            header = Unquote(header ?? "");
            foreach (var pair in (header ?? "").Split(';'))
            {
                var at = pair.IndexOf('='); if (at <= 0) continue;
                var name = pair[..at].Trim(); var value = pair[(at + 1)..].Trim();
                if (value.Length == 0) continue;
                if (name == "stytch_session") { if (session is not null && session != value) throw new InvalidDataException("Ambiguous console session."); session = value; }
                if (name == "stytch_session_jwt") { if (jwt is not null && jwt != value) throw new InvalidDataException("Ambiguous console session."); jwt = value; }
            }
        }
        else if (credential.StartsWith('{'))
        {
            using var json = JsonDocument.Parse(credential);
            session = Text(json.RootElement, "session_token"); jwt = Text(json.RootElement, "session_jwt");
        }
        else if (credential.Equals("Session", StringComparison.OrdinalIgnoreCase)) session = null;
        else if (credential.StartsWith("Session ", StringComparison.OrdinalIgnoreCase)) session = credential[8..].Trim();
        else jwt = credential.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? credential[7..].Trim() : credential;
        session = GroqValidToken(session); jwt = GroqValidToken(jwt);
        if (session is null && jwt is null) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        return (session, jwt);
    }
    private static string? GroqValidToken(string? value)
    {
        value = value?.Trim(); if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 32768 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        return value;
    }
    private static string GroqOrganization(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3 || parts.Any(x => x.Length == 0) || jwt.Length > 32768) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        try
        {
            if (parts[1].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new FormatException();
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            // Only a routing hint. The Groq server authenticates the JWT and organization access.
            var org = Text(Get(document.RootElement, "https://groq.com/organization"), "id")
                ?? Text(Get(document.RootElement, "https://stytch.com/organization"), "slug");
            if (org is not { Length: > 0 and <= 256 } || org.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            return org;
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw new ProviderRequestException(HttpStatusCode.Unauthorized); }
    }
    private async Task<JsonElement> GroqConsoleJson(HttpRequestMessage request, string scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (retryAfter.TryGetValue(scope, out var retry) && retry > DateTimeOffset.Now)
            throw new ProviderRequestException(HttpStatusCode.TooManyRequests);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            retryAfter[scope] = response.Headers.RetryAfter?.Date ?? DateTimeOffset.Now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
        if (!response.IsSuccessStatusCode) throw new ProviderRequestException(response.StatusCode);
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("Console response too large.");
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("Console response too large.");
            output.Write(buffer, 0, read);
        }
        using var json = JsonDocument.Parse(output.ToArray()); return json.RootElement.Clone();
    }
    private async Task<ProviderReading> FetchGroqConsole(string credential, Func<Uri, string?>? cookies, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var session = GroqSession(credential, cookies); var jwt = session.Jwt;
        // The selected console session binds the complete quota transaction, including a refreshed JWT.
        // Rolling activity dates and refresh output must not bypass the same selected-session limit.
        var quotaScope = ProviderRetryScope.Create("groq", session.Token, GroqConsoleOrigin.AbsoluteUri, new { session.Jwt });
        if (retryAfter.TryGetValue(quotaScope, out var retry) && retry > DateTimeOffset.Now)
            throw new ProviderRequestException(HttpStatusCode.TooManyRequests);
        if (session.Token is not null)
        {
            try
            {
                using var refresh = new HttpRequestMessage(HttpMethod.Post, "https://api.stytchb2b.groq.com/sdk/v1/b2b/sessions/authenticate");
                refresh.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(GroqPublicToken + ":" + session.Token)));
                refresh.Headers.Add("Origin", "https://console.groq.com"); refresh.Headers.Add("X-SDK-Parent-Host", "https://console.groq.com");
                refresh.Headers.Add("X-SDK-Client", Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"app":{"identifier":"console.groq.com"},"sdk":{"identifier":"Stytch.js Javascript SDK","version":"5.43.0"}}""")));
                refresh.Content = new StringContent(JsonSerializer.Serialize(new { session_token = session.Token, session_duration_minutes = 30 }), Encoding.UTF8, "application/json");
                var refreshScope = ProviderRetryScope.Create("groq", session.Token, refresh.RequestUri!.AbsoluteUri);
                var response = await GroqConsoleJson(refresh, refreshScope, token).ConfigureAwait(false);
                jwt = GroqValidToken(Text(Get(response, "data"), "session_jwt"));
                if (string.IsNullOrWhiteSpace(jwt)) throw new InvalidDataException("Missing refreshed console session.");
                _ = GroqOrganization(jwt);
            }
            catch (Exception error) when (session.Jwt is not null && error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
            { token.ThrowIfCancellationRequested(); jwt = session.Jwt; }
        }
        token.ThrowIfCancellationRequested();
        var organization = GroqOrganization(jwt ?? "");
        var today = DateTime.Today; var now = DateTimeOffset.Now;
        var start = new DateTimeOffset(today.AddDays(-29)).ToUnixTimeSeconds(); var end = new DateTimeOffset(today.AddDays(1)).ToUnixTimeSeconds();
        var uri = "https://api.groq.com/platform/v1/organizations/" + Uri.EscapeDataString(organization) + "/activity?start_date=" + start.ToString(CultureInfo.InvariantCulture) + "&end_date=" + end.ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.Authorization = new("Bearer", jwt);
        return ParseGroqConsole(await GroqConsoleJson(request, quotaScope, token).ConfigureAwait(false), now);
    }
    private static ProviderReading ParseGroqConsole(JsonElement payload, DateTimeOffset now)
    {
        var rows = Get(payload, "data");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 10000) throw new InvalidDataException("Invalid console activity.");
        var entries = new List<ProviderCostEntry>(); var today = DateOnly.FromDateTime(now.LocalDateTime); var first = today.AddDays(-29);
        var incomplete = false; double spend = 0; var costKnown = true; long requests = 0; var requestsKnown = true;
        foreach (var row in rows.EnumerateArray())
        {
            var timestamp = Numeric(row, "timestamp");
            if (timestamp is not >= 0 or > 253402300799) throw new InvalidDataException("Invalid console timestamp.");
            var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds((long)timestamp.Value).LocalDateTime);
            if (date < first || date > today) { incomplete = true; costKnown = false; requestsKnown = false; continue; }
            long? OptionalCount(string name)
            {
                var raw = Get(row, name); if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
                var value = Count(row, name); if (value is null) throw new InvalidDataException("Invalid console count."); return value;
            }
            var context = OptionalCount("n_context_tokens_total"); var nonCached = OptionalCount("n_non_cached_context_tokens_total");
            var output = OptionalCount("n_generated_tokens_total"); var calls = OptionalCount("num_requests");
            if (nonCached > context) throw new InvalidDataException("Invalid console cache count.");
            var rawCost = Get(row, "cost"); var cost = Numeric(row, "cost");
            if (rawCost.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && cost is not >= 0) throw new InvalidDataException("Invalid console cost.");
            incomplete |= context is null || output is null || calls is null || cost is null;
            if (cost is { } amount) { spend += amount; if (!double.IsFinite(spend)) throw new InvalidDataException("Invalid console total."); } else costKnown = false;
            if (calls is { } count) requests = checked(requests + count); else requestsKnown = false;
            // Input includes cached context, matching total input semantics. Never relabel cache as reasoning tokens.
            entries.Add(new(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Text(row, "model"), context, output, null, calls, cost, null));
        }
        if (rows.GetArrayLength() > 0 && entries.Count == 0) throw new InvalidDataException("Console activity is outside the requested period.");
        var history = new ProviderCostUsage("USD", 30, "Last 30 days", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), entries);
        history.Validate();
        var windows = new List<LimitWindow>();
        if (costKnown || entries.Any(x => x.Cost.HasValue)) windows.Add(new("spend", costKnown ? "30-day spend" : "Known 30-day spend", Unit: "USD", DisplayValue: spend.ToString("N2", CultureInfo.InvariantCulture) + " USD"));
        if (requestsKnown) windows.Add(new("requests", "30-day requests", UsedCount: requests, Unit: "requests", DisplayValue: requests.ToString("N0", CultureInfo.InvariantCulture) + " requests"));
        return new("groq", incomplete ? ReadingState.Partial : ReadingState.Ready, windows, now,
            incomplete ? "Some activity fields are unavailable; missing values are not counted as zero." : null, "Groq console", history);
    }
}
