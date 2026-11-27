#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

internal static class RustReleasePage
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Row(int Major, int Minor, int Build, int UrlStart, int UrlLength,
        int DigestStart, int DigestLength, int Installer, long ReleaseId, long AssetId, long Size);

    static RustReleasePage() => RustAccountingLibrary.EnsureResolverInstalled();

    internal static unsafe int Project(ReadOnlySpan<byte> bytes, string architecture, bool preferInstaller, Span<Row> rows)
    {
        fixed (byte* data = bytes)
        fixed (Row* output = rows)
            return ProjectNative(data, (nuint)bytes.Length, architecture == "arm64" ? 1 : 0,
                preferInstaller ? 1 : 0, output, (nuint)rows.Length);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_windows_release_page", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe int ProjectNative(byte* data, nuint size, int arm64, int preferInstaller, Row* rows, nuint capacity);
}
#endif
