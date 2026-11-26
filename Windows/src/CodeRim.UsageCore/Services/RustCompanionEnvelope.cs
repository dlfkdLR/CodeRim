#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

internal static class RustCompanionEnvelope
{
    internal const int ProviderLimit = 70;
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Range(int Start, int Length)
    {
        internal ReadOnlySpan<byte> From(ReadOnlySpan<byte> bytes) => bytes.Slice(Start, Length);
    }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Period(RustAccountingLibrary.RawUsage Usage, int Kind);
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Row(Range Id, Range Name, Range Limits, Range Cache, Range AccountScope,
        Range Scope, Range State, Range UpdatedAt, Range PeriodsAsOf, Range Zone, int Enabled, int Stale, int Count,
        Period First, Period Second, Period Third, Period Fourth)
    {
        internal Period At(int n) => n switch { 0 => First, 1 => Second, 2 => Third, 3 => Fourth, _ => throw new ArgumentOutOfRangeException(nameof(n)) };
    }

    static RustCompanionEnvelope() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_windows_companion_v1", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe int Read(byte* bytes, int length, Row* rows, int capacity, Range* date);

    internal static unsafe int Read(ReadOnlySpan<byte> bytes, Span<Row> rows, out Range date)
    {
        // The decoder borrows wire bytes and writes bounded numeric rows. Neither buffer
        // escapes the call; offsets in the result reference the original pinned byte span.
        Range generatedAt = default;
        fixed (byte* data = bytes)
        fixed (Row* output = rows)
        {
            var count = Read(data, bytes.Length, output, rows.Length, &generatedAt);
            date = generatedAt;
            return count;
        }
    }
}
#endif
