#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

internal static class RustMobileSessionOrder
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Key(double Since, uint Rank, uint Reserved = 0);
    static RustMobileSessionOrder() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_mobile_session_order",
        CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe uint Sort(Key* keys, uint count, uint* indices, uint capacity);

    internal static unsafe bool Select(ReadOnlySpan<Key> keys, Span<uint> indices)
    {
        fixed (Key* input = keys)
        fixed (uint* output = indices)
            return Sort(input, (uint)keys.Length, output, (uint)indices.Length) == (uint)indices.Length;
    }
}
#endif
