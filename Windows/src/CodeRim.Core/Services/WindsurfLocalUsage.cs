using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using Microsoft.Data.Sqlite;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Services;
public static class WindsurfLocalUsage
{
    public static string? Source(string? value) => value?.Trim().ToLowerInvariant() switch
    { null or "" or "web" => "web", "local" => "local", _ => null };
    public static ProviderReading Read(string path)
    {
        try
        {
            var values = LocalStateDatabase.Read(path, "windsurf.settings.cachedPlanInfo");
            if (!values.TryGetValue("windsurf.settings.cachedPlanInfo", out var bytes)) return Missing();
            foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UnicodeEncoding(false, false, true) })
            {
                try
                {
                    var json = encoding.GetString(bytes).Trim('\0', '\uFEFF');
                    using var document = JsonDocument.Parse(json);
                    return Parse(document.RootElement);
                }
                catch (Exception error) when (error is JsonException or DecoderFallbackException) { }
            }
            return new("windsurf", ReadingState.Error, [], Message: "The Windsurf cache could not be read.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or ArgumentException)
        { return new("windsurf", ReadingState.Unavailable, [], Message: "Select Windsurf's local state.vscdb file, then refresh."); }
    }
    public static ProviderReading Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return Missing();
        var windows = new List<LimitWindow>();
        var quota = Get(root, "quotaUsage"); var usage = Get(root, "usage");
        foreach (var (id, name, remainingKey, resetKey, totalKey, usedKey, countKey, unit) in new[] {
            ("daily", "Daily", "dailyRemainingPercent", "dailyResetAtUnix", "messages", "usedMessages", "remainingMessages", "messages"),
            ("weekly", "Weekly", "weeklyRemainingPercent", "weeklyResetAtUnix", "flowActions", "usedFlowActions", "remainingFlowActions", "flow actions") })
        {
            if (Number(quota, remainingKey) is >= 0 and <= 100 and var remaining)
                windows.Add(new(id, name, 100 - remaining, Date(Get(quota, resetKey))));
            else if (Count(usage, totalKey) is > 0 and var total && (Count(usage, usedKey) ?? (Count(usage, countKey) is { } left ? Math.Max(0, total - left) : null)) is { } rawUsed)
            {
                var used = Math.Clamp(rawUsed, 0, total);
                windows.Add(new(totalKey, totalKey == "messages" ? "Messages" : "Flow actions", (double)used / total * 100, UsedCount: used, RemainingCount: total - used, Unit: unit));
            }
        }
        return windows.Count == 0 ? Missing() : new("windsurf", ReadingState.Stale, windows,
            Message: "Local Windsurf cache · freshness and current account are not verified.", Plan: Text(root, "planName") is { } plan ? plan[..Math.Min(128, plan.Length)] : null);
    }
    private static ProviderReading Missing() => new("windsurf", ReadingState.Unavailable, [], Message: "No cached Windsurf quota is available. Open Windsurf and refresh.");
}
