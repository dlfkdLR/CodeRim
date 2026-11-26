#if CODERIM_RUST_ACCOUNTING
using System.Buffers;
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;
using static CodeRim.Core.Domain.RustAccountingLibrary;

namespace CodeRim.Core.Services;

internal static class RustModelCost
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Event(RawUsage Usage, int Context, int Model = 0);

    [Flags]
    internal enum Status : uint { Standard = 1, High = 2, FirstHigh = 4, InvalidUsage = 8, UnknownContext = 16 }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Summary(RawUsage Total, RawUsage Standard, RawUsage High, Status Flags);

    static RustModelCost() => EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(BridgeName, EntryPoint = "cr_windows_model_cost_v1", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static unsafe extern int Fold(Summary* summary, Event* events, nuint count, long highContextThreshold);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(BridgeName, EntryPoint = "cr_windows_model_cost_table_v1", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static unsafe extern int FoldTable(Summary* summaries, nuint models, Event* events, nuint count, long highContextThreshold);

    private static Event Pack(UsageEvent row, int model = 0) => new(RawUsage.From(row.Usage), row.PricingContext switch {
        null => -1, PricingContext.Standard => 0, PricingContext.HighContext => 1, _ => 2
    }, model);

    internal sealed class ModelTable : IDisposable
    {
        // Retain only ordinary-size buffers in the shared pool. Large, one-off
        // catalogs can be collected as soon as this computation finishes.
        private const int MaxPooledModels = 8192;
        private Summary[] groups = ArrayPool<Summary>.Shared.Rent(8);
        internal List<string> Names { get; } = [];
        internal Summary this[int index] => groups[index];

        private static void Release(Summary[] buffer)
        {
            if (buffer.Length is > 0 and <= MaxPooledModels) ArrayPool<Summary>.Shared.Return(buffer);
        }

        internal int Add(string name)
        {
            int index = Names.Count;
            if (index == groups.Length)
            {
                var next = index < MaxPooledModels ? ArrayPool<Summary>.Shared.Rent(index * 2) : new Summary[index * 2];
                groups.CopyTo(next, 0);
                Release(groups);
                groups = next;
            }
            // The previous renter may have left any combination of flags or totals.
            groups[index] = default;
            Names.Add(name);
            return index;
        }

        internal unsafe void Fold(Event* buffer, int count)
        {
            fixed (Summary* output = groups)
                if (FoldTable(output, (nuint)Names.Count, buffer, (nuint)count, TokenUsage.HighContextInputThreshold) != 1)
                    throw new InvalidDataException("Invalid Windows model cost table.");
        }

        public void Dispose()
        {
            Release(groups);
            groups = [];
        }
    }

    internal static unsafe ModelTable SummarizeModels(UsageEvent[] events)
    {
        var table = new ModelTable();
        try
        {
            var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
            int nullIndex = -1;
            const int capacity = 128;
            Event* buffer = stackalloc Event[capacity];
            for (int start = 0; start < events.Length; start += capacity)
            {
                int size = Math.Min(capacity, events.Length - start);
                for (int i = 0; i < size; i++)
                {
                    var row = events[start + i];
                    int index;
                    if (row.Model is null)
                    {
                        // LINQ groups null model names and visits that group in first-seen order.
                        if (nullIndex < 0) nullIndex = table.Add(null!);
                        index = nullIndex;
                    }
                    else
                    {
                        ref int id = ref CollectionsMarshal.GetValueRefOrAddDefault(indexes, row.Model, out bool found);
                        if (!found) id = table.Add(row.Model);
                        index = id;
                    }
                    buffer[i] = Pack(row, index);
                }
                table.Fold(buffer, size);
            }
            return table;
        }
        catch
        {
            table.Dispose();
            throw;
        }
    }

    internal static unsafe Summary Summarize(IEnumerable<UsageEvent> events)
    {
        const int capacity = 128;
        Event* buffer = stackalloc Event[capacity];
        var summary = default(Summary);
        int count = 0;
        foreach (var row in events)
        {
            buffer[count++] = Pack(row);
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
