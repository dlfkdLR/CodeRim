using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

public static partial class SessionPresentation
{
    public static string? Duration(SessionActivity session, bool enabled, DateTimeOffset now)
    {
        if (!enabled || session.State is not ("busy" or "waiting") || session.Since > now) return null;
        var seconds = (now - session.Since).TotalSeconds;
        if (seconds < 45) return "<1 min";
        var minutes = (long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (minutes < 60) return Math.Max(1, minutes) + " min";
        var hours = minutes / 60; var rest = minutes % 60;
        if (hours >= 24) return hours / 24 + " d " + hours % 24 + " hr " + rest + " min";
        return hours + " hr" + (rest == 0 ? "" : " " + rest + " min");
    }

    /// <summary>Only attributed local sessions receive totals; recursively include deduplicated child usage.</summary>
    public static IReadOnlyDictionary<string, long> TokenTotals(IReadOnlyList<SessionActivity> sessions, string provider,
        IReadOnlyList<UsageEvent> events, IReadOnlyList<SessionDetails> metadata, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parents = new HashSet<string>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var item in metadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(item.ParentId)) continue;
            parents.Add(item.Id);
            if (!children.TryGetValue(item.ParentId, out var list)) children[item.ParentId] = list = [];
            list.Add(item.Id);
        }
        var roots = sessions.Where(x => x.Provider == provider && x.RemoteHostId is null && x.UsageSessionId is { Length: > 0 }
            && !parents.Contains(x.UsageSessionId)).ToArray();
        if (roots.Length == 0) return new Dictionary<string, long>();
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<string>(); pending.Push(root.UsageSessionId!);
            while (pending.TryPop(out var id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(id)) continue;
                if (!owners.TryGetValue(id, out var values)) owners[id] = values = new(StringComparer.Ordinal);
                values.Add(root.Id);
                if (children.TryGetValue(id, out var descendants)) foreach (var child in descendants) pending.Push(child);
            }
        }
        var totals = new Dictionary<string, long>(StringComparer.Ordinal); var invalid = new HashSet<string>(StringComparer.Ordinal);
        var eventKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Provider != provider || !owners.TryGetValue(item.SessionId, out var ownerIds) || !eventKeys.Add(item.EventKey)) continue;
            foreach (var owner in ownerIds)
            {
                if (!item.Usage.IsValid) { invalid.Add(owner); continue; }
                try { totals[owner] = checked(totals.GetValueOrDefault(owner) + checked(item.Usage.InputTokens + item.Usage.OutputTokens)); }
                catch (OverflowException) { invalid.Add(owner); }
            }
        }
        foreach (var id in invalid) totals.Remove(id);
        return totals;
    }
}
