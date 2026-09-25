using System.Reflection;
using System.Globalization;
using System.Text.Json;

namespace CodeRim.Core.Domain;

public enum ReadingState { Ready, Loading, Partial, Stale, NeedsAuth, Unsupported, Unavailable, Error, Disabled }
public sealed record LimitWindow(string Id, string Name, double? UsedPercent = null, DateTimeOffset? ResetsAt = null,
    int DurationMinutes = 0, long? UsedCount = null, long? RemainingCount = null, string? Unit = null, string? DisplayValue = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Group = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? AccountLimitName = null)
{
    public double? RemainingPercent => UsedPercent is { } value ? Math.Clamp(100 - value, 0, 100) : null;
}
public sealed record ProviderCostEntry(string Date, string? Model, long? InputTokens, long? OutputTokens,
    long? ReasoningTokens, long? Requests, double? Cost, double? EstimatedCost);
public sealed record ProviderCostUsage(string Currency, int HistoryDays, string HistoryLabel,
    string? WindowEnd, IReadOnlyList<ProviderCostEntry> Entries, bool AllowCredits = false)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Currency) || Currency.Length > 16 || string.IsNullOrWhiteSpace(HistoryLabel)
            || HistoryLabel.Length > 256 || HistoryDays is < 1 or > 366 || Entries is null || Entries.Count > 10000
            || WindowEnd is { } end && !ValidDate(end))
            throw new InvalidDataException("Invalid provider activity.");
        foreach (var row in Entries)
            if (row is null || !ValidDate(row.Date) || row.Model?.Length > 1024
                || row.InputTokens < 0 || row.OutputTokens < 0 || row.ReasoningTokens < 0 || row.Requests < 0
                || row.Cost is { } cost && (!double.IsFinite(cost) || !AllowCredits && cost < 0)
                || row.EstimatedCost is { } estimated && (!double.IsFinite(estimated) || !AllowCredits && estimated < 0)
                || !AllowCredits && row.EstimatedCost > row.Cost)
                throw new InvalidDataException("Invalid provider activity entry.");
    }
    private static bool ValidDate(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
public sealed record ProviderReading(string Id, ReadingState State, IReadOnlyList<LimitWindow> Windows,
    DateTimeOffset? UpdatedAt = null, string? Message = null, string? Plan = null, ProviderCostUsage? CostUsage = null)
{
    public LimitWindow? Headline => Windows.Count == 0 ? null : Id is "codex" or "claude"
        ? Windows.Where(x => x.UsedPercent is { } used && double.IsFinite(used)).MaxBy(x => x.UsedPercent) ?? Windows[0]
        : Windows[0];
    public bool IsStale(DateTimeOffset now)
    {
        if (UpdatedAt is not { } updated) return true;
        var age = now - updated;
        // Account stores have distinct source-clock policies on Mac. Codex
        // quota resets do not invalidate an otherwise recent server reading.
        if (Id == "codex") return age >= TimeSpan.FromMinutes(5) || age < -TimeSpan.FromMinutes(1);
        if (Id == "claude") return age > TimeSpan.FromMinutes(15) || age < -TimeSpan.FromMinutes(5)
            || Windows.Any(window => window.ResetsAt <= now);
        return age > TimeSpan.FromMinutes(5) || age < -TimeSpan.FromMinutes(1) || Windows.Any(window => window.ResetsAt <= now);
    }
    public ProviderReading Evaluated(DateTimeOffset now) => State == ReadingState.Ready && IsStale(now) ? this with { State = ReadingState.Stale } : this;
}
public sealed record ProviderDefinition(string Id, string Name, string Summary, string[] EnvironmentKeys)
{
    public bool HasLocalHistory => Id is "codex" or "claude";
    public string GuideUrl => "https://github.com/dlfkdLR/CodeRim/blob/main/docs/providers/" + Id + ".md";
}
public static class ProviderCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static IReadOnlyList<ProviderDefinition> All { get; } = ReadResource<ProviderDefinition[]>("providers.json");
    public static ProviderDefinition? Find(string id) => All.FirstOrDefault(x => x.Id == id);
    internal static T ReadResource<T>(string name)
    {
        using var stream = typeof(ProviderCatalog).Assembly.GetManifestResourceStream("CodeRim.Core.Resources." + name)
            ?? throw new InvalidOperationException("Missing bundled catalog");
        return JsonSerializer.Deserialize<T>(stream, Options)!;
    }
}
public enum NotchEdge { Right, Left, Top, Bottom }
public enum NotchVisibility { OnHover, AlwaysShow, Hidden }
public enum RingColorMode { Usage, Fixed, Gradient }
public readonly record struct ScreenArea(double X, double Y, double Width, double Height);
public static class NotchGeometry
{
    public static (double X, double Y) Place(ScreenArea area, double width, double height, NotchEdge edge, double offset)
    {
        var x = area.X + (area.Width - width) / 2 + offset;
        var y = area.Y + (area.Height - height) / 2 + offset;
        return edge switch
        {
            NotchEdge.Right => (area.X + area.Width - width, Clamp(y, area.Y, area.Y + area.Height - height)),
            NotchEdge.Left => (area.X, Clamp(y, area.Y, area.Y + area.Height - height)),
            NotchEdge.Top => (Clamp(x, area.X, area.X + area.Width - width), area.Y),
            _ => (Clamp(x, area.X, area.X + area.Width - width), area.Y + area.Height - height)
        };
    }
    private static double Clamp(double value, double min, double max) => max < min ? min : Math.Clamp(value, min, max);
    public static string BandColor(double? used, string accent = "#00FF88") => used is >= 70 ? "#FF3F00" : used is >= 50 ? "#F2FF00" : accent;
}

public sealed class ThresholdTracker
{
    private static readonly int[] Thresholds = [80, 100];
    private readonly Dictionary<string, int> levels = new(StringComparer.Ordinal);
    public IReadOnlyList<int> Observe(ProviderReading reading, DateTimeOffset now)
    {
        if (reading.State != ReadingState.Ready || reading.IsStale(now) || reading.Headline?.UsedPercent is not { } percent) return [];
        var level = percent >= 100 ? 100 : percent >= 80 ? 80 : 0;
        levels.TryGetValue(reading.Id, out var old);
        levels[reading.Id] = level;
        return Thresholds.Where(x => x > old && x <= level).ToArray();
    }
}
