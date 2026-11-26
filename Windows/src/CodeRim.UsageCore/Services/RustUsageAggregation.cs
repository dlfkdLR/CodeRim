using System.Buffers;
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;
using static CodeRim.Core.Domain.RustAccountingLibrary;

namespace CodeRim.Core.Services;

internal static class RustUsageAggregation
{
    // The bridge consumes these numeric rows synchronously in batches. Model, project
    // and session strings stay in the caller. The source event supplies the winning offset.
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NumericEvent(long At, RawUsage Usage, long Repaired);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Bounds(long Through, long Today, long Week, long Month);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Totals(RawUsage Today, RawUsage Week, RawUsage Month, RawUsage AllTime,
        long NewestTicks, int NewestIndex, int Marked);

    static RustUsageAggregation() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(BridgeName, EntryPoint = "cr_aggregate_windows", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern Totals AggregateBatch(Totals totals, Bounds bounds,
        nint events, int count, int start);

    internal static UsageSnapshot Aggregate(IReadOnlyList<UsageEvent> events, DateTimeOffset now,
        DateTimeOffset today, DateTimeOffset week, DateTimeOffset month, bool partial, bool sourcesPartial, string zone)
    {
        var zero = RawUsage.From(TokenUsage.Zero);
        var totals = new Totals(zero, zero, zero, zero, 0, -1, 0);
        var bounds = new Bounds(now.UtcTicks, today.UtcTicks, week.UtcTicks, month.UtcTicks);
        var length = events.Count;
        if (length > 0)
        {
            const int capacity = 256;
            var block = ArrayPool<NumericEvent>.Shared.Rent(Math.Min(length, capacity));
            GCHandle pinned = default;
            try
            {
                // Pin the pool buffer once for this scan. Rust only borrows each initialized prefix
                // synchronously and never retains a pointer to the array after AggregateBatch.
                pinned = GCHandle.Alloc(block, GCHandleType.Pinned);
                var address = pinned.AddrOfPinnedObject();
                for (var start = 0; start < length;)
                {
                    var count = Math.Min(capacity, length - start);
                    for (var i = 0; i < count; i++)
                    {
                        var e = events[start + i];
                        block[i] = new(e.OccurredAt.UtcTicks, RawUsage.From(e.Usage), e.Repaired ? 1 : 0);
                    }
                    totals = AggregateBatch(totals, bounds, address, count, start);
                    if (totals.NewestIndex < -1 || totals.NewestIndex >= start + count)
                        throw new InvalidDataException("Invalid event index returned by Rust aggregation.");
                    start += count;
                }
            }
            finally
            {
                if (pinned.IsAllocated) pinned.Free();
                ArrayPool<NumericEvent>.Shared.Return(block);
            }
        }
        DateTimeOffset? newest = totals.NewestIndex < 0 ? null : events[totals.NewestIndex].OccurredAt;
        var quality = partial || totals.Marked != 0 ? DataQuality.Partial
            : newest.HasValue ? DataQuality.Exact : DataQuality.Unavailable;
        return new UsageSnapshot(totals.Today.ToUsage(), totals.Week.ToUsage(), totals.Month.ToUsage(),
            totals.AllTime.ToUsage(), quality, newest)
        { SourcesPartial = sourcesPartial, Through = now, TimeZoneIdentifier = zone };
    }
}
