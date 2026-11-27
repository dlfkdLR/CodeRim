#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
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
        // The compatibility path retains the original publication point and exceptions for malformed input.
        if (Array.IndexOf(next, null) >= 0 || Array.IndexOf(old, null) >= 0) return null;
        if (old.Length == 0) return new((SessionActivity[])next.Clone(), null, 0);
        return Plan(old, next);
#else
        return null;
#endif
    }

#if CODERIM_RUST_ACCOUNTING
    private static SessionActivityRefresh Plan(SessionActivity[] old, SessionActivity[] next)
    {
        // Transport exact UTF-16 identities (including null fields). The native planner computes
        // state transitions and first previous matches independently of culture and hash ordering.
        var identities = new Dictionary<(string Provider, string Id), int>();
        Dictionary<string, int>? otherClaudeStates = null;
        int StateCode(SessionActivity row)
        {
            switch (row.State)
            {
                case "busy": return 0;
                case "idle": return 1;
                case "waiting": return 2;
                case null: return 3;
            }
            if (row.Provider != "claude") return 3;
            otherClaudeStates ??= new Dictionary<string, int>(StringComparer.Ordinal);
            return 4 + Intern(otherClaudeStates, row.State);
        }
        void Encode(SessionActivity[] sessions, Span<RustActivityRefresh.Node> output)
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                var row = sessions[i];
                int id = Intern(identities, (row.Provider, row.Id));
                int flags = (row.Provider == "claude" ? 1 : 0) | (row.ParentThreadId is null ? 2 : 0);
                output[i] = new(row.Since.UtcTicks, row.ProcessStartedAt?.UtcTicks ?? -1, id, StateCode(row), flags);
            }
        }
        Span<RustActivityRefresh.Node> prior = old.Length <= 64 ? stackalloc RustActivityRefresh.Node[old.Length] : new RustActivityRefresh.Node[old.Length];
        Span<RustActivityRefresh.Node> incoming = next.Length <= 64 ? stackalloc RustActivityRefresh.Node[next.Length] : new RustActivityRefresh.Node[next.Length];
        Span<int> matches = next.Length <= 64 ? stackalloc int[next.Length] : new int[next.Length];
        Encode(old, prior);
        Encode(next, incoming);
        var decision = RustActivityRefresh.Resolve(prior, incoming, identities.Count, matches);
        var result = (SessionActivity[])next.Clone();
        for (int i = 0; i < result.Length; i++)
            if (matches[i] >= 0) result[i] = next[i] with { Since = old[matches[i]].Since };
        return new(result, decision.Attention < 0 ? null : result[decision.Attention], decision.Signals);
    }

    private static int Intern<T>(Dictionary<T, int> ordinals, T key) where T : notnull
    {
        ref int index = ref CollectionsMarshal.GetValueRefOrAddDefault(ordinals, key, out var exists);
        if (!exists) index = ordinals.Count - 1;
        return index;
    }
#endif
}
