using CodeRim.Core.Parsing;

namespace CodeRim.Core.Services;

public sealed record ActivityDisplayRow(SessionActivity Session, int Depth, bool ContextOnly);
public sealed record ActivityDisplayGroup(ActivityDisplayRow Parent, IReadOnlyList<ActivityDisplayRow> Children);

public static partial class SessionPresentation
{
    /// <summary>Display-only parents never enter completion detection or live activity counts.</summary>
    public static IReadOnlyList<ActivityDisplayGroup> Groups(IReadOnlyList<SessionActivity> sessions)
    {
        var nodes = new Dictionary<string, SessionActivity>(StringComparer.Ordinal);
        foreach (var session in sessions) nodes.TryAdd(session.Id, session);
        var threads = new Dictionary<(string Provider, string? Host, string Thread), string>();
        static string ThreadKey(string id) => Guid.TryParseExact(id, "D", out var value) ? value.ToString("D") : id;
        foreach (var session in nodes.Values)
            if (session.CodexThreadId is { } thread) threads.TryAdd((session.Provider, session.RemoteHostId, ThreadKey(thread)), session.Id);
        var context = new HashSet<string>(StringComparer.Ordinal);
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (session.Provider != "codex" || session.ParentThreadId is not { } parent || !Guid.TryParseExact(parent, "D", out var parentId)
                || ThreadKey(parent) == ThreadKey(session.CodexThreadId ?? "")) continue;
            var usageIdentity = ClaudeJsonlParser.Hash(parent);
            parent = parentId.ToString("D");
            var thread = (session.Provider, session.RemoteHostId, parent);
            if (!threads.TryGetValue(thread, out var key))
            {
                key = "context:" + session.Provider + ":" + session.RemoteHostId + ":" + parent;
                while (nodes.ContainsKey(key)) key = "context:" + key;
                nodes[key] = new(key, session.Provider, session.Name, "idle", DateTimeOffset.MinValue) {
                    CodexThreadId = parent, RemoteHostId = session.RemoteHostId,
                    Detail = session.ParentThreadTitle ?? "Task " + parent[..8],
                    UsageSessionId = session.RemoteHostId is null ? usageIdentity : null };
                threads[thread] = key; context.Add(key);
            }
            parents[session.Id] = key;
        }
        // Remove one edge of each corrupt cycle deterministically, keeping every task visible.
        foreach (var key in parents.Keys.Order(StringComparer.Ordinal).ToArray())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { key }; var cursor = key;
            while (parents.TryGetValue(cursor, out var next))
            {
                if (!seen.Add(next)) { parents.Remove(key); break; }
                cursor = next;
            }
        }
        var children = parents.Keys.GroupBy(x => parents[x], StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        static int Rank(string state) => state switch { "waiting" => 0, "busy" => 1, "idle" => 2, _ => 3 };
        var priorities = new Dictionary<string, (int Rank, DateTimeOffset Since)>(StringComparer.Ordinal);
        foreach (var root in nodes.Keys.Where(x => !parents.ContainsKey(x)))
        {
            var stack = new Stack<(string Id, bool Visited)>(); stack.Push((root, false));
            while (stack.TryPop(out var item))
            {
                var descendants = children.GetValueOrDefault(item.Id) ?? [];
                if (!item.Visited)
                {
                    stack.Push((item.Id, true));
                    foreach (var child in descendants) stack.Push((child, false));
                    continue;
                }
                var node = nodes[item.Id]; var rank = Rank(node.State); var since = node.Since;
                foreach (var child in descendants)
                {
                    var priority = priorities[child]; rank = Math.Min(rank, priority.Rank);
                    if (priority.Since > since) since = priority.Since;
                }
                priorities[item.Id] = (rank, since);
            }
        }
        IEnumerable<string> Ordered(IEnumerable<string> keys) => keys.OrderBy(x => priorities[x].Rank)
            .ThenByDescending(x => priorities[x].Since).ThenBy(x => x, StringComparer.Ordinal);
        var groups = new List<ActivityDisplayGroup>();
        foreach (var root in Ordered(nodes.Keys.Where(x => !parents.ContainsKey(x))))
        {
            var rows = new List<ActivityDisplayRow>(); var stack = new Stack<(string Id, int Depth)>();
            stack.Push((root, 0));
            while (stack.TryPop(out var item))
            {
                rows.Add(new(nodes[item.Id], item.Depth, context.Contains(item.Id)));
                foreach (var child in Ordered(children.GetValueOrDefault(item.Id) ?? []).Reverse()) stack.Push((child, item.Depth + 1));
            }
            groups.Add(new(rows[0], rows.Skip(1).ToArray()));
        }
        return groups;
    }
}
