using System.Runtime.InteropServices;

namespace CodeRim.Core.Domain;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct NotchOutline(double Depth, double Length, double Curl, double Corner);

#if CODERIM_RUST_ACCOUNTING
internal static class RustNotchOutline
{
    static RustNotchOutline() => RustAccountingLibrary.EnsureResolverInstalled();

    // A value-only numeric calculation; it never calls back, allocates or blocks.
    [SuppressGCTransition]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_windows_notch_outline",
        CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NotchOutline Calculate(double fullDepth, double fullLength, double expansion,
        double scale, double pillDepth, double pillLength, double curl, double corner);
}
#endif
