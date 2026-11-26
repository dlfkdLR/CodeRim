using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

public static class AnalyticsEventNames
{
    private static bool HasProjectName(UsageEvent row) => !string.IsNullOrWhiteSpace(row.Project)
        && (row.Project != "Unknown project" || row.ProjectId != "unknown");

    private static bool Prefer(UsageEvent row, UsageEvent current)
    {
        bool named = HasProjectName(row), otherNamed = HasProjectName(current);
        if (named != otherNamed) return named;
        int rank = row.OccurredAt.CompareTo(current.OccurredAt);
        if (rank != 0) return rank > 0;
        rank = string.CompareOrdinal(row.EventKey, current.EventKey);
        if (rank != 0) return rank < 0;
        rank = string.CompareOrdinal(row.ProjectId, current.ProjectId);
        return rank != 0 ? rank < 0 : string.CompareOrdinal(row.Project, current.Project) < 0;
    }

    public static UsageEvent Preferred(IEnumerable<UsageEvent> events) =>
        events.Aggregate((current, row) => Prefer(row, current) ? row : current);

    public static string SessionName(UsageEvent row, string id) => HasProjectName(row)
        ? row.Project : "Session " + id[..Math.Min(8, id.Length)];

    public static IReadOnlyList<string> ForSessions(IReadOnlyList<UsageEvent> events,
        IReadOnlyList<SettingsUsageSession> sessions, int count, Func<string, string?> publishedName)
    {
        var names = new string[count];
        Dictionary<string, int>? missing = null;
        for (int i = 0; i < count; i++)
        {
            string id = sessions[i].Id;
            if (publishedName(id) is { } name) names[i] = name;
            else (missing ??= new(StringComparer.Ordinal)).Add(id, i);
        }
        if (missing is null) return names;
        var best = new UsageEvent?[count];
        foreach (var row in events)
            if (row.SessionId is { } id && missing.TryGetValue(id, out int index)
                && (best[index] is not { } prior || Prefer(row, prior)))
                best[index] = row;
        foreach (var (id, index) in missing)
        {
            // Every ranked session has an event in its source frame. Use the
            // same empty-sequence failure as a direct detail query if it is missing.
            names[index] = SessionName(best[index] ?? Enumerable.Empty<UsageEvent>().First(), id);
        }
        return names;
    }
}
