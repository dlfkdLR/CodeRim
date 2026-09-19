using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static readonly (string Id, string Metric)[] BedrockMetrics = [("inputTokens", "InputTokenCount"), ("outputTokens", "OutputTokenCount"), ("requests", "Invocations")];
    private static string BedrockRegion(Func<string, string?> setting) => setting("AWS_REGION") ?? setting("AWS_DEFAULT_REGION") ?? "us-east-1";
    private static void ConfigureBedrock(HttpRequestMessage request, string credential, Func<string, string?> setting, string body)
    {
        var region = BedrockRegion(setting); var monitoring = request.RequestUri!.Host.StartsWith("monitoring.", StringComparison.Ordinal);
        var key = setting("AWS_ACCESS_KEY_ID"); var secret = credential; var session = setting("AWS_SESSION_TOKEN");
        if (credential.TrimStart().StartsWith('{'))
        {
            using var auth = JsonDocument.Parse(credential);
            key = Text(auth.RootElement, "AccessKeyId") ?? Text(auth.RootElement, "aws_access_key_id");
            secret = Text(auth.RootElement, "SecretAccessKey") ?? Text(auth.RootElement, "aws_secret_access_key") ?? "";
            session = Text(auth.RootElement, "SessionToken") ?? Text(auth.RootElement, "aws_session_token");
        }
        request.Method = HttpMethod.Post; var bytes = Encoding.UTF8.GetBytes(body); request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new(monitoring ? "application/x-amz-json-1.0" : "application/x-amz-json-1.1");
        request.Headers.Add("X-Amz-Target", monitoring ? "GraniteServiceVersion20100801.GetMetricData" : "AWSInsightsIndexService.GetCostAndUsage");
        CloudSignature.Sign(request, bytes, key ?? "", secret, monitoring ? region : "us-east-1", monitoring ? "monitoring" : "ce", sessionToken: session);
    }
    private static async Task<ProviderReading> FetchBedrock(Func<string, string?> setting, Func<string, string, Task<JsonElement>> get, CancellationToken token)
    {
        var region = BedrockRegion(setting);
        if (region.Length is 0 or > 64 || !region.All(x => char.IsAsciiLetterOrDigit(x) || x == '-')) throw new InvalidDataException("Invalid AWS region.");
        var now = DateTimeOffset.UtcNow; var start = new DateOnly(now.Year, now.Month, 1); var tomorrow = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
        var costs = new List<ProviderCostEntry>(); var seen = new HashSet<string>(StringComparer.Ordinal); string? page = null; var partial = false; var currencies = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 20; index++)
        {
            var payload = new Dictionary<string, object> { ["TimePeriod"] = new { Start = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), End = tomorrow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                ["Granularity"] = "DAILY", ["Metrics"] = new[] { "UnblendedCost" }, ["GroupBy"] = new[] { new { Type = "DIMENSION", Key = "SERVICE" } } };
            if (page is not null) payload["NextPageToken"] = page;
            var response = await get("https://ce.us-east-1.amazonaws.com/", JsonSerializer.Serialize(payload)).ConfigureAwait(false);
            var rows = Get(response, "ResultsByTime"); if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing Cost Explorer results.");
            foreach (var row in rows.EnumerateArray())
            {
                var date = Text(Get(row, "TimePeriod"), "Start"); var groups = Get(row, "Groups");
                if (groups.ValueKind != JsonValueKind.Array || date is null) continue;
                foreach (var group in groups.EnumerateArray())
                {
                    var keys = Get(group, "Keys");
                    if (keys.ValueKind != JsonValueKind.Array || !keys.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString()!.Contains("Bedrock", StringComparison.OrdinalIgnoreCase))) continue;
                    var value = Get(Get(group, "Metrics"), "UnblendedCost"); var amount = Numeric(value, "Amount"); var currency = Text(value, "Unit") ?? "USD";
                    if (amount is null) throw new InvalidDataException("Invalid Bedrock cost.");
                    currencies.Add(currency); costs.Add(new(date, null, null, null, null, null, amount, Get(row, "Estimated").ValueKind == JsonValueKind.True ? amount : null));
                    if (costs.Count > 10000) throw new InvalidDataException("Too many billing rows.");
                }
            }
            page = Text(response, "NextPageToken"); if (string.IsNullOrWhiteSpace(page)) break;
            if (!seen.Add(page) || index == 19) throw new InvalidDataException("Incomplete Cost Explorer pagination.");
        }
        if (currencies.Count > 1) throw new InvalidDataException("Mixed billing currencies.");
        var total = costs.Sum(x => x.Cost ?? 0); if (!double.IsFinite(total)) throw new InvalidDataException("Invalid billing total.");
        double? budget = double.TryParse(setting("CODEXBAR_BEDROCK_BUDGET") ?? setting("CODERIM_BEDROCK_BUDGET"), NumberStyles.Float, CultureInfo.InvariantCulture, out var configured) && double.IsFinite(configured) && configured > 0 ? configured : null;
        var currencyCode = currencies.FirstOrDefault() ?? "USD"; var end = new DateTimeOffset(start.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var windows = new List<LimitWindow> { new("monthly-spend", "Monthly Bedrock spending", budget.HasValue ? Math.Clamp(total / budget.Value * 100, 0, 100) : null, end,
            Unit: currencyCode, DisplayValue: budget.HasValue ? $"{total:N2} / {budget:N2} {currencyCode}" : $"{total:N2} {currencyCode}") };
        try
        {
            page = null; seen.Clear(); var totals = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var index = 0; index < 20; index++)
            {
                var payload = new Dictionary<string, object> { ["StartTime"] = now.AddDays(-14).ToUnixTimeSeconds(), ["EndTime"] = now.ToUnixTimeSeconds(),
                    ["ScanBy"] = "TimestampAscending", ["MetricDataQueries"] = BedrockMetrics.Select(x => new { Id = x.Id,
                        Expression = "SUM(SEARCH('{AWS/Bedrock,ModelId} MetricName=\"" + x.Metric + "\" claude', 'Sum', 86400))", ReturnData = true }).ToArray() };
                if (page is not null) payload["NextToken"] = page;
                var response = await get("https://monitoring." + region + (region.StartsWith("cn-", StringComparison.Ordinal) ? ".amazonaws.com.cn/" : ".amazonaws.com/"), JsonSerializer.Serialize(payload)).ConfigureAwait(false);
                var messages = Get(response, "Messages"); if (messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0) throw new InvalidDataException("Incomplete CloudWatch results.");
                var rows = Get(response, "MetricDataResults");
                if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing CloudWatch metrics.");
                foreach (var row in rows.EnumerateArray())
                {
                    var id = Text(row, "Id"); if (id is null || !BedrockMetrics.Any(x => x.Id == id) || Text(row, "StatusCode") != "Complete") throw new InvalidDataException("Incomplete CloudWatch metric.");
                    var values = Get(row, "Values"); if (values.ValueKind != JsonValueKind.Array) continue;
                    foreach (var value in values.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0) throw new InvalidDataException("Invalid CloudWatch value.");
                        totals[id] = totals.GetValueOrDefault(id) + number;
                    }
                }
                page = Text(response, "NextToken"); if (string.IsNullOrWhiteSpace(page)) break;
                if (!seen.Add(page) || index == 19) throw new InvalidDataException("Incomplete CloudWatch pagination.");
            }
            foreach (var (id, label) in new[] { ("inputTokens", "Claude input · last 14 days"), ("outputTokens", "Claude output · last 14 days"), ("requests", "Claude requests · last 14 days") })
            {
                var value = totals.GetValueOrDefault(id); if (!double.IsFinite(value) || value >= 9223372036854775808d) throw new InvalidDataException("Invalid metric total.");
                windows.Add(new(id, label, UsedCount: (long)Math.Round(value), Unit: id == "requests" ? "requests" : "tokens", DisplayValue: $"{value:N0}"));
            }
        }
        catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); partial = true; }
        var history = new ProviderCostUsage(currencyCode, now.Day, "This month · AWS Bedrock", tomorrow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), costs, AllowCredits: true); history.Validate();
        return new("bedrock", partial ? ReadingState.Partial : ReadingState.Ready, windows, now, partial ? "Billing loaded; CloudWatch activity is unavailable." : null, region, history);
    }
}
