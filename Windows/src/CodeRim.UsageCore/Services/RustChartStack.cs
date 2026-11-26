using System.Runtime.InteropServices;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

internal static class RustChartStack
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Row(long Input, long Cached, long Output, byte Slots)
    {
        internal Row(TokenUsage usage, byte slots = 0) : this(usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, slots) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Result(long First, long Second, long Third, long Fourth, long Fifth, long Other, byte Valid)
    {
        internal long this[int column] => column switch
        {
            0 => First, 1 => Second, 2 => Third, 3 => Fourth, 4 => Fifth, 5 => Other,
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };
    }

    static RustChartStack() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_project_chart_stack",
        CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe Result ProjectRaw(Row* rows, nuint length, byte mode);

    internal static unsafe Result ProjectModels(ReadOnlySpan<Row> rows)
    {
        // One synchronous borrow of the initialized span. IDs and display strings stay in .NET.
        fixed (Row* start = rows)
        {
            var result = ProjectRaw(start, (nuint)rows.Length, 2);
            return result.Valid == 1 ? result : throw new InvalidDataException("Invalid Rust chart projection.");
        }
    }
}
