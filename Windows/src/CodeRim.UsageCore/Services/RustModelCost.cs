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
        private const int FirstPageLimit = 8192;
        private const int ExtraPageSize = 512;
        private Summary[] first = ArrayPool<Summary>.Shared.Rent(8);
        private List<Summary[]>? extra;
        internal List<string> Names { get; } = [];
        internal Summary this[int index] => index < FirstPageLimit ? first[index]
            : extra![(index - FirstPageLimit) / ExtraPageSize][(index - FirstPageLimit) % ExtraPageSize];

        internal int Add(string name)
        {
            int index = Names.Count;
            if (index < FirstPageLimit)
            {
                if (index == first.Length)
                {
                    var next = ArrayPool<Summary>.Shared.Rent(index * 2);
                    first.CopyTo(next, 0);
                    ArrayPool<Summary>.Shared.Return(first);
                    first = next;
                }
                first[index] = default;
            }
            else
            {
                int offset = (index - FirstPageLimit) % ExtraPageSize;
                if (offset == 0)
                    (extra ??= []).Add(ArrayPool<Summary>.Shared.Rent(ExtraPageSize));
                extra![^1][offset] = default;
            }
            Names.Add(name);
            return index;
        }

        internal unsafe void Fold(Event* buffer, int count)
        {
            if (extra is null)
            {
                FoldPage(first, Names.Count, buffer, count);
                return;
            }
            // Dense model IDs grow in encounter order. Keep a very large catalog
            // in bounded pages and preserve the event order for each model.
            for (int start = 0; start < count;)
            {
                int id = buffer[start].Model;
                int page = id < FirstPageLimit ? -1 : (id - FirstPageLimit) / ExtraPageSize;
                int origin = page < 0 ? 0 : FirstPageLimit + page * ExtraPageSize;
                int length = page < 0 ? FirstPageLimit : Math.Min(ExtraPageSize, Names.Count - origin);
                int end = start;
                while (end < count && (uint)(buffer[end].Model - origin) < (uint)length)
                {
                    if (origin != 0) buffer[end] = buffer[end] with { Model = buffer[end].Model - origin };
                    end++;
                }
                FoldPage(page < 0 ? first : extra[page], length, buffer + start, end - start);
                start = end;
            }
        }

        private static unsafe void FoldPage(Summary[] page, int models, Event* buffer, int count)
        {
            fixed (Summary* output = page)
                if (FoldTable(output, (nuint)models, buffer, (nuint)count, TokenUsage.HighContextInputThreshold) != 1)
                    throw new InvalidDataException("Invalid Windows model cost table.");
        }

        public void Dispose()
        {
            if (first.Length == 0) return;
            ArrayPool<Summary>.Shared.Return(first);
            first = [];
            if (extra is not null)
                foreach (var page in extra) ArrayPool<Summary>.Shared.Return(page);
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
