#if CODERIM_RUST_ACCOUNTING
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

internal static unsafe class RustClaudeScanSessions
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Row(long UtcTicks, uint Session, uint Flags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Group(uint First, uint Parent, uint Earliest, uint Project);

    // Keep the cache bounded after a large import. Larger scratch buffers live
    // only for the current reduction and are collected with the scan.
    private static readonly ArrayPool<Row> Rows = ArrayPool<Row>.Create(4096, 2);
    private static readonly ArrayPool<Group> Groups = ArrayPool<Group>.Create(4096, 2);

    static RustClaudeScanSessions() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_windows_claude_scan_sessions",
        CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint ProjectSessions(Row* rows, uint count, uint groupCount, Group* groups,
        delegate* unmanaged[Cdecl]<nint, uint, uint, int> compare, nint context);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CompareProject(nint context, uint left, uint right)
    {
        var events = (List<UsageEvent>)GCHandle.FromIntPtr(context).Target!;
        return string.CompareOrdinal(events[(int)left].Project, events[(int)right].Project);
    }

    internal static SessionDetails[] Project(List<UsageEvent> events)
    {
        if (events.Count == 0) return [];
        var ids = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint nextId = 0;
        uint? nullId = null;
        var rows = Rows.Rent(events.Count);
        Group[]? groups = null;
        var context = default(GCHandle);
        try
        {
            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i];
                uint id;
                if (e.SessionId is null) id = nullId ??= nextId++;
                else
                {
                    ref var interned = ref CollectionsMarshal.GetValueRefOrAddDefault(ids, e.SessionId, out var exists);
                    if (!exists) interned = nextId++;
                    id = interned;
                }
                var flags = (e.ProjectId != "unknown" ? 1u : 0u) | (e.ImportParentSessionId is not null ? 2u : 0u);
                rows[i] = new(e.OccurredAt.UtcTicks, id, flags);
            }

            groups = Groups.Rent((int)nextId);
            context = GCHandle.Alloc(events);
            fixed (Row* input = rows)
            fixed (Group* output = groups)
                if (ProjectSessions(input, (uint)events.Count, nextId, output, &CompareProject, GCHandle.ToIntPtr(context)) != nextId)
                    throw new InvalidOperationException("Invalid Claude scan session metadata.");

            var result = new SessionDetails[(int)nextId];
            for (var i = 0; i < result.Length; i++)
            {
                var group = groups[i];
                var project = group.Project == uint.MaxValue ? null : events[(int)group.Project];
                var parent = group.Parent == uint.MaxValue ? null : events[(int)group.Parent].ImportParentSessionId;
                result[i] = new(events[(int)group.First].SessionId, parent, [])
                {
                    StartedAt = events[(int)group.Earliest].OccurredAt,
                    ProjectName = project?.Project,
                    ProjectObservedAt = project?.OccurredAt
                };
            }
            return result;
        }
        finally
        {
            if (context.IsAllocated) context.Free();
            if (groups is not null) Groups.Return(groups);
            Rows.Return(rows);
        }
    }
}
#endif
