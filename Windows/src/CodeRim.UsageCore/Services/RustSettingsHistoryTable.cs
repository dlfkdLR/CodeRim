#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

internal static class RustSettingsHistoryTable
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Event(long At, long Input, long Cached, long Output, long Written, int Model, int Session);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Group(long Input, long Cached, long Output, long Written, long LatestPlusOne)
    {
        internal TokenUsage Usage => new(Input, Cached, Output, Written < 0 ? null : Written);
    }

    static RustSettingsHistoryTable() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_windows_settings_history_v1", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe int Project(Event* events, nuint count, long* ends, nuint days, nuint models, nuint sessions, Group* output, nuint groups);

    internal static unsafe bool TryProject(ReadOnlySpan<Event> events, ReadOnlySpan<long> ends, int models, int sessions, Span<Group> output)
    {
        // One synchronous borrow. Keep a full GC transition because histories may contain thousands of rows.
        fixed (Event* rows = events)
        fixed (long* limits = ends)
        fixed (Group* groups = output)
            return Project(rows, (nuint)events.Length, limits, (nuint)ends.Length, (nuint)models, (nuint)sessions, groups, (nuint)output.Length) == 1;
    }
}
#endif
