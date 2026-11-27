#if CODERIM_RUST_ACCOUNTING
namespace CodeRim.Core.Services;

public static partial class SessionPresentation
{
    private static List<ActivityDisplayGroup> DisplayGroups(Dictionary<string, SessionActivity> nodes,
        Dictionary<string, string> parents, HashSet<string> context)
    {
        var groups = new List<ActivityDisplayGroup>();
        if (nodes.Count == 0) return groups;
        if (nodes.Count == 1)
        {
            var only = nodes.Values.First();
            groups.Add(new(new(only, 0, context.Contains(only.Id)), []));
            return groups;
        }

        // The index is also the .NET UTF-16 ordinal tie-breaker, including unpaired surrogates.
        var ids = nodes.Keys.ToArray();
        Array.Sort(ids, StringComparer.Ordinal);
        var indexes = new Dictionary<string, int>(parents.Count == 0 ? 0 : ids.Length, StringComparer.Ordinal);
        if (parents.Count > 0)
            for (int i = 0; i < ids.Length; i++) indexes.Add(ids[i], i);
        var input = new RustActivityGroups.Node[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            var node = nodes[ids[i]];
            int parent = parents.TryGetValue(node.Id, out var parentId) ? indexes[parentId] : -1;
            int rank = node.State switch { "waiting" => 0, "busy" => 1, "idle" => 2, _ => 3 };
            input[i] = new(node.Since.UtcTicks, parent, rank);
        }

        var plan = new RustActivityGroups.Row[ids.Length];
        RustActivityGroups.Plan(input, plan);
        ActivityDisplayRow Row(RustActivityGroups.Row row)
        {
            var id = ids[row.Index];
            return new(nodes[id], row.Depth, context.Contains(id));
        }
        for (int root = 0; root < plan.Length;)
        {
            int end = root + 1;
            while (end < plan.Length && plan[end].Depth != 0) end++;
            var children = end == root + 1 ? Array.Empty<ActivityDisplayRow>() : new ActivityDisplayRow[end - root - 1];
            for (int i = root + 1; i < end; i++) children[i - root - 1] = Row(plan[i]);
            groups.Add(new(Row(plan[root]), children));
            root = end;
        }
        return groups;
    }
}
#endif
