#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;
using static CodeRim.Core.Domain.RustAccountingLibrary;

namespace CodeRim.Core.Services;

internal static class RustModelCost
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Event(RawUsage Usage, int Context);

    [Flags]
    internal enum Status : uint { Standard = 1, High = 2, FirstHigh = 4, InvalidUsage = 8, UnknownContext = 16 }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Summary(RawUsage Total, RawUsage Standard, RawUsage High, Status Flags);

    static RustModelCost() => EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(BridgeName, EntryPoint = "cr_windows_model_cost_v1", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static unsafe extern int Fold(Summary* summary, Event* events, nuint count, long highContextThreshold);

    internal static unsafe Summary Summarize(IEnumerable<UsageEvent> events)
    {
        const int capacity = 128;
        Event* buffer = stackalloc Event[capacity];
        var summary = default(Summary);
        int count = 0;
        foreach (var row in events)
        {
            var context = row.PricingContext switch {
                null => -1, PricingContext.Standard => 0, PricingContext.HighContext => 1, _ => 2
            };
            buffer[count++] = new(RawUsage.From(row.Usage), context);
            if (count != capacity) continue;
            FoldBlock(&summary, buffer, count);
            count = 0;
        }
        if (count > 0) FoldBlock(&summary, buffer, count);
        return summary;
    }

    private static unsafe void FoldBlock(Summary* summary, Event* events, int count)
    {
        if (Fold(summary, events, (nuint)count, TokenUsage.HighContextInputThreshold) != 1)
            throw new InvalidDataException("Invalid Windows model cost batch.");
    }
}
#endif
