#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

internal static class RustStoredUsage
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Restored(long Ticks, long Input, int Context);

    static RustStoredUsage() => RustAccountingLibrary.EnsureResolverInstalled();

    // Pure integer leaf function, with no allocation, blocking, I/O or reverse callbacks.
    [SuppressGCTransition]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_restore_windows_history", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern Restored Restore(long unixMs, long exclusiveInput, long cached, long written, long context);
}
#endif
