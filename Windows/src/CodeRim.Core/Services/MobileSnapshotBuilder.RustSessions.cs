#if CODERIM_RUST_ACCOUNTING
using System.Buffers;
using System.Text;
using System.Runtime.InteropServices;

namespace CodeRim.Core.Services;

public static partial class MobileSnapshotBuilder
{
    private static MobileSession[]? TrySelectSessions(IReadOnlyList<SessionActivity> sessions, HashSet<string> enabled, bool shareTitles)
    {
        // The public API accepts custom lists. Preserve their enumeration and error timing
        // in the managed implementation. The desktop store supplies an array.
        ReadOnlySpan<SessionActivity> source;
        if (sessions is SessionActivity[] array) source = array;
        else if (sessions?.GetType() == typeof(List<SessionActivity>)) source = CollectionsMarshal.AsSpan((List<SessionActivity>)sessions);
        else return null;
        int length = source.Length;
        if (length == 0) return [];
        if (length > 1_000_000) return null;
        RustMobileSessionOrder.Key[]? rentedKeys = null;
        int[]? rentedPositions = null;
        try
        {
            Span<RustMobileSessionOrder.Key> keys = length <= 128 ? stackalloc RustMobileSessionOrder.Key[length]
                : (rentedKeys = ArrayPool<RustMobileSessionOrder.Key>.Shared.Rent(length));
            Span<int> positions = length <= 128 ? stackalloc int[length] : (rentedPositions = ArrayPool<int>.Shared.Rent(length));
            int count = 0;
            for (int i = 0; i < length; i++)
            {
                var session = source[i];
                if (!enabled.Contains(session.Provider)) continue;
                if (shareTitles && session.Name is null) return null;
                uint rank = session.State switch { "waiting" => 0, "busy" => 1, "idle" => 2, _ => 3 };
                keys[count] = new(Seconds(session.Since) ?? double.NegativeInfinity, rank);
                positions[count++] = i;
            }
            if (count == 0) return [];
            Span<uint> order = stackalloc uint[Math.Min(count, 64)];
            if (count == 1) order[0] = 0;
            else if (!RustMobileSessionOrder.Select(keys[..count], order)) return null;
            var result = new MobileSession[order.Length];
            for (int i = 0; i < order.Length; i++)
            {
                if (order[i] >= count) return null;
                int index = (int)order[i];
                var session = source[positions[index]];
                var key = keys[index];
                string phase = key.Rank switch { 0 => "waiting", 1 => "working", 2 => "idle", _ => "unavailable" };
                result[i] = new(session.Provider, phase, shareTitles ? SessionTitle(session.Name) : "",
                    double.IsNegativeInfinity(key.Since) ? null : key.Since);
            }
            return result;
        }
        finally
        {
            if (rentedKeys is not null) ArrayPool<RustMobileSessionOrder.Key>.Shared.Return(rentedKeys);
            if (rentedPositions is not null) ArrayPool<int>.Shared.Return(rentedPositions);
        }
    }

    // Keep .NET's Unicode scalar and replacement-character behavior and publish at most 60.
    private static string SessionTitle(string value)
    {
        int end = 0;
        for (int runes = 0; runes < 60 && end < value.Length; runes++)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(end), out _, out int chars) != OperationStatus.Done) return Short(value, 60);
            end += chars;
        }
        return end == value.Length ? value : value[..end];
    }
}
#endif
