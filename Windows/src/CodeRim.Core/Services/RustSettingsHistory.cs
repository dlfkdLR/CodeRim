#if CODERIM_RUST_ACCOUNTING
using System.Buffers;
using System.Collections.ObjectModel;
using CodeRim.Core.Domain;
using static CodeRim.Core.Services.RustSettingsHistoryTable;

namespace CodeRim.Core.Services;

internal static class RustSettingsHistory
{
    internal static SettingsUsageSnapshot? TryBuild(AnalyticsSourceFrame frame, AnalyticsRange range,
        DateTimeOffset start, TimeZoneInfo zone, UsageEvent[] input)
    {
        // For small ranges the original projection avoids binding and marshalling a native table.
        if (input.Length is < 64 or > UsageScanner.MaximumEventCount) return null;
        var modelIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var sessionIds = new Dictionary<string, int>(StringComparer.Ordinal);
        // Keep ordinary refresh buffers reusable without retaining a one-off, multi-megabyte archive.
        bool pooled = input.Length <= 65_536;
        var rows = pooled ? ArrayPool<Event>.Shared.Rent(input.Length) : new Event[input.Length];
        try
        {
            bool repaired = false;
            for (int i = 0; i < input.Length; i++)
            {
                var e = input[i];
                var u = e.Usage;
                // Unsigned counts add independently of ordering. Signed, corrupt inputs may depend
                // on the existing stable chronological fold when saturation occurs; retain that path.
                if (e.Model is null || e.SessionId is null || u.InputTokens < 0 || u.CachedInputTokens < 0 ||
                    u.OutputTokens < 0 || u.CacheWriteInputTokens < 0) return null;
                rows[i] = new(e.OccurredAt.UtcTicks, u.InputTokens, u.CachedInputTokens, u.OutputTokens,
                    u.CacheWriteInputTokens ?? -1, Index(modelIds, e.Model), Index(sessionIds, e.SessionId));
                repaired |= e.Repaired;
            }
            var bounds = SettingsUsageAnalytics.DayBounds(start, frame.Through, zone).ToArray();
            if (bounds.Length is 0 or > 32 || (long)bounds.Length * modelIds.Count > 65_536) return null;
            var ends = bounds.Select(d => d.End == DateTimeOffset.MaxValue ? d.End.UtcTicks : d.End.UtcTicks - 1).ToArray();
            int modelsAt = 1 + bounds.Length, sessionsAt = modelsAt + modelIds.Count, cellsAt = sessionsAt + sessionIds.Count;
            var groups = new Group[cellsAt + bounds.Length * modelIds.Count];
            if (!TryProject(rows.AsSpan(0, input.Length), ends, modelIds.Count, sessionIds.Count, groups)) return null;

            var names = new string[modelIds.Count];
            foreach (var (name, index) in modelIds) names[index] = name;
            var days = new SettingsUsageDay[bounds.Length];
            for (int day = 0; day < days.Length; day++)
                days[day] = new(bounds[day].Start, bounds[day].End, groups[1 + day].Usage,
                    Models(names, groups, cellsAt + day * names.Length, onlyPresent: true));
            var sessions = new SettingsUsageSession[sessionIds.Count];
            foreach (var (name, index) in sessionIds)
            {
                var group = groups[sessionsAt + index];
                sessions[index] = new(name, group.Usage, input[(int)group.LatestPlusOne - 1].OccurredAt);
            }
            Array.Sort(sessions, (a, b) => Ranked(a.Usage, a.Id, b.Usage, b.Id));
            var total = groups[0].Usage;
            var local = frame.Local;
            var quality = local is null || total.IsZero ? DataQuality.Unavailable :
                LocalHistoryQuality.ForNonemptyRange(local, local.SourcesPartial || local.RetainsPartialHistory || repaired);
            return new(range, frame.Through, total, quality, Array.AsReadOnly(days),
                Models(names, groups, modelsAt, onlyPresent: false), Array.AsReadOnly(sessions));
        }
        finally { if (pooled) ArrayPool<Event>.Shared.Return(rows); }
    }

    private static int Index(Dictionary<string, int> ids, string label)
    {
        if (ids.TryGetValue(label, out int id)) return id;
        id = ids.Count;
        ids.Add(label, id);
        return id;
    }

    private static ReadOnlyCollection<SettingsUsageModel> Models(string[] names, Group[] groups, int start, bool onlyPresent)
    {
        var models = new List<SettingsUsageModel>();
        for (int i = 0; i < names.Length; i++)
        {
            var group = groups[start + i];
            if (!onlyPresent || group.LatestPlusOne != 0) models.Add(new(names[i], names[i], group.Usage));
        }
        models.Sort((a,b) => Ranked(a.Usage, a.Id, b.Usage, b.Id));
        return models.AsReadOnly();
    }

    private static int Ranked(TokenUsage a, string aId, TokenUsage b, string bId)
    {
        int rank = b.TotalTokens.CompareTo(a.TotalTokens);
        return rank == 0 ? StringComparer.Ordinal.Compare(aId, bId) : rank;
    }
}
#endif
