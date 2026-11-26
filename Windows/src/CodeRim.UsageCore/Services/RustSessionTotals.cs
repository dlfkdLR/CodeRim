#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

internal static class RustSessionTotals
{
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Edge(int Parent, int Child);
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Root(int Node, int Owner);
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Event(ulong Owners, RustAccountingLibrary.RawUsage Usage);
    internal const int BatchSize = 256;

    static RustSessionTotals() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_windows_session_owners", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static unsafe extern int Attribute(Root* roots, int rootsCount, Edge* edges, int edgesCount, ulong* owners, int nodes, int* cancellation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_windows_session_tokens", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static unsafe extern int Aggregate(Event* events, int count, long* totals, int owners);

    internal static unsafe void Attribute(ReadOnlySpan<Root> roots, ReadOnlySpan<Edge> edges, Span<ulong> owners, CancellationToken token)
    {
        var signal = new int[1];
        using var registration = token.UnsafeRegister(static state => Volatile.Write(ref ((int[])state!)[0], 1), signal);
        // Synchronous numeric borrows. The cancellation callback and Rust access a single aligned
        // 32-bit flag atomically. Registration disposal waits for any callback before returning.
        fixed (Root* r = roots)
        fixed (Edge* e = edges)
        fixed (ulong* o = owners)
        fixed (int* flag = signal)
        {
            var status = Attribute(r, roots.Length, e, edges.Length, o, owners.Length, flag);
            token.ThrowIfCancellationRequested();
            if (status != 1) throw new InvalidDataException("Invalid session attribution.");
        }
    }

    internal static unsafe void Aggregate(ReadOnlySpan<Event> events, Span<long> totals)
    {
        fixed (Event* e = events)
        fixed (long* t = totals)
            if (Aggregate(e, events.Length, t, totals.Length) != 1)
                throw new InvalidDataException("Invalid session token batch.");
    }
}
#endif
