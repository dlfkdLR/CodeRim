using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{

    private sealed class GeminiMigrationException : Exception;
    private static bool GeminiMigrationSignal(string value)
    {
        var text = value.ToLowerInvariant();
        return text.Contains("unsupported_client", StringComparison.Ordinal) || text.Contains("ineligibletiererror", StringComparison.Ordinal)
            || text.Contains("no longer supported", StringComparison.Ordinal) && text.Contains("gemini code assist", StringComparison.Ordinal)
            || text.Contains("migrate", StringComparison.Ordinal) && text.Contains("antigravity", StringComparison.Ordinal) && text.Contains("gemini", StringComparison.Ordinal);
    }
    private static bool GeminiConsumerUnsupported(JsonElement assist, JsonElement auth)
    {
        if (!string.IsNullOrWhiteSpace(Text(Get(assist, "paidTier"), "name"))) return false;
        var identity = Text(auth, "id_token");
        if (identity is { Length: > 0 and <= 65536 })
        {
            try
            {
                var parts = identity.Split('.');
                if (parts.Length >= 2)
                {
                    var payload = parts[1].Replace('-', '+').Replace('_', '/');
                    using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
                    if (!string.IsNullOrWhiteSpace(Text(claims.RootElement, "hd"))) return false;
                }
            }
            catch (Exception error) when (error is JsonException or FormatException) { }
        }
        var tiers = Get(assist, "ineligibleTiers");
        return tiers.ValueKind == JsonValueKind.Array && tiers.EnumerateArray().Any(x => GeminiMigrationSignal(Text(x, "reasonCode") ?? "") || GeminiMigrationSignal(Text(x, "reasonMessage") ?? ""));
    }

    private static readonly string[] GoogleRefreshKeys = ["refresh_token", "client_id", "client_secret"];
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> googleTokens = new(StringComparer.Ordinal);
    private async Task<string> ResolveGoogleToken(JsonElement auth, Func<Task<JsonElement>> refresh, bool allowUndatedAccess = false)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(auth.GetRawText())));
        if (googleTokens.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(1)) return cached.Token;
        var access = Text(auth, "access_token");
        if (access is { Length: > 0 and <= 32768 } && !access.Any(char.IsControl)
            && (EpochDate(auth, "expiry_date") > DateTimeOffset.UtcNow.AddMinutes(1) || allowUndatedAccess && Get(auth, "expiry_date").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)) return access;
        if (Text(auth, "type") != "service_account" && GoogleRefreshKeys.Any(x => Text(auth, x) is not { Length: > 0 })) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        var response = await refresh().ConfigureAwait(false); access = Text(response, "access_token");
        if (access is not { Length: > 0 and <= 32768 } || access.Any(char.IsControl)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        var seconds = Numeric(response, "expires_in");
        if (seconds is > 60 and <= 86400)
        {
            if (googleTokens.Count >= 64) googleTokens.Clear();
            googleTokens[key] = (access, DateTimeOffset.UtcNow.AddSeconds(seconds.Value));
        }
        return access;
    }
    private static Dictionary<string, string> GoogleTokenForm(JsonElement auth)
    {
        if (Text(auth, "type") != "service_account")
            return new() { ["client_id"] = Text(auth, "client_id")!, ["client_secret"] = Text(auth, "client_secret")!,
                ["refresh_token"] = Text(auth, "refresh_token")!, ["grant_type"] = "refresh_token" };
        var email = Text(auth, "client_email"); var key = Text(auth, "private_key");
        if (email is not { Length: > 0 and <= 512 } || key is not { Length: > 0 and <= 32768 }) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = email, scope = "https://www.googleapis.com/auth/monitoring.read",
            aud = "https://oauth2.googleapis.com/token", iat = now, exp = now + 3600 }));
        var unsigned = header + "." + payload;
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(key); }
        catch (ArgumentException) { throw new InvalidDataException("Invalid Google service account key."); }
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new() { ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = unsigned + "." + Encode(signature) };
    }
    private static async Task<ProviderReading> FetchVertex(string? project, Func<string, Task<JsonElement>> get)
    {
        if (project is not { Length: > 0 and <= 256 } || !project.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_' or '.'))
            throw new InvalidDataException("Set the Google Cloud project ID.");
        var now = DateTimeOffset.UtcNow; var documents = new Dictionary<string, JsonElement>(); var partial = false;
        foreach (var (key, metric) in new[] { ("usage", "serviceruntime.googleapis.com/quota/allocation/usage"), ("limit", "serviceruntime.googleapis.com/quota/limit") })
        {
            var filter = "metric.type=\"" + metric + "\" AND resource.type=\"consumer_quota\" AND resource.label.service=\"aiplatform.googleapis.com\"";
            var baseUrl = "https://monitoring.googleapis.com/v3/projects/" + Uri.EscapeDataString(project) + "/timeSeries?filter=" + Uri.EscapeDataString(filter)
                + "&interval.startTime=" + Uri.EscapeDataString(now.AddDays(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture))
                + "&interval.endTime=" + Uri.EscapeDataString(now.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
                + "&aggregation.alignmentPeriod=3600s&aggregation.perSeriesAligner=ALIGN_MAX&view=FULL";
            var collected = new List<JsonElement>(); var seen = new HashSet<string>(StringComparer.Ordinal); string? page = null;
            for (var index = 0; index < 8; index++)
            {
                var response = await get(baseUrl + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page))).ConfigureAwait(false);
                var series = Get(response, "timeSeries");
                if (series.ValueKind == JsonValueKind.Array)
                {
                    if (collected.Count + series.GetArrayLength() > 10000) { partial = true; break; }
                    collected.AddRange(series.EnumerateArray());
                }
                page = Text(response, "nextPageToken"); if (string.IsNullOrWhiteSpace(page)) break;
                if (!seen.Add(page) || index == 7) { partial = true; break; }
            }
            documents[key] = JsonSerializer.SerializeToElement(new { timeSeries = collected });
        }
        var reading = ParseVertex(documents);
        return partial && reading.Windows.Count > 0 ? reading with { State = ReadingState.Partial, Message = "Quota series were truncated." } : reading;
    }
    private readonly record struct VertexQuotaKey(string Metric, string Limit, string Location);
    private static ProviderReading ParseVertex(IReadOnlyDictionary<string, JsonElement> payloads)
    {
        static Dictionary<VertexQuotaKey, double> Values(JsonElement response)
        {
            var result = new Dictionary<VertexQuotaKey, double>(); var rows = Get(response, "timeSeries");
            if (rows.ValueKind != JsonValueKind.Array) return result;
            foreach (var row in rows.EnumerateArray())
            {
                var metric = Get(Get(row, "metric"), "labels"); var resource = Get(Get(row, "resource"), "labels");
                var name = Text(metric, "quota_metric") ?? Text(resource, "quota_id");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var key = new VertexQuotaKey(name, Text(metric, "limit_name") ?? "", Text(resource, "location") ?? "global");
                var points = Get(row, "points"); if (points.ValueKind != JsonValueKind.Array) continue;
                foreach (var point in points.EnumerateArray())
                {
                    var value = Numeric(Get(point, "value"), "doubleValue") ?? Numeric(Get(point, "value"), "int64Value");
                    if (value is >= 0) result[key] = Math.Max(result.GetValueOrDefault(key), value.Value);
                }
            }
            return result;
        }
        var usage = Values(payloads.GetValueOrDefault("usage")); var limits = Values(payloads.GetValueOrDefault("limit"));
        var windows = new List<LimitWindow>();
        foreach (var (key, amount) in usage)
        {
            double? limit = limits.TryGetValue(key, out var exact) && exact > 0 ? exact : null;
            if (!limit.HasValue && key.Limit.Length == 0)
            {
                var matches = limits.Where(x => x.Key.Metric == key.Metric && x.Key.Location == key.Location && x.Value > 0).ToArray();
                if (matches.Length == 1) limit = matches[0].Value;
            }
            if (limit is not > 0) continue;
            windows.Add(new(key.Metric + "|" + key.Limit + "|" + key.Location, key.Metric + " · " + key.Location,
                Math.Clamp(amount / limit.Value * 100, 0, 100), Unit: "quota", DisplayValue: $"{amount:N2} / {limit:N2}"));
        }
        return Metered("vertexai", windows.OrderByDescending(x => x.UsedPercent).ToArray(), "Cloud Monitoring · last 24 hours");
    }
    private static ProviderReading ParseGemini(JsonElement root)
    {
        var buckets = Get(root, "buckets"); if (buckets.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing Gemini quota buckets.");
        var quotas = new Dictionary<string, LimitWindow>(StringComparer.Ordinal);
        foreach (var bucket in buckets.EnumerateArray())
        {
            var model = Text(bucket, "modelId"); var fraction = Numeric(bucket, "remainingFraction");
            if (model is not { Length: > 0 and <= 256 } || fraction is not >= 0 or > 1) continue;
            var used = (1 - fraction.Value) * 100;
            if (!quotas.TryGetValue(model, out var previous) || previous.UsedPercent < used)
                quotas[model] = new(model, model, used, EpochDate(bucket, "resetTime"), 1440);
        }
        return Metered("gemini-cli", quotas.Values.OrderByDescending(x => x.Name.Contains("pro", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.UsedPercent).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray());
    }
}
