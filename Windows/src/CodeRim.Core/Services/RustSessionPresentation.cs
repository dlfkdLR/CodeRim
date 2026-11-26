#if CODERIM_RUST_ACCOUNTING
using System.Numerics;
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

internal static class RustSessionPresentation
{
    // A bit identifies each distinct local display row. The unrestricted managed traversal
    // handles larger sets. Intern ordinals in .NET to preserve all UTF-16 string identities.
    internal static IReadOnlyDictionary<string, long>? TryTotals(IReadOnlyList<SessionActivity> sessions, string provider,
        IReadOnlyList<UsageEvent> events, IReadOnlyList<SessionDetails> metadata, CancellationToken token)
    {
        var nodes = new Dictionary<string, int>(StringComparer.Ordinal);
        var edges = new List<RustSessionTotals.Edge>();
        foreach (var detail in metadata)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(detail.ParentId)) continue;
            if (detail.Id is null) return null;
            edges.Add(new(Intern(nodes, detail.ParentId), Intern(nodes, detail.Id)));
        }
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        var roots = new List<RustSessionTotals.Root>();
        foreach (var session in sessions)
        {
            if (session.Provider != provider || session.RemoteHostId is not null || string.IsNullOrEmpty(session.UsageSessionId)) continue;
            var owner = owners.Count;
            // Repeated display IDs can be first reached in different orders for different descendants.
            if (session.Id is null || !owners.TryAdd(session.Id, owner)) return null;
            roots.Add(new(Intern(nodes, session.UsageSessionId), owner));
        }
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (roots.Count == 0) return result;
        var masks = new ulong[nodes.Count];
        RustSessionTotals.Attribute(CollectionsMarshal.AsSpan(roots), CollectionsMarshal.AsSpan(edges), masks, token);
        if (!masks.AsSpan().ContainsAnyExcept(0UL)) return result;

        var ownerNames = new string[owners.Count];
        foreach (var (name, index) in owners) ownerNames[index] = name;
        Span<int> order = stackalloc int[owners.Count];
        var orderCount = 0;
        ulong encountered = 0;
        var eventKeys = new HashSet<string>(StringComparer.Ordinal);
        Span<long> totals = stackalloc long[owners.Count];
        totals.Fill(-1);
        var capacity = events switch
        {
            UsageEvent[] array => Math.Min(array.Length, RustSessionTotals.BatchSize),
            List<UsageEvent> list => Math.Min(list.Count, RustSessionTotals.BatchSize),
            _ => RustSessionTotals.BatchSize
        };
        Span<RustSessionTotals.Event> batch = stackalloc RustSessionTotals.Event[capacity];
        var count = 0;
        foreach (var e in events)
        {
            token.ThrowIfCancellationRequested();
            if (e.Provider != provider || !nodes.TryGetValue(e.SessionId, out var node)) continue;
            var mask = masks[node];
            if (mask == 0 || !eventKeys.Add(e.EventKey)) continue;
            // Dictionary enumeration follows the first accepted event and root order at shared nodes.
            for (var added = mask & ~encountered; added != 0; added &= added - 1)
                order[orderCount++] = BitOperations.TrailingZeroCount(added);
            encountered |= mask;
            batch[count++] = new(mask, RustAccountingLibrary.RawUsage.From(e.Usage));
            if (count == batch.Length) { RustSessionTotals.Aggregate(batch, totals); count = 0; }
        }
        if (count > 0) RustSessionTotals.Aggregate(batch[..count], totals);
        foreach (var index in order[..orderCount])
            if (totals[index] >= 0) result.Add(ownerNames[index], totals[index]);
        return result;
    }

    private static int Intern(Dictionary<string, int> ordinals, string id)
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(ordinals, id, out var exists);
        if (!exists) slot = ordinals.Count - 1;
        return slot;
    }
}
#endif
