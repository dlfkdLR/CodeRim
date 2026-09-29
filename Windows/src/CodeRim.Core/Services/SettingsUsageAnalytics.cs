using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

public enum SettingsUsageGrouping { TokenType, Model }
public enum SettingsUsageSection { Overview, Analytics, Limits }
public sealed record SettingsUsageSeries(string Id, string Title, long Tokens);
public sealed record SettingsUsageModel(string Id, string Title, TokenUsage Usage);
public sealed record SettingsUsageDay(DateTimeOffset Start, DateTimeOffset End, TokenUsage Usage, IReadOnlyList<SettingsUsageModel> Models);
public sealed record SettingsUsageSession(string Id, TokenUsage Usage, DateTimeOffset LastActivityAt);
public sealed record SettingsUsageSnapshot(AnalyticsRange Range, DateTimeOffset Through, TokenUsage Usage, DataQuality Quality,
    IReadOnlyList<SettingsUsageDay> Days, IReadOnlyList<SettingsUsageModel> Models, IReadOnlyList<SettingsUsageSession> Sessions);

/// <summary>Local Settings projections. Account/server totals never participate.</summary>
public static class SettingsUsageAnalytics
{
    public static SettingsUsageSnapshot Build(AnalyticsSourceFrame frame, AnalyticsRange range, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(zone);
        if (!Enum.IsDefined(range)) throw new ArgumentOutOfRangeException(nameof(range));
        if (range == AnalyticsRange.Today) range = AnalyticsRange.SevenDays;
        var start = AnalyticsTimeline.Start(range, frame.Through, zone);
        var events = frame.Events.Where(e => e.OccurredAt >= start && e.OccurredAt <= frame.Through)
            .OrderBy(e => e.OccurredAt).ToArray();
        var total = Sum(events);
        var days = new List<SettingsUsageDay>(); var index = 0;
        for (var cursor = start; cursor <= frame.Through;)
        {
            var date = TimeZoneInfo.ConvertTime(cursor, zone).Date;
            var next = date == DateTime.MaxValue.Date ? DateTimeOffset.MaxValue
                : AnalyticsTimeline.LocalMidnight(date.AddDays(1), zone);
            var end = next <= frame.Through ? next : frame.Through == DateTimeOffset.MaxValue ? frame.Through : frame.Through.AddTicks(1);
            var rows = new List<UsageEvent>();
            while (index < events.Length && (events[index].OccurredAt < end || end == DateTimeOffset.MaxValue && events[index].OccurredAt == end))
                rows.Add(events[index++]);
            days.Add(new(cursor, end, Sum(rows), Models(rows)));
            if (next <= cursor || next > frame.Through || next == DateTimeOffset.MaxValue) break;
            cursor = next;
        }
        var sessions = events.GroupBy(e => e.SessionId, StringComparer.Ordinal)
            .Select(g => new SettingsUsageSession(g.Key, Sum(g), g.Max(e => e.OccurredAt)))
            .OrderByDescending(s => s.Usage.TotalTokens).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
        var quality = total.IsZero ? DataQuality.Unavailable
            : frame.Local.RetainsPartialHistory || frame.Local.Quality == DataQuality.Partial ? DataQuality.Partial : frame.Local.Quality;
        return new(range, frame.Through, total, quality, days.AsReadOnly(), Models(events), Array.AsReadOnly(sessions));
    }

    public static IReadOnlyList<SettingsUsageSeries> Series(SettingsUsageSnapshot snapshot, SettingsUsageGrouping grouping,
        bool showsCachedInput, SettingsUsageDay? day = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(grouping)) throw new ArgumentOutOfRangeException(nameof(grouping));
        var usage = day?.Usage ?? snapshot.Usage;
        if (grouping == SettingsUsageGrouping.TokenType)
            return showsCachedInput
                ? [new("input", "Uncached input", Math.Max(0, usage.InputTokens - usage.CachedInputTokens)),
                    new("cached", "Cached input", usage.CachedInputTokens), new("output", "Output", usage.OutputTokens)]
                : [new("input", "Input", usage.InputTokens), new("output", "Output", usage.OutputTokens)];
        var leading = snapshot.Models.OrderByDescending(m => m.Usage.TotalTokens).ThenBy(m => m.Id, StringComparer.Ordinal).Take(5).ToArray();
        var models = day?.Models ?? snapshot.Models;
        var ids = leading.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var result = leading.Select(m => new SettingsUsageSeries("model:" + m.Id, m.Title,
            models.FirstOrDefault(row => row.Id == m.Id)?.Usage.TotalTokens ?? 0)).ToList();
        if (snapshot.Models.Count > leading.Length)
            result.Add(new("other-models", "Other models", models.Where(m => !ids.Contains(m.Id))
                .Aggregate(TokenUsage.Zero, (sum, m) => sum.Add(m.Usage)).TotalTokens));
        return result.AsReadOnly();
    }

    public static SettingsUsageDay? DayAt(SettingsUsageSnapshot snapshot, DateTimeOffset? date, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(zone);
        if (date is null) return null;
        var localDate = TimeZoneInfo.ConvertTime(date.Value, zone).Date;
        return snapshot.Days.FirstOrDefault(d => TimeZoneInfo.ConvertTime(d.Start, zone).Date == localDate);
    }

    private static TokenUsage Sum(IEnumerable<UsageEvent> events) => events.Aggregate(TokenUsage.Zero, (sum, e) => sum.Add(e.Usage));
    private static System.Collections.ObjectModel.ReadOnlyCollection<SettingsUsageModel> Models(IEnumerable<UsageEvent> events) => Array.AsReadOnly(events
        .GroupBy(e => e.Model, StringComparer.Ordinal).Select(g => new SettingsUsageModel(g.Key, g.Key, Sum(g)))
        .OrderByDescending(m => m.Usage.TotalTokens).ThenBy(m => m.Id, StringComparer.Ordinal).ToArray());
}
