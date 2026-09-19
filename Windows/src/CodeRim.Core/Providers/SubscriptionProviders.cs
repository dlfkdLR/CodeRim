using System.Globalization;
using System.Net;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public sealed partial class NativeProviders
{
    private static readonly string[] KiloResetKeys = ["resetAt", "resetsAt", "nextResetAt", "renewAt", "renewsAt", "nextRenewalAt", "currentPeriodEnd", "periodEndsAt", "expiresAt", "expiryAt"];
    private static readonly HashSet<string> SubscriptionIds = new(StringComparer.Ordinal) { "kilo", "devin", "minimax" };
    public static string[]? CredentialKeys(string id) => id switch
    {
        "kimi" => ["KIMI_CODE_API_KEY"], "minimax" => ["MINIMAX_CODING_API_KEY", "MINIMAX_API_KEY"],
        "devin" => ["DEVIN_BEARER_TOKEN", "DEVIN_AUTHORIZATION"],
        "longcat" => ["LONGCAT_MANUAL_COOKIE", "longcat_manual_cookie"],
        "factory" => ["FACTORY_API_KEY"], "zed" => ["ZED_ACCESS_TOKEN"], "groq" => ["GROQ_API_KEY"], "mistral" => ["MISTRAL_COOKIE", "MISTRAL_COOKIE_HEADER"], "zoommate" => ["ZOOMMATE_BEARER_TOKEN"], "notion" => ["NOTION_COOKIE", "NOTION_COOKIE_HEADER"], "alibaba" => ["ALIBABA_CODING_PLAN_API_KEY", "ALIBABA_QWEN_API_KEY", "DASHSCOPE_API_KEY"], "gemini-cli" => ["GEMINI_OAUTH_ACCESS_TOKEN"], "vertexai" => ["GOOGLE_OAUTH_ACCESS_TOKEN"], "alibabatokenplan" => ["ALIBABA_TOKEN_PLAN_COOKIE"], "qwencloud" => ["QWEN_CLOUD_COOKIE"], "augment" => ["AUGMENT_COOKIE", "AUGMENT_COOKIE_HEADER"], "kiro" => ["KIRO_ACCESS_TOKEN"], "azureopenai" => ["AZURE_OPENAI_API_KEY"], _ => null
    };
    private static string KiloEndpoint() => "https://app.kilo.ai/api/trpc/user.getCreditBlocks,kiloPass.getState,user.getAutoTopUpPaymentMethod?batch=1&input=" +
        Uri.EscapeDataString("""{"0":{"json":null},"1":{"json":null},"2":{"json":null}}""");
    private static (string[] Paths, string? InternalId) DevinPaths(Func<string, string?> setting)
    {
        var org = (setting("DEVIN_ORGANIZATION") ?? setting("DEVIN_ORG") ?? "").Trim().Trim('/');
        if (Uri.TryCreate(org, UriKind.Absolute, out var url) && (url.Host == "devin.ai" || url.Host.EndsWith(".devin.ai", StringComparison.Ordinal)))
            org = url.AbsolutePath.Trim('/');
        var parts = org.Split('/');
        if (parts.Length > 1 && parts[0] is "org" or "organizations") org = string.Join('/', parts.Take(2));
        else if (parts.Length == 1) org = (org.StartsWith("org-", StringComparison.Ordinal) || org.StartsWith("org_", StringComparison.Ordinal) ? "organizations/" : "org/") + org;
        else throw new InvalidDataException("Set the Devin organization ID or slug.");
        if (org.Length > 256 || org.Split('/').Any(x => x.Length == 0 || x.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))) throw new InvalidDataException();
        var internalId = org.StartsWith("organizations/", StringComparison.Ordinal) ? org["organizations/".Length..] : null;
        var paths = new List<string>(); if (internalId is not null) paths.Add(internalId);
        paths.Add(org); if (org.StartsWith("org/", StringComparison.Ordinal)) paths.Add(org[4..]);
        return (paths.Distinct().Select(x => "https://app.devin.ai/api/" + x + "/billing/quota/usage").ToArray(), internalId);
    }
    private static async Task<ProviderReading> FetchSubscription(string id, Func<string, string?> setting, Func<string, Task<JsonElement>> get)
    {
        if (id == "kilo") return ParseSubscription(id, await get(KiloEndpoint()).ConfigureAwait(false));
        if (id == "devin")
        {
            var paths = DevinPaths(setting).Paths;
            for (var index = 0; index < paths.Length; index++)
            {
                try { return ParseSubscription(id, await get(paths[index]).ConfigureAwait(false)); }
                catch (ProviderRequestException error) when (error.Status == HttpStatusCode.NotFound && index < paths.Length - 1) { }
            }
            throw new InvalidDataException();
        }
        var region = setting("MINIMAX_REGION")?.Trim().ToLowerInvariant() ?? "global";
        if (region is not ("global" or "cn")) throw new InvalidDataException("MiniMax region must be global or cn.");
        var host = region == "cn" ? "https://api.minimaxi.com" : "https://api.minimax.io";
        var rejectedCredential = false;
        try
        {
            var reading = ParseSubscription(id, await get(host + "/v1/token_plan/remains").ConfigureAwait(false));
            if (reading.State == ReadingState.Ready) return reading;
        }
        catch (ProviderRequestException error) when (error.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { rejectedCredential = error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException) { }
        try { return ParseSubscription(id, await get(host + "/v1/api/openplatform/coding_plan/remains").ConfigureAwait(false)); }
        catch (Exception error) when (rejectedCredential && error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException)
        { throw new ProviderRequestException(HttpStatusCode.Unauthorized); }
    }
    private static IEnumerable<JsonElement> Contexts(JsonElement root, int depth = 0)
    {
        if (depth > 10) yield break;
        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
            foreach (var property in root.EnumerateObject())
                foreach (var context in Contexts(property.Value, depth + 1)) yield return context;
        }
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray())
                foreach (var context in Contexts(item, depth + 1)) yield return context;
    }
    private static double? FirstNumeric(JsonElement root, params string[] keys)
    {
        foreach (var context in Contexts(root)) foreach (var key in keys) if (Numeric(context, key) is { } value) return value;
        return null;
    }
    private static string? FirstText(JsonElement root, params string[] keys)
    {
        foreach (var context in Contexts(root)) foreach (var key in keys) if (Text(context, key) is { Length: > 0 } value) return value;
        return null;
    }
    private static DateTimeOffset? EpochDate(JsonElement root, string key)
    {
        var value = Numeric(root, key);
        if (value.HasValue)
        {
            var seconds = value > 10_000_000_000 ? value / 1000 : value;
            return seconds is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds.Value) : null;
        }
        return Date(Get(root, key));
    }
    private static ProviderReading ParseSubscription(string id, JsonElement root)
    {
        var windows = new List<LimitWindow>(); string? plan = null;
        if (id == "devin")
        {
            foreach (var (period, minutes) in new[] { ("daily", 1440), ("weekly", 10080) })
            {
                if (period == "daily" && Get(root, "hide_daily_quota").ValueKind == JsonValueKind.True) continue;
                var percent = Numeric(root, period + "_percentage"); DateTimeOffset? reset = EpochDate(root, period + "_reset_at");
                if (percent is >= 0) percent = percent < 1 ? percent * 100 : percent;
                else foreach (var context in Contexts(root))
                {
                    foreach (var property in context.EnumerateObject().Where(x => !x.Name.Contains("hide", StringComparison.OrdinalIgnoreCase)
                        && (x.Name.Contains(period, StringComparison.OrdinalIgnoreCase) || x.Name.Contains(period == "daily" ? "day" : "week", StringComparison.OrdinalIgnoreCase))))
                    {
                        var item = property.Value;
                        double? direct = null;
                        foreach (var key in new[] { "used_percent", "usedPercent", "usage_percent", "usagePercent", "percent_used", "percentUsed", "percent" })
                            if (Numeric(item, key) is { } value) { direct = value; break; }
                        if (direct.HasValue) percent = direct <= 1 ? direct * 100 : direct;
                        else
                        {
                            var left = FirstNumeric(item, "remaining_percent", "remainingPercent", "percent_remaining", "percentRemaining");
                            if (left.HasValue) percent = 100 - (left <= 1 ? left * 100 : left);
                            else if (FirstNumeric(item, "limit", "quota", "total", "max") is > 0 and var limit)
                            {
                                var used = FirstNumeric(item, "used", "usage", "used_count", "usedCount", "consumed");
                                used ??= limit - FirstNumeric(item, "remaining", "left", "available");
                                if (used is >= 0) percent = used / limit * 100;
                            }
                        }
                        if (!percent.HasValue) continue;
                        reset = item.ValueKind == JsonValueKind.Object ? item.EnumerateObject().Where(x => x.Name.Contains("reset", StringComparison.OrdinalIgnoreCase))
                            .Select(x => EpochDate(item, x.Name)).FirstOrDefault(x => x.HasValue) : null;
                        break;
                    }
                    if (percent.HasValue) break;
                }
                if (percent is >= 0) windows.Add(new(period, period == "daily" ? "Daily limit" : "Weekly limit", Math.Clamp(percent.Value, 0, 100), reset, minutes));
            }
            plan = FirstText(root, "plan_name", "planName", "plan", "tier", "subscription_tier", "subscriptionTier");
            var balance = Numeric(root, "overage_balance") ?? Numeric(root, "overage_balance_cents") / 100;
            if (balance is >= 0) windows.Add(new("balance", "Extra usage balance", Unit: "USD", DisplayValue: $"{balance:N2} USD"));
        }
        else if (id == "kilo")
        {
            JsonElement Payload(int index)
            {
                var entry = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > index ? root[index]
                    : root.ValueKind == JsonValueKind.Object ? Get(root, index.ToString(CultureInfo.InvariantCulture)) : default;
                if (index == 0 && Get(root, "result").ValueKind == JsonValueKind.Object) entry = root;
                var error = Get(entry, "error");
                if (index < 2 && error.ValueKind == JsonValueKind.Object)
                {
                    var code = string.Join(" ", Contexts(error).SelectMany(x => new[] { Text(x, "code"), Text(x, "message") }).Where(x => x is not null));
                    if (code.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || code.Contains("forbidden", StringComparison.OrdinalIgnoreCase)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                    throw new InvalidDataException();
                }
                var result = Get(entry, "result"); var data = Get(result, "data");
                var json = Get(data, "json"); if (json.ValueKind != JsonValueKind.Undefined) return json;
                return data.ValueKind == JsonValueKind.Object ? data : Get(result, "json");
            }
            var credits = Payload(0); var pass = Payload(1);
            var blocks = Contexts(credits).Select(x => Get(x, "creditBlocks")).FirstOrDefault(x => x.ValueKind == JsonValueKind.Array);
            double? total = null, left = null, used = null;
            if (blocks.ValueKind == JsonValueKind.Array && blocks.GetArrayLength() > 0)
            {
                var all = blocks.EnumerateArray().ToArray();
                if (all.All(x => Numeric(x, "amount_mUsd").HasValue)) total = all.Sum(x => Numeric(x, "amount_mUsd")!.Value) / 1_000_000;
                if (all.All(x => Numeric(x, "balance_mUsd").HasValue)) left = all.Sum(x => Numeric(x, "balance_mUsd")!.Value) / 1_000_000;
                used = total - left;
            }
            else
            {
                used = FirstNumeric(credits, "used", "usedCredits", "consumed", "spent", "creditsUsed");
                total = FirstNumeric(credits, "total", "totalCredits", "creditsTotal", "limit");
                left = FirstNumeric(credits, "remaining", "remainingCredits", "creditsRemaining") ?? FirstNumeric(credits, "totalBalance_mUsd") / 1_000_000;
                total ??= used + left; used ??= total - left;
            }
            if (left is >= 0) windows.Add(new("credits", "Credit balance", total is > 0 && used is >= 0 ? used / total * 100 : null, Unit: "USD", DisplayValue: $"{left:N2} USD remaining"));
            var subscription = Get(pass, "subscription");
            if (subscription.ValueKind == JsonValueKind.Undefined) subscription = pass;
            var spent = Numeric(subscription, "currentPeriodUsageUsd");
            var allowance = Numeric(subscription, "currentPeriodBaseCreditsUsd") + (Numeric(subscription, "currentPeriodBonusCreditsUsd") ?? 0);
            plan = Text(subscription, "tier") ?? FirstText(pass, "planName", "tierName", "passName", "subscriptionName");
            var renewal = EpochDate(subscription, "nextBillingAt") ?? EpochDate(subscription, "nextRenewalAt") ?? EpochDate(subscription, "renewsAt") ?? EpochDate(subscription, "renewAt");
            double? bonus = Numeric(subscription, "currentPeriodBonusCreditsUsd");
            if (spent is null && allowance is null && Get(pass, "subscription").ValueKind != JsonValueKind.Null)
            {
                double? Money(string[] cents, string[] micro, string[] plain) => FirstNumeric(pass, cents) / 100 ?? FirstNumeric(pass, micro) / 1_000_000 ?? FirstNumeric(pass, plain);
                allowance = Money(["amountCents", "totalCents", "planAmountCents", "monthlyAmountCents", "limitCents", "includedCents", "valueCents"],
                    ["amount_mUsd", "total_mUsd", "planAmount_mUsd", "limit_mUsd", "included_mUsd", "value_mUsd"], ["amount", "total", "limit", "included", "value", "creditsTotal", "totalCredits", "planAmount"]);
                spent = Money(["usedCents", "spentCents", "consumedCents", "usedAmountCents", "consumedAmountCents"],
                    ["used_mUsd", "spent_mUsd", "consumed_mUsd", "usedAmount_mUsd"], ["used", "spent", "consumed", "usage", "creditsUsed", "usedAmount", "consumedAmount"]);
                var remaining = Money(["remainingCents", "remainingAmountCents", "availableCents", "leftCents", "balanceCents"],
                    ["remaining_mUsd", "available_mUsd", "left_mUsd", "balance_mUsd"], ["remaining", "available", "left", "balance", "creditsRemaining", "remainingAmount", "availableAmount"]);
                allowance ??= spent + remaining; spent ??= allowance - remaining;
                bonus = Money(["bonusCents", "bonusAmountCents", "includedBonusCents", "bonusRemainingCents"],
                    ["bonus_mUsd", "bonusAmount_mUsd"], ["bonus", "bonusAmount", "bonusCredits", "includedBonus"]);
                renewal = Contexts(pass).SelectMany(context => KiloResetKeys
                    .Select(key => EpochDate(context, key))).FirstOrDefault(x => x.HasValue);
            }
            if (spent is >= 0 && allowance is > 0)
                windows.Insert(0, new("pass", "Kilo Pass", spent / allowance * 100, renewal,
                    Unit: "USD", DisplayValue: $"{spent:N2} / {allowance:N2} USD used"));
            if (bonus is > 0) windows.Add(new("bonus", "Pass bonus", Unit: "USD", DisplayValue: $"{bonus:N2} USD"));
        }
        else if (id == "minimax")
        {
            var data = Get(root, "data"); if (data.ValueKind != JsonValueKind.Object) data = root;
            var status = Numeric(Get(data, "base_resp"), "status_code") ?? Numeric(Get(root, "base_resp"), "status_code");
            if (status == 1004 || (Text(Get(data, "base_resp"), "status_msg") ?? Text(Get(root, "base_resp"), "status_msg"))?.Trim().Equals("invalid api key", StringComparison.OrdinalIgnoreCase) == true) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            if (status.HasValue && status != 0) throw new InvalidDataException();
            plan = FirstText(data, "current_subscribe_title", "plan_name", "combo_title", "current_plan_title");
            var models = Get(data, "model_remains");
            if (models.ValueKind == JsonValueKind.Array)
                foreach (var model in models.EnumerateArray())
                {
                    var name = Text(model, "model_name"); if (name is null) continue;
                    foreach (var weekly in new[] { false, true })
                    {
                        if (weekly && !(name == "general" || name.Contains("minimax-m", StringComparison.OrdinalIgnoreCase) || name.StartsWith("m2.", StringComparison.OrdinalIgnoreCase))) continue;
                        var prefix = weekly ? "current_weekly_" : "current_interval_";
                        var total = Numeric(model, prefix + "total_count"); var left = Numeric(model, prefix + "usage_count");
                        var remaining = Numeric(model, prefix + "remaining_percent"); var unavailable = Numeric(model, prefix + "status") == 3;
                        var label = name + (weekly ? " · Weekly" : "");
                        if (unavailable && remaining is >= 100)
                        {
                            if (weekly) windows.Add(new(name + ".weekly", label, DisplayValue: "Unlimited"));
                            if (weekly || (total ?? 0) == 0 && (left ?? 0) == 0) continue;
                        }
                        var percent = remaining is >= 0 ? 100 - remaining : total is > 0 && left is >= 0 ? (total - left) / total * 100 : null;
                        if (!percent.HasValue) continue;
                        var start = EpochDate(model, weekly ? "weekly_start_time" : "start_time"); var end = EpochDate(model, weekly ? "weekly_end_time" : "end_time");
                        windows.Add(new(name + (weekly ? ".weekly" : ".interval"), label, Math.Clamp(percent.Value, 0, 100), end,
                            start.HasValue && end > start && (end.Value - start.Value).TotalMinutes < int.MaxValue ? (int)(end.Value - start.Value).TotalMinutes : weekly ? 10080 : 0));
                    }
                }
            var services = Get(data, "services");
            if (windows.Count == 0 && services.ValueKind == JsonValueKind.Array)
                foreach (var service in services.EnumerateArray())
                    if (Numeric(service, "limit") is > 0 and var limit && Numeric(service, "usage") is >= 0 and var used)
                        windows.Add(new("service." + windows.Count, (Text(service, "service_type") ?? "Usage") + " · " + Text(service, "window_type"),
                            Numeric(service, "percent") ?? used / limit * 100, DisplayValue: Text(service, "time_range")));
            var balance = FirstNumeric(data, "points_balance", "point_balance", "credits_balance", "credit_balance");
            if (balance is >= 0) windows.Add(new("points", "Points balance", Unit: "points", DisplayValue: $"{balance:N2} points"));
        }
        return Metered(id, windows, plan);
    }
}
