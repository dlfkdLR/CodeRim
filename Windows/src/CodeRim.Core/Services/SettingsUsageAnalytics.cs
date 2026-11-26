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

/// <summary>How complete the local history shown for one range is.</summary>
public static class LocalHistoryQuality
{
    /// Partial when the read was incomplete, when a retained reading was, or when a record in the range was
    /// marked incomplete; not because of a marked record in some other period the current snapshot covers.
    public static DataQuality ForRange(UsageSnapshot? local, IReadOnlyCollection<UsageEvent> rangeEvents)
    {
        ArgumentNullException.ThrowIfNull(rangeEvents);
        if (local is null || rangeEvents.Aggregate(TokenUsage.Zero, (sum, e) => sum.Add(e.Usage)).IsZero) return DataQuality.Unavailable;
        return ForNonemptyRange(local, local.SourcesPartial || local.RetainsPartialHistory || rangeEvents.Any(e => e.Repaired));
    }

    internal static DataQuality ForNonemptyRange(UsageSnapshot local, bool partial) =>
        partial ? DataQuality.Partial : local.Quality == DataQuality.Partial ? DataQuality.Exact : local.Quality;
}

/// <summary>Local Settings projections. Account/server totals never participate.</summary>
public static class SettingsUsageAnalytics
{
    public static SettingsUsageSnapshot Build(AnalyticsSourceFrame frame, AnalyticsRange range, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(zone);
        if (!Enum.IsDefined(range)) throw new ArgumentOutOfRangeException(nameof(range));
        if (range == AnalyticsRange.Today) range = AnalyticsRange.SevenDays;
        var start = AnalyticsTimeline.Start(range, frame.Through, zone);
        var selected = frame.Events.Where(e => e.OccurredAt >= start && e.OccurredAt <= frame.Through);
#if CODERIM_RUST_ACCOUNTING
        var input = selected.ToArray();
        if (RustSettingsHistory.TryBuild(frame, range, start, zone, input) is { } projection) return projection;
        selected = input;
#endif
        var events = selected.OrderBy(e => e.OccurredAt).ToArray();
        var total = Sum(events);
        var days = new List<SettingsUsageDay>(); var index = 0;
        foreach (var (cursor, end) in DayBounds(start, frame.Through, zone))
        {
            var rows = new List<UsageEvent>();
            while (index < events.Length && (events[index].OccurredAt < end || end == DateTimeOffset.MaxValue && events[index].OccurredAt == end))
                rows.Add(events[index++]);
            days.Add(new(cursor, end, Sum(rows), Models(rows)));
        }
        var sessions = events.GroupBy(e => e.SessionId, StringComparer.Ordinal)
            .Select(g => new SettingsUsageSession(g.Key, Sum(g), g.Max(e => e.OccurredAt)))
            .OrderByDescending(s => s.Usage.TotalTokens).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
        var quality = LocalHistoryQuality.ForRange(frame.Local, events);
        return new(range, frame.Through, total, quality, days.AsReadOnly(), Models(events), Array.AsReadOnly(sessions));
    }

    internal static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> DayBounds(DateTimeOffset start, DateTimeOffset through, TimeZoneInfo zone)
    {
        for (var cursor = start; cursor <= through;)
        {
            var date = TimeZoneInfo.ConvertTime(cursor, zone).Date;
            var next = date == DateTime.MaxValue.Date ? DateTimeOffset.MaxValue
                : UsageCalendar.LocalMidnight(date.AddDays(1), zone);
            var end = next <= through ? next : through == DateTimeOffset.MaxValue ? through : through.AddTicks(1);
            yield return (cursor, end);
            if (next <= cursor || next > through || next == DateTimeOffset.MaxValue) break;
            cursor = next;
        }
    }

    public static Func<SettingsUsageDay?, IReadOnlyList<SettingsUsageSeries>> CreateSeriesProjection(
        SettingsUsageSnapshot snapshot, SettingsUsageGrouping grouping, bool showsCachedInput)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(grouping)) throw new ArgumentOutOfRangeException(nameof(grouping));
#if CODERIM_RUST_ACCOUNTING
        return grouping == SettingsUsageGrouping.TokenType
            ? day => TokenSeries(day?.Usage ?? snapshot.Usage, showsCachedInput)
            : new RustSettingsSeries(snapshot).Project;
#else
        return day => Series(snapshot, grouping, showsCachedInput, day);
#endif
    }

    public static IReadOnlyList<SettingsUsageSeries> Series(SettingsUsageSnapshot snapshot, SettingsUsageGrouping grouping,
        bool showsCachedInput, SettingsUsageDay? day = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(grouping)) throw new ArgumentOutOfRangeException(nameof(grouping));
        if (grouping == SettingsUsageGrouping.TokenType)
            return TokenSeries(day?.Usage ?? snapshot.Usage, showsCachedInput);
#if CODERIM_RUST_ACCOUNTING
        return new RustSettingsSeries(snapshot).Project(day);
#else
        var leading = snapshot.Models.OrderByDescending(m => m.Usage.TotalTokens).ThenBy(m => m.Id, StringComparer.Ordinal).Take(5).ToArray();
        var models = day?.Models ?? snapshot.Models;
        var ids = leading.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var result = leading.Select(m => new SettingsUsageSeries("model:" + m.Id, m.Title,
            models.FirstOrDefault(row => row.Id == m.Id)?.Usage.TotalTokens ?? 0)).ToList();
        if (snapshot.Models.Count > leading.Length)
            result.Add(new("other-models", "Other models", models.Where(m => !ids.Contains(m.Id))
                .Aggregate(TokenUsage.Zero, (sum, m) => sum.Add(m.Usage)).TotalTokens));
        return result.AsReadOnly();
#endif
    }

    private static IReadOnlyList<SettingsUsageSeries> TokenSeries(TokenUsage usage, bool showsCachedInput) =>
        showsCachedInput
            ? [new("input", "Uncached input", Math.Max(0, usage.InputTokens - usage.CachedInputTokens)),
                new("cached", "Cached input", usage.CachedInputTokens), new("output", "Output", usage.OutputTokens)]
            : [new("input", "Input", usage.InputTokens), new("output", "Output", usage.OutputTokens)];

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
