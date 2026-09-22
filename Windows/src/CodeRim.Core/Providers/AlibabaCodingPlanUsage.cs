using System.Globalization;
using System.Text.Json;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Providers;

public static class AlibabaCodingPlanUsage
{
    private static readonly string[] MessageNames = ["message", "msg", "statusMessage", "status_msg"];
    private static readonly string[] CodeNames = ["code", "statusCode", "status_code"];
    private static readonly string[] QuotaPrefixes = ["per5Hour", "perFiveHour", "perWeek", "perBillMonth", "perMonth"];
    private static readonly string[] PlanNames = ["planName", "plan_name", "packageName", "package_name", "instanceName", "instance_name"];
    private static readonly string[] StatusNames = ["status", "instanceStatus"];
    private static readonly string[] ExpiryNames = ["endTime", "periodEndTime", "expireTime", "expirationTime"];
    private static InvalidDataException Invalid() => new("Coding Plan returned unsupported quota data.");
    private static JsonElement Get(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) ? value : default;
    private static JsonElement Decode(JsonElement root)
    {
        for (var i = 0; i < 8 && root.ValueKind == JsonValueKind.String && root.GetString()?.Trim() is { Length: > 1 } text && text[0] is '{' or '['; i++)
        { using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 }); root = json.RootElement.Clone(); }
        return root;
    }
    private static List<JsonElement> Contexts(JsonElement root)
    {
        var result = new List<JsonElement>(); var nodes = 0;
        void Visit(JsonElement value, int depth)
        {
            if (++nodes > 32768 || depth > 16) throw Invalid();
            value = Decode(value);
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal); result.Add(value);
                foreach (var p in value.EnumerateObject()) { if (!names.Add(p.Name)) throw Invalid(); Visit(p.Value, depth + 1); }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            { if (value.GetArrayLength() > 4096) throw Invalid(); foreach (var item in value.EnumerateArray()) Visit(item, depth + 1); }
        }
        Visit(root, 0); return result;
    }
    private static string? Text(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString()?.Trim() is not { Length: > 0 and <= 256 } text || text.Any(char.IsControl)) throw Invalid();
        return text;
    }
    private static string? Name(IEnumerable<JsonElement> frames)
        => frames.SelectMany(x => PlanNames.Select(key => Text(Get(x, key)))).FirstOrDefault(x => x is not null);
    private static long? Count(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0) return count;
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 20 } text
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count >= 0) return count;
        throw Invalid();
    }
    private static DateTimeOffset? Date(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
        if (text is not { Length: > 0 and <= 128 }) throw Invalid();
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) && epoch > 0)
        {
            try { return epoch >= 1000000000000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch); }
            catch (ArgumentOutOfRangeException) { throw Invalid(); }
        }
        if (value.ValueKind == JsonValueKind.Number || text.Length < 10 || text[4] != '-' || text[7] != '-') throw Invalid();
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : throw Invalid();
    }
    private static bool Equivalent(JsonElement left, JsonElement right, string? key = null)
    {
        left = Decode(left); right = Decode(right);
        if (key is not null && QuotaPrefixes.Any(prefix => key == prefix + "UsedQuota" || key == prefix + "TotalQuota")) return Count(left) == Count(right);
        if (key is not null && QuotaPrefixes.Any(prefix => key == prefix + "QuotaNextRefreshTime")) return Date(left) == Date(right);
        if (left.ValueKind == JsonValueKind.Object && right.ValueKind == JsonValueKind.Object)
            return left.EnumerateObject().Count() == right.EnumerateObject().Count()
                && left.EnumerateObject().All(field => right.TryGetProperty(field.Name, out var other) && Equivalent(field.Value, other, field.Name));
        if (left.ValueKind == JsonValueKind.Array && right.ValueKind == JsonValueKind.Array)
            return left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Equivalent(pair.First, pair.Second));
        return JsonElement.DeepEquals(left, right);
    }
    private static void ValidateAliases(JsonElement frame, string canonical, string alias)
    {
        static void Match<T>(T? left, T? right) where T : struct
        { if (left.HasValue && right.HasValue && !EqualityComparer<T>.Default.Equals(left.Value, right.Value)) throw Invalid(); }
        Match(Count(Get(frame, canonical + "UsedQuota")), Count(Get(frame, alias + "UsedQuota")));
        Match(Count(Get(frame, canonical + "TotalQuota")), Count(Get(frame, alias + "TotalQuota")));
        Match(Date(Get(frame, canonical + "QuotaNextRefreshTime")), Date(Get(frame, alias + "QuotaNextRefreshTime")));
    }
    private static int Active(JsonElement root, DateTimeOffset now)
    {
        var status = StatusNames.Select(x => Text(Get(root, x))).FirstOrDefault(x => x is not null)?.ToUpperInvariant();
        if (status is "ACTIVE" or "VALID") return 3;
        if (status is "EXPIRED" or "INVALID" or "INACTIVE" or "DISABLED" or "TERMINATED" or "STOPPED") return -1;
        var flag = Get(root, "isActive"); if (flag.ValueKind == JsonValueKind.Undefined) flag = Get(root, "active");
        if (flag.ValueKind == JsonValueKind.True) return 3;
        if (flag.ValueKind == JsonValueKind.False) return -1;
        if (flag.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            var text = flag.ValueKind == JsonValueKind.String ? flag.GetString()?.Trim().ToLowerInvariant() : flag.GetRawText();
            if (text is "true" or "1" or "yes" or "active" or "valid") return 3;
            if (text is "false" or "0" or "no" or "inactive" or "invalid" or "expired") return -1;
            throw Invalid();
        }
        if (flag.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) throw Invalid();
        return ExpiryNames.Select(x => Date(Get(root, x))).Any(x => x > now) ? 1 : 0;
    }
    public static ProviderReading Parse(JsonElement root, DateTimeOffset? now = null, bool web = false)
    {
        var time = now ?? DateTimeOffset.UtcNow;
        if (root.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || root.GetRawText().Length > 2 * 1024 * 1024 || Decode(root).ValueKind != JsonValueKind.Object) throw Invalid();
        var frames = Contexts(root);
        ProviderReading LoginRequired() => new("alibaba", web ? ReadingState.NeedsAuth : ReadingState.Unsupported, [], Message: web
            ? "Reconnect the selected Coding Plan Web session." : "This account requires Coding Plan Web mode. Its console does not expose this quota through API keys.");
        foreach (var frame in frames)
        {
            var status = Get(frame, "status");
            if (status.ValueKind == JsonValueKind.String && status.GetString()?.Contains("login", StringComparison.OrdinalIgnoreCase) == true) return LoginRequired();
            foreach (var key in MessageNames)
            {
                var message = Get(frame, key);
                if (message.ValueKind != JsonValueKind.String) continue;
                var text = message.GetString()!;
                if (text.Contains("login", StringComparison.OrdinalIgnoreCase) || text.Contains("log in", StringComparison.OrdinalIgnoreCase)
                    || !web && text.Contains("console session", StringComparison.OrdinalIgnoreCase)) return LoginRequired();
            }
        }
        foreach (var frame in frames)
            foreach (var key in CodeNames)
            {
                var value = Get(frame, key); if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                var code = value.ValueKind == JsonValueKind.String ? Text(value) : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : throw Invalid();
                if (code?.Contains("login", StringComparison.OrdinalIgnoreCase) == true) return LoginRequired();
                if (code is "401" or "403" or "Unauthorized" or "InvalidApiKey" or "TokenError") return new("alibaba", ReadingState.NeedsAuth, [], Message: "Reconnect the selected Coding Plan account.");
                if (code is "0" or "200") continue;
                var message = Text(Get(frame, "statusMessage")) ?? Text(Get(frame, "status_msg")) ?? Text(Get(frame, "message"));
                if (message?.Contains("api key", StringComparison.OrdinalIgnoreCase) == true) return new("alibaba", ReadingState.NeedsAuth, [], Message: "Update the Coding Plan API key.");
                throw Invalid();
            }
        foreach (var frame in frames)
        {
            var success = Get(frame, "success");
            if (success.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.True)) throw Invalid();
            ValidateAliases(frame, "per5Hour", "perFiveHour");
            ValidateAliases(frame, "perBillMonth", "perMonth");
        }
        var arrays = frames.Select(x => Decode(Get(x, "codingPlanInstanceInfos"))).Concat(frames.Select(x => Decode(Get(x, "coding_plan_instance_infos"))))
            .Where(x => x.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)).ToArray();
        if (arrays.Any(x => x.ValueKind != JsonValueKind.Array)) throw Invalid();
        if (arrays.Length > 1 && arrays.Skip(1).Any(array => !Equivalent(arrays[0], array))) throw Invalid();
        var instances = arrays.FirstOrDefault().ValueKind == JsonValueKind.Array ? arrays[0].EnumerateArray().ToArray() : [];
        if (instances.Any(x => x.ValueKind != JsonValueKind.Object)) throw Invalid();
        var ranked = instances.Select((item, index) => (Item: item, Score: Active(item, time), Index: index)).OrderByDescending(x => x.Score).ThenBy(x => x.Index).ToArray();
        var selected = ranked.Length > 0 && ranked[0].Score > 0 ? ranked[0].Item : instances.FirstOrDefault();
        var selectedFrames = selected.ValueKind == JsonValueKind.Object ? Contexts(selected) : frames;
        var active = selected.ValueKind == JsonValueKind.Object ? Active(selected, time) > 0 : frames.Any(x => Active(x, time) > 0);
        var plan = Name(selectedFrames) ?? (instances.Length <= 1 ? Name(frames) : null);
        static bool Quota(JsonElement frame) => frame.EnumerateObject().Any(x => x.Name.StartsWith("per", StringComparison.Ordinal) && (x.Name.EndsWith("UsedQuota", StringComparison.Ordinal) || x.Name.EndsWith("TotalQuota", StringComparison.Ordinal)));
        var quota = selectedFrames.FirstOrDefault(Quota);
        if (quota.ValueKind == JsonValueKind.Undefined && !(instances.Length > 1 && active)) quota = frames.FirstOrDefault(Quota);
        foreach (var frame in frames)
            foreach (var prefix in QuotaPrefixes)
            { _ = Count(Get(frame, prefix + "UsedQuota")); _ = Count(Get(frame, prefix + "TotalQuota")); _ = Date(Get(frame, prefix + "QuotaNextRefreshTime")); }
        var windows = new List<LimitWindow>();
        void Add(string id, string label, int minutes, string prefix, string? alias = null)
        {
            var used = Count(Get(quota, prefix + "UsedQuota")); var total = Count(Get(quota, prefix + "TotalQuota")); var reset = Date(Get(quota, prefix + "QuotaNextRefreshTime"));
            if (alias is not null) { used ??= Count(Get(quota, alias + "UsedQuota")); total ??= Count(Get(quota, alias + "TotalQuota")); reset ??= Date(Get(quota, alias + "QuotaNextRefreshTime")); }
            if (used is null || total is not > 0) return;
            if (minutes == 300 && reset is { } date && date < time.AddSeconds(60)) reset = date.AddHours(5) >= time.AddSeconds(60) ? date.AddHours(5) : time.AddHours(5);
            windows.Add(new(id, label, (double)((decimal)Math.Min(used.Value, total.Value) / total.Value * 100), reset, minutes,
                UsedCount: used, RemainingCount: Math.Max(0, total.Value - used.Value), Unit: "requests", DisplayValue: $"{used.Value:N0} / {total.Value:N0} requests"));
        }
        Add("five-hour", "5-hour limit", 300, "per5Hour", "perFiveHour"); Add("weekly", "Weekly limit", 10080, "perWeek"); Add("monthly", "Monthly limit", 43200, "perBillMonth", "perMonth");
        var hasReportedTotal = QuotaPrefixes.Any(prefix => Count(Get(quota, prefix + "TotalQuota")).HasValue);
        if (windows.Count == 0 && !(active && plan is not null) && !hasReportedTotal) throw Invalid();
        return new("alibaba", ReadingState.Ready, windows, time, windows.Count == 0 ? active && plan is not null ? "The plan is active; quota counters are not reported." : "The console has not reported complete quota counters." : null, plan);
    }
}
