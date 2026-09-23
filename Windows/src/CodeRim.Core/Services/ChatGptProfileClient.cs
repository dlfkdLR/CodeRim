using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

public sealed record ProfileUsageSnapshot(long Today, long Week, long Month, long Lifetime,
    DateOnly StatsAsOf, DateTimeOffset GeneratedAt, string? AccountKey = null);

// Never use a record for credentials: its generated ToString would disclose tokens.
public sealed class ProfileCredential
{
    public string AccessToken { get; }
    public string AccountId { get; }
    public string? AccountKey { get; }
    public ProfileCredential(string accessToken, string accountId)
    {
        if (!ValidHeader(accessToken, 16384) || !ValidHeader(accountId, 256))
            throw new ProfileCredentialException();
        AccessToken = accessToken; AccountId = accountId;
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length != 3) return;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (claims.RootElement.TryGetProperty("sub", out var sub) && sub.ValueKind == JsonValueKind.String && sub.GetString() is { Length: > 0 } subject)
                AccountKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Encoding.UTF8.GetByteCount(accountId).ToString(CultureInfo.InvariantCulture) + ":" + accountId + subject)));
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException) { }
    }
    private static bool ValidHeader(string? value, int maximum) => !string.IsNullOrEmpty(value)
        && Encoding.UTF8.GetByteCount(value) <= maximum && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
    public bool SameAccount(ProfileCredential other) => AccountId == other.AccountId && AccountKey == other.AccountKey
        && (AccountKey is not null || AccessToken == other.AccessToken);
    public override string ToString() => "ProfileCredential [redacted]";
    public static ProfileCredential Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var tokens = document.RootElement.GetProperty("tokens");
            return new(tokens.GetProperty("access_token").GetString()!, tokens.GetProperty("account_id").GetString()!);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { throw new ProfileCredentialException(); }
    }
}

public sealed class ProfileCredentialException : Exception
{
    public ProfileCredentialException() : base("Codex sign-in is unavailable.") { }
}

/// <summary>Read-only account history. Never adds unowned local history to server totals.</summary>
public sealed class ChatGptProfileClient : IDisposable
{
    public static Uri Endpoint { get; } = new("https://chatgpt.com/backend-api/wham/profiles/me");
    public const int MaximumResponseBytes = 1024 * 1024;
    private static readonly string[] GeneratedFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
    private readonly Func<ProfileCredential> credentials;
    private readonly HttpClient http;
    public ChatGptProfileClient(Func<ProfileCredential> credentials, HttpMessageHandler? handler = null)
    {
        this.credentials = credentials;
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
    }
    public async Task<ProfileUsageSnapshot> FetchAsync(DateTimeOffset now, WeekStart weekStart, CancellationToken token = default)
    {
        var before = credentials();
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", before.AccessToken);
        request.Headers.Add("ChatGPT-Account-ID", before.AccountId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.Pragma.ParseAdd("no-cache"); request.Headers.UserAgent.ParseAdd("CodeRim/1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new ProfileCredentialException();
        if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != Endpoint)
            throw new InvalidDataException("Account history response is unavailable.");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Account history response is too large.");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > MaximumResponseBytes) throw new InvalidDataException("Account history response is too large.");
            output.Write(buffer, 0, count);
        }
        var snapshot = Decode(output.ToArray(), now, weekStart);
        token.ThrowIfCancellationRequested();
        if (!before.SameAccount(credentials())) throw new ProfileCredentialException();
        return snapshot with { AccountKey = before.AccountKey };
    }
    public static ProfileUsageSnapshot Decode(ReadOnlyMemory<byte> json, DateTimeOffset now, WeekStart weekStart)
    {
        if (json.Length > MaximumResponseBytes) throw new InvalidDataException("Account history response is too large.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement; var stats = root.GetProperty("stats"); var metadata = root.GetProperty("metadata");
            if (metadata.GetProperty("stats_error").ValueKind != JsonValueKind.Null) throw new FormatException();
            var lifetime = Nonnegative(stats.GetProperty("lifetime_tokens"));
            var asOf = Day(metadata.GetProperty("stats_as_of").GetString());
            var generated = metadata.GetProperty("generated_at").GetString();
            if (generated is null || generated != generated.Trim() || !DateTimeOffset.TryParseExact(generated,
                GeneratedFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var generatedAt)) throw new FormatException();
            var currentDay = DateOnly.FromDateTime(now.DateTime);
            var daysSinceStart = ((int)currentDay.DayOfWeek - (weekStart == WeekStart.Sunday ? 0 : 1) + 7) % 7;
            var week = currentDay.AddDays(-daysSinceStart); var month = new DateOnly(currentDay.Year, currentDay.Month, 1);
            long todayTotal = 0, weekTotal = 0, monthTotal = 0; var seen = new HashSet<DateOnly>();
            foreach (var bucket in stats.GetProperty("daily_usage_buckets").EnumerateArray())
            {
                var day = Day(bucket.GetProperty("start_date").GetString()); var tokens = Nonnegative(bucket.GetProperty("tokens"));
                if (day > asOf || !seen.Add(day)) throw new FormatException();
                if (day == asOf) todayTotal = tokens;
                if (day >= week && day.DayNumber - week.DayNumber < 7) weekTotal = checked(weekTotal + tokens);
                if (day.Year == month.Year && day.Month == month.Month) monthTotal = checked(monthTotal + tokens);
            }
            return new(todayTotal, weekTotal, monthTotal, lifetime, asOf, generatedAt);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException)
        { throw new InvalidDataException("Account history response is invalid."); }
    }
    private static long Nonnegative(JsonElement value) => value.TryGetInt64(out var number) && number >= 0 ? number : throw new FormatException();
    private static DateOnly Day(string? value) => value is { Length: 10 } && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : throw new FormatException();
    public void Dispose() => http.Dispose();
}
