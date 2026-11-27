#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

internal static class RustActivityRefresh
{
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Node(long Since, long ProcessStart, int Id, int State, int Flags);
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Decision(int Attention, int Signals);

    static RustActivityRefresh() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_windows_activity_refresh", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static unsafe extern int Plan(Node* previous, int previousCount, Node* current, int currentCount,
        int identities, int* previousMatches, Decision* decision);

    internal static unsafe Decision Resolve(ReadOnlySpan<Node> old, ReadOnlySpan<Node> incoming, int identities, Span<int> matches)
    {
        if (matches.Length != incoming.Length) throw new ArgumentException("Activity match count differs from incoming sessions.");
        Decision decision;
        fixed (Node* previous = old)
        fixed (Node* current = incoming)
        fixed (int* output = matches)
            if (Plan(previous, old.Length, current, incoming.Length, identities, output, &decision) != 1)
                throw new InvalidDataException("Invalid session activity refresh.");
        return decision;
    }
}
#endif
