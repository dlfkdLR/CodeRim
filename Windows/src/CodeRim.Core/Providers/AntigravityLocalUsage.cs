using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Providers;

/// <summary>Read-only language-server protocol. The Windows caller owns and verifies the local transport.</summary>
public static class AntigravityLocalUsage
{
    private const string Service = "/exa.language_server_pb.LanguageServerService/";
    private const string Metadata = """{"metadata":{"ideName":"antigravity","extensionName":"antigravity","ideVersion":"unknown","locale":"en"}}""";
    public static string? Source(string? value) => value?.Trim().ToLowerInvariant() switch
    { null or "" or "oauth" => "oauth", "local" => "local", _ => null };
    public static async Task<ProviderReading> FetchAsync(HttpClient client, string csrf, Func<bool> sameProcess, CancellationToken token = default)
    {
        if (client.BaseAddress is not { Scheme: "https", Host: "127.0.0.1" } address || address.UserInfo.Length != 0
            || address.Query.Length != 0 || address.Fragment.Length != 0 || address.AbsolutePath != "/" || !ValidCsrf(csrf))
            return Failure(ReadingState.Error);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var started = Stopwatch.GetTimestamp();
        async Task<JsonElement> Read(string method, bool reserveFallback)
        {
            using var stage = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var remaining = TimeSpan.FromSeconds(8) - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException(deadline.Token);
            stage.CancelAfter(reserveFallback ? remaining / 2 : remaining);
            stage.Token.ThrowIfCancellationRequested();
            if (!sameProcess()) throw new ProcessChangedException();
            using var request = new HttpRequestMessage(HttpMethod.Post, Service + method) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            request.Headers.Add("Connect-Protocol-Version", "1"); request.Headers.Add("X-Codeium-Csrf-Token", csrf);
            request.Content = new StringContent(method == "RetrieveUserQuotaSummary" ? """{"forceRefresh":true}""" : Metadata, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stage.Token).ConfigureAwait(false);
            if (!sameProcess()) throw new ProcessChangedException();
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new LocalThrottleException();
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LocalAuthenticationException();
            if (!response.IsSuccessStatusCode) throw new LocalUnavailableException();
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException();
            using var input = await response.Content.ReadAsStreamAsync(stage.Token).ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[16384]; int count;
            while ((count = await input.ReadAsync(buffer, stage.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException();
                output.Write(buffer, 0, count);
            }
            stage.Token.ThrowIfCancellationRequested();
            if (!sameProcess()) throw new ProcessChangedException();
            using var json = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            ValidateObject(json.RootElement);
            var code = Get(json.RootElement, "code").ToString().ToLowerInvariant();
            if (code is "16" or "unauthenticated" or "7" or "permission_denied") throw new LocalAuthenticationException();
            if (code is "8" or "resource_exhausted") throw new LocalThrottleException();
            return json.RootElement.Clone();
        }
        ProviderReading Finish(ProviderReading reading)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!sameProcess()) throw new ProcessChangedException();
            return reading;
        }
        try
        {
            async Task<JsonElement> TryRead(string method, bool reserveFallback)
            {
                try { return await Read(method, reserveFallback).ConfigureAwait(false); }
                catch (Exception error) when (error is LocalUnavailableException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!sameProcess()) throw new ProcessChangedException();
                    return default;
                }
            }
            var summary = await TryRead("RetrieveUserQuotaSummary", true).ConfigureAwait(false);
            var reading = ParseSummary(summary, DateTimeOffset.UtcNow);
            var status = await TryRead("GetUserStatus", true).ConfigureAwait(false);
            if (reading.Windows.Count > 0) return Finish(reading with { Plan = Plan(status) });
            reading = ParseLegacy(status, DateTimeOffset.UtcNow);
            if (reading.Windows.Count > 0) return Finish(reading);
            var models = await TryRead("GetCommandModelConfigs", false).ConfigureAwait(false);
            reading = ParseLegacy(models, DateTimeOffset.UtcNow);
            return Finish(reading.Windows.Count > 0 ? reading with { Plan = Plan(status) ?? reading.Plan } : Failure(ReadingState.Unavailable));
        }
        catch (LocalThrottleException) { return Failure(ReadingState.Unavailable); }
        catch (LocalAuthenticationException) { return Failure(ReadingState.NeedsAuth); }
        catch (ProcessChangedException) { return Failure(ReadingState.Unavailable); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or HttpRequestException or LocalUnavailableException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return Failure(ReadingState.Error);
        }
    }
    public static bool ValidCsrf(string? value) => value is { Length: > 0 and <= 4096 }
        && value.All(c => c >= 0x21 && c <= 0x7e);
    private static ProviderReading Failure(ReadingState state) => new("gemini", state, [],
        Message: state == ReadingState.NeedsAuth ? "Sign in to the running Antigravity IDE, then refresh."
            : "Antigravity Local IDE usage is unavailable. Open one signed-in IDE and refresh.");
    private sealed class ProcessChangedException : Exception { }
    private sealed class LocalThrottleException : Exception { }
    private sealed class LocalAuthenticationException : Exception { }
    private sealed class LocalUnavailableException : Exception { }
    public static ProviderReading ParseSummary(JsonElement root, DateTimeOffset now)
    {
        if (!Successful(root)) return Failure(ReadingState.Unavailable);
        try { ValidateObject(root); } catch (InvalidDataException) { return Failure(ReadingState.Error); }
        var payload = Get(root, "response").ValueKind == JsonValueKind.Object ? Get(root, "response")
            : Get(root, "summary").ValueKind == JsonValueKind.Object ? Get(root, "summary") : root;
        var groups = Get(payload, "groups"); if (groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() > 128) return Failure(ReadingState.Unavailable);
        var windows = new List<LimitWindow>(); var seen = new HashSet<string>(StringComparer.Ordinal); var unknown = false;
        foreach (var group in groups.EnumerateArray())
        {
            var buckets = Get(group, "buckets"); if (buckets.ValueKind != JsonValueKind.Array) { unknown = true; continue; }
            if (buckets.GetArrayLength() > 128 || windows.Count + buckets.GetArrayLength() > 512) return Failure(ReadingState.Error);
            var groupName = Label(Text(group, "displayName") ?? Text(group, "name")) ?? "Quota";
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (Get(bucket, "disabled").ValueKind == JsonValueKind.True) continue;
                var id = Label(Text(bucket, "bucketId") ?? Text(bucket, "id"));
                if (id is null) { unknown = true; continue; }
                var remaining = Fraction(Get(bucket, "remainingFraction"));
                var nested = Get(bucket, "remaining");
                remaining ??= Fraction(Get(nested, "remainingFraction"));
                if (remaining is null && Text(nested, "case") == "remainingFraction") remaining = Fraction(Get(nested, "value"));
                if (remaining is null) { unknown = true; continue; }
                var identity = groupName + "/" + id;
                if (!seen.Add(identity)) return Failure(ReadingState.Error);
                var label = Label(Text(bucket, "displayName") ?? Text(bucket, "name")) ?? id;
                windows.Add(new("local/" + identity, groupName == label ? label : groupName + " · " + label,
                    (1 - remaining.Value) * 100, Reset(bucket), Cadence(id, label)));
            }
        }
        return windows.Count == 0 ? Failure(ReadingState.Unavailable)
            : new("gemini", unknown ? ReadingState.Partial : ReadingState.Ready, windows, now, Message: "Local IDE · This PC");
    }
    public static ProviderReading ParseLegacy(JsonElement root, DateTimeOffset now)
    {
        if (!Successful(root)) return Failure(ReadingState.Unavailable);
        try { ValidateObject(root); } catch (InvalidDataException) { return Failure(ReadingState.Error); }
        var status = Get(root, "userStatus");
        var models = Get(Get(status, "cascadeModelConfigData"), "clientModelConfigs");
        if (models.ValueKind != JsonValueKind.Array) models = Get(root, "clientModelConfigs");
        if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > 512) return Failure(ReadingState.Unavailable);
        var windows = new List<LimitWindow>(); var seen = new HashSet<string>(StringComparer.Ordinal); var unknown = false;
        foreach (var model in models.EnumerateArray())
        {
            var id = Label(Text(Get(model, "modelOrAlias"), "model")); var label = Label(Text(model, "label"));
            var quota = Get(model, "quotaInfo"); var remaining = Fraction(Get(quota, "remainingFraction"));
            if (id is null || label is null || remaining is null) { unknown = true; continue; }
            if (!seen.Add(id)) return Failure(ReadingState.Error);
            windows.Add(new("local/model/" + id, label, (1 - remaining.Value) * 100, Reset(quota)));
        }
        return windows.Count == 0 ? Failure(ReadingState.Unavailable)
            : new("gemini", unknown ? ReadingState.Partial : ReadingState.Ready, windows, now, Message: "Local IDE · This PC", Plan: Plan(root));
    }
    private static string? Plan(JsonElement root)
    {
        if (!Successful(root)) return null;
        var status = Get(root, "userStatus");
        var plan = Label(Text(Get(status, "userTier"), "name"));
        var info = Get(Get(status, "planStatus"), "planInfo");
        foreach (var key in new[] { "planDisplayName", "displayName", "productName", "planName", "planShortName" }) plan ??= Label(Text(info, key));
        return plan;
    }
    private static bool Successful(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var code = Get(root, "code");
        return code.ValueKind == JsonValueKind.Undefined || code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) && number == 0
            || code.ValueKind == JsonValueKind.String && code.GetString()?.ToLowerInvariant() is "0" or "ok" or "success";
    }
    private static void ValidateObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var pending = new Stack<JsonElement>(); pending.Push(root); var count = 0;
        while (pending.TryPop(out var element))
        {
            if (++count > 20000) throw new InvalidDataException();
            if (element.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) { if (!keys.Add(property.Name)) throw new InvalidDataException(); pending.Push(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) pending.Push(value);
        }
    }
    private static double? Fraction(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
        && double.IsFinite(number) && number is >= 0 and <= 1 ? number : null;
    private static string? Label(string? value) => value?.Trim() is { Length: > 0 and <= 256 } text && !text.Any(char.IsControl) ? text : null;
    private static DateTimeOffset? Reset(JsonElement root) => Date(Get(root, "resetTime"));
    private static int Cadence(string id, string label)
    {
        var candidates = new List<string>();
        foreach (var value in new[] { id, label })
        {
            var normalized = value.Trim().ToLowerInvariant().Replace('_', '-');
            candidates.Add(normalized);
            if (normalized.EndsWith(" limit", StringComparison.Ordinal)) candidates.Add(normalized[..^6]);
        }
        foreach (var alias in new[] { "session", "5h", "5-hour", "5hour", "five hour", "five-hour" })
            if (candidates.Any(value => value == alias || value.EndsWith("-" + alias, StringComparison.Ordinal))) return 300;
        return candidates.Any(value => value is "week" or "weekly" || value.EndsWith("-weekly", StringComparison.Ordinal)) ? 10080 : 0;
    }
}
