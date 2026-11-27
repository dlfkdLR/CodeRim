#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

internal static class RustActivityGroups
{
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Node(long UtcTicks, int Parent, int Rank);
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Row(int Index, int Depth);

    static RustActivityGroups() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_windows_activity_groups", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static unsafe extern int Plan(Node* nodes, int count, Row* output);

    internal static unsafe void Plan(ReadOnlySpan<Node> nodes, Span<Row> rows)
    {
        if (rows.Length != nodes.Length) throw new ArgumentException("Activity row count must match the node count.");
        fixed (Node* input = nodes)
        fixed (Row* output = rows)
            if (Plan(input, nodes.Length, output) != 1)
                throw new InvalidDataException("Invalid activity hierarchy.");
    }
}
#endif
