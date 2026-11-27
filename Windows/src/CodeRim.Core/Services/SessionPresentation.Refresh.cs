#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;
#endif

namespace CodeRim.Core.Services;

public sealed class SessionActivityRefresh
{
    internal SessionActivityRefresh(SessionActivity[] sessions, SessionActivity? attention, int signals)
    {
        Sessions = sessions;
        Attention = attention;
        this.signals = signals;
    }

    // ABI bits: finished, blocked, finished without a parent, blocked without a parent.
    private readonly int signals;
    public IReadOnlyList<SessionActivity> Sessions { get; }
    public SessionActivity? Attention { get; }
    public bool Finished => (signals & 1) != 0;
    public bool Blocked => (signals & 2) != 0;
    public bool ShouldChimeFinished(bool includeSubagents) => includeSubagents ? Finished : (signals & 4) != 0;
    public bool ShouldChimeBlocked(bool includeSubagents) => includeSubagents ? Blocked : (signals & 8) != 0;
}

public static partial class SessionPresentation
{
    /// <summary>Plan a normal refresh of the WPF activity timer's two captured arrays.</summary>
    public static SessionActivityRefresh? TryPlanActivityRefresh(IReadOnlyList<SessionActivity> previous, IReadOnlyList<SessionActivity> current)
    {
#if CODERIM_RUST_ACCOUNTING
        if (previous is not SessionActivity[] old || current is not SessionActivity[] next) return null;
        if (next.Length == 0) return new([], null, 0);
        if (old.Length == 1 && next.Length == 1 && old[0] is { } prior && next[0] is { } incoming)
            return RustAccountingLoadState.IsLoaded ? PlanSingle(prior, incoming) : PlanColdSingle(prior, incoming);
        // A profile may have no accounting work yet. Loading the entire native library solely
        // for a small refresh costs more than the released scan; a million possible pairs pays for it.
        bool cold = old.Length != 0 && !RustAccountingLoadState.IsLoaded && (long)old.Length * next.Length < 1024L * 1024;
        if (cold && (old.Length > 64 || next.Length > 64)) return null;
        // The compatibility path retains the original publication point and exceptions for malformed input.
        if (Array.IndexOf(next, null) >= 0 || Array.IndexOf(old, null) >= 0) return null;
        if (old.Length == 0) return new((SessionActivity[])next.Clone(), null, 0);
        if (cold) return PlanColdSmall(old, next);
        return old.Length <= 64 && next.Length <= 64 ? PlanSmall(old, next) : Plan(old, next);
#else
        return null;
#endif
    }

#if CODERIM_RUST_ACCOUNTING
    private static bool SameIdentity(SessionActivity left, SessionActivity right) =>
        left.Id == right.Id && left.Provider == right.Provider;

    private static int KnownState(string? state) => state switch
    {
        "busy" => 0, "idle" => 1, "waiting" => 2, null => 3, _ => -1
    };

    private static RustActivityRefresh.Node Encode(SessionActivity row, int id, int state) => new(
        row.Since.UtcTicks, row.ProcessStartedAt?.UtcTicks ?? -1, id, state,
        (row.Provider == "claude" ? 1 : 0) | (row.ParentThreadId is null ? 2 : 0));

    private static SessionActivityRefresh PlanColdSingle(SessionActivity prior, SessionActivity incoming)
    {
        // A profile with no previous Rust work need not load the library solely for a single pair.
        bool same = SameIdentity(prior, incoming);
        var published = same && incoming.Provider == "claude" && prior.State == incoming.State
            && prior.ProcessStartedAt == incoming.ProcessStartedAt ? incoming with { Since = prior.Since } : incoming;
        int transition = same && prior.State == "busy" ? incoming.State switch { "idle" => 1, "waiting" => 2, _ => 0 } : 0;
        int signals = incoming.ParentThreadId is null ? transition | (transition << 2) : transition;
        return new([published], transition != 0 ? published : null, signals);
    }

    private static SessionActivityRefresh PlanColdSmall(SessionActivity[] old, SessionActivity[] next)
    {
        var result = (SessionActivity[])next.Clone();
        SessionActivity? attention = null;
        int signals = 0;
        for (int i = 0; i < next.Length; i++)
        {
            var item = next[i];
            bool copySince = item.Provider == "claude";
            int signal = item.State switch { "idle" => 1, "waiting" => 2, _ => 0 };
            if (!copySince && signal == 0) continue;
            bool busy = false;
            foreach (var prior in old)
            {
                if (!SameIdentity(item, prior)) continue;
                if (copySince && prior.State == item.State && prior.ProcessStartedAt == item.ProcessStartedAt)
                {
                    result[i] = item with { Since = prior.Since };
                    copySince = false;
                }
                busy |= prior.State == "busy";
                if (!copySince && (signal == 0 || busy)) break;
            }
            if (!busy || signal == 0) continue;
            signals |= item.ParentThreadId is null ? signal | (signal << 2) : signal;
            if (attention is null || result[i].Since > attention.Since) attention = result[i];
        }
        return new(result, attention, signals);
    }

    private static SessionActivityRefresh PlanSingle(SessionActivity prior, SessionActivity incoming)
    {
        int beforeState = KnownState(prior.State);
        int afterState = KnownState(incoming.State);
        var before = Encode(prior, 0, beforeState < 0 ? 4 : beforeState);
        var after = Encode(incoming, SameIdentity(prior, incoming) ? 0 : 1,
            afterState >= 0 ? afterState : prior.State == incoming.State ? 4 : 5);
        var decision = RustActivityRefresh.ResolveOne(before, after, out int match);
        var published = match >= 0 ? incoming with { Since = prior.Since } : incoming;
        return new([published], decision.Attention >= 0 ? published : null, decision.Signals);
    }

    private static SessionActivityRefresh PlanSmall(SessionActivity[] old, SessionActivity[] next)
    {
        // At most 64 rows from each capture. Their source positions provide exact ordinals
        // without constructing a hash table. Only previous-row identities can match a new row.
        Span<RustActivityRefresh.Node> prior = stackalloc RustActivityRefresh.Node[old.Length];
        Span<RustActivityRefresh.Node> incoming = stackalloc RustActivityRefresh.Node[next.Length];
        Span<int> matches = stackalloc int[next.Length];
        Span<int> index = old.Length > 32 || next.Length > 32 ? stackalloc int[128] : default;
        index.Clear();
        EncodeSmall(old, next, prior, incoming, index);
        var decision = RustActivityRefresh.Resolve(prior, incoming, old.Length + 1, matches);
        return Restore(old, next, matches, decision);
    }

    private static void EncodeSmall(SessionActivity[] old, SessionActivity[] next,
        Span<RustActivityRefresh.Node> prior, Span<RustActivityRefresh.Node> incoming, Span<int> index)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            var source = pass == 0 ? old : next;
            var encoded = pass == 0 ? prior : incoming;
            for (int i = 0; i < source.Length; i++)
            {
                var row = source[i];
                // A non-Claude row outside a terminal state is published untouched. Its ID
                // cannot affect any decision because new rows are never used as predecessors.
                if (pass == 1 && row.Provider != "claude" && row.State is not ("idle" or "waiting"))
                {
                    encoded[i] = Encode(row, 0, 0);
                    continue;
                }
                int count = pass == 0 ? i : old.Length;
                int id = count;
                if (pass == 1 && i < old.Length && SameIdentity(row, old[i])) id = prior[i].Id;
                else if (!index.IsEmpty)
                {
                    // Up to 64 previous rows occupy a 128-slot table. Compare both original
                    // UTF-16 fields on collisions and retain the first previous row for duplicates.
                    int slot = (row.Id?.GetHashCode(StringComparison.Ordinal) ?? 0) & 127;
                    while (index[slot] != 0 && !SameIdentity(row, old[index[slot] - 1]))
                        slot = (slot + 1) & 127;
                    if (index[slot] != 0) id = index[slot] - 1;
                    else if (pass == 0) index[slot] = i + 1;
                }
                else
                    for (int j = 0; j < count; j++)
                        if (SameIdentity(row, old[j])) { id = prior[j].Id; break; }
                int state = KnownState(row.State);
                if (state < 0)
                {
                    state = 4 + count;
                    for (int j = 0; j < count; j++)
                        if (row.State == old[j].State) { state = prior[j].State; break; }
                }
                encoded[i] = Encode(row, id, state);
            }
        }
    }

    private static SessionActivityRefresh Restore(SessionActivity[] old, SessionActivity[] next, ReadOnlySpan<int> matches, RustActivityRefresh.Decision decision)
    {
        var result = (SessionActivity[])next.Clone();
        for (int i = 0; i < result.Length; i++)
            if (matches[i] >= 0) result[i] = next[i] with { Since = old[matches[i]].Since };
        return new(result, decision.Attention < 0 ? null : result[decision.Attention], decision.Signals);
    }

    // Timer refreshes run on the WPF dispatcher. Drop every string before returning a workspace,
    // cap the per-thread retained capacity, and detach it from the cache during use.
    [ThreadStatic] private static RefreshWorkspace? availableRefreshWorkspace;

    private static SessionActivityRefresh Plan(SessionActivity[] old, SessionActivity[] next)
    {
        const int retainLimit = 4096;
        bool retain = old.Length <= retainLimit && next.Length <= retainLimit;
        var work = retain ? availableRefreshWorkspace ?? new RefreshWorkspace() : new RefreshWorkspace();
        if (retain) availableRefreshWorkspace = null;
        try { return work.Apply(old, next); }
        finally
        {
            if (retain)
            {
                work.ClearKeys();
                availableRefreshWorkspace = work;
            }
        }
    }

    private sealed class RefreshWorkspace
    {
        private readonly Dictionary<(string Provider, string Id), int> identities = [];
        private readonly Dictionary<string, int> otherClaudeStates = new(StringComparer.Ordinal);
        private RustActivityRefresh.Node[] prior = [];
        private RustActivityRefresh.Node[] incoming = [];
        private int[] matches = [];

        internal void ClearKeys()
        {
            identities.Clear();
            otherClaudeStates.Clear();
        }

        internal SessionActivityRefresh Apply(SessionActivity[] old, SessionActivity[] next)
        {
            if (prior.Length < old.Length) prior = new RustActivityRefresh.Node[old.Length];
            if (incoming.Length < next.Length)
            {
                incoming = new RustActivityRefresh.Node[next.Length];
                matches = new int[next.Length];
            }
            for (int i = 0; i < old.Length; i++)
                prior[i] = EncodeIndexed(old[i], Intern(identities, (old[i].Provider, old[i].Id)));
            for (int i = 0; i < next.Length; i++)
            {
                var row = next[i];
                if (row.Provider != "claude" && row.State is not ("idle" or "waiting"))
                {
                    incoming[i] = Encode(row, 0, 0);
                    continue;
                }
                // Repeated timer reads normally preserve row order even when the objects are new.
                int id = i < old.Length && SameIdentity(row, old[i])
                    ? prior[i].Id : Intern(identities, (row.Provider, row.Id));
                incoming[i] = EncodeIndexed(row, id);
            }
            var decision = RustActivityRefresh.Resolve(prior.AsSpan(0, old.Length), incoming.AsSpan(0, next.Length), identities.Count, matches.AsSpan(0, next.Length));
            return Restore(old, next, matches, decision);
        }

        private RustActivityRefresh.Node EncodeIndexed(SessionActivity row, int id)
        {
            int state = KnownState(row.State);
            if (state < 0) state = row.Provider == "claude" ? 4 + Intern(otherClaudeStates, row.State) : 3;
            return Encode(row, id, state);
        }
    }

    private static int Intern<T>(Dictionary<T, int> ordinals, T key) where T : notnull
    {
        ref int index = ref CollectionsMarshal.GetValueRefOrAddDefault(ordinals, key, out var exists);
        if (!exists) index = ordinals.Count - 1;
        return index;
    }
#endif
}
