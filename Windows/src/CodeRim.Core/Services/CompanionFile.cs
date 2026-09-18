using System.Text.Json;
using System.Text.Json.Serialization;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

public sealed record CompanionSnapshot(int SchemaVersion, DateTimeOffset GeneratedAt, IReadOnlyList<CompanionProvider> Providers);
public sealed record CompanionProvider(string Id, string Name, bool Enabled, LocalUsage? LocalUsage, ProviderReading Limits);
public sealed record LocalUsage(string Scope, string State, DateTimeOffset? UpdatedAt, DateTimeOffset PeriodsAsOf,
    string TimeZoneIdentifier, Dictionary<string, TokenUsage> Totals);
public static class CompanionFile
{
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public static string DataDirectory => Environment.GetEnvironmentVariable("CODERIM_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeRim");
    public static string SnapshotPath => Path.Combine(DataDirectory, "snapshot.json");
    public static CompanionSnapshot Read(string? path = null)
    {
        path ??= SnapshotPath;
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Snapshot is too large.");
        var result = JsonSerializer.Deserialize<CompanionSnapshot>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Snapshot is empty.");
        Validate(result);
        return result with { Providers = result.Providers.Select(p => p with { Limits = p.Limits.Evaluated(DateTimeOffset.Now), LocalUsage = p.LocalUsage is { } local &&
            (DateTimeOffset.Now - local.PeriodsAsOf > TimeSpan.FromMinutes(5) || local.PeriodsAsOf > DateTimeOffset.Now.AddMinutes(1)
            || local.PeriodsAsOf.LocalDateTime.Date != DateTime.Today || local.TimeZoneIdentifier != TimeZoneInfo.Local.Id) ? local with { State = local.State == "partial" ? "partial" : "stale" } : p.LocalUsage }).ToArray() };
    }
    public static void Write(CompanionSnapshot snapshot, string? path = null)
    {
        Validate(snapshot);
        path ??= SnapshotPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions)); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }
    private static void Validate(CompanionSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.GeneratedAt == default || snapshot.Providers is null || snapshot.Providers.Count > 70)
            throw new InvalidDataException("Invalid snapshot structure or schema.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in snapshot.Providers)
        {
            if (provider is null || ProviderCatalog.Find(provider.Id) is null || !ids.Add(provider.Id) || string.IsNullOrWhiteSpace(provider.Name)
                || provider.Limits is null || provider.Limits.Id != provider.Id || !Enum.IsDefined(provider.Limits.State)
                || provider.Limits.Windows is null || provider.Limits.Windows.Count > 512)
                throw new InvalidDataException("Invalid provider reading.");
            provider.Limits.CostUsage?.Validate();
            var windows = new HashSet<string>(StringComparer.Ordinal);
            foreach (var window in provider.Limits.Windows)
                if (window is null || string.IsNullOrWhiteSpace(window.Id) || !windows.Add(window.Id) || string.IsNullOrWhiteSpace(window.Name)
                    || window.DurationMinutes < 0 || window.UsedCount < 0 || window.RemainingCount < 0
                    || window.UsedPercent is { } percent && (!double.IsFinite(percent) || percent < 0))
                    throw new InvalidDataException("Invalid quota window.");
            if (provider.LocalUsage is { } local && (local.Scope != "this-pc" || local.Totals is null || local.Totals.Count > 4
                || local.Totals.Any(x => x.Key is not ("today" or "week" or "month" or "all-time") || !x.Value.IsValid)))
                throw new InvalidDataException("Invalid local usage.");
        }
    }
    public static LocalUsage Local(UsageSnapshot snapshot, DateTimeOffset now) => new("this-pc", snapshot.Quality == DataQuality.Exact ? "ready" : snapshot.Quality == DataQuality.Partial ? "partial" : "unavailable", snapshot.UpdatedAt,
        now, TimeZoneInfo.Local.Id, new Dictionary<string, TokenUsage>(StringComparer.Ordinal) { ["today"] = snapshot.Today, ["week"] = snapshot.Week, ["month"] = snapshot.Month, ["all-time"] = snapshot.AllTime });
}
