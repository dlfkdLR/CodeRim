#if CODERIM_RUST_ACCOUNTING
using System.Numerics;
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

internal sealed class RustCodexAttachments
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Window
    {
        internal long LatestVisible;
        internal long FirstFuture;
    }

    private readonly Window window;
    private readonly int visible;
    private readonly ulong[]? selection;
    private RustCodexAttachments(Window window, int visible, ulong[]? selection)
        => (this.window, this.visible, this.selection) = (window, visible, selection);
    static RustCodexAttachments() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_windows_codex_attachment_mask",
        CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe ulong SelectBlock(long* times, uint count, long now, ref Window window);

    internal bool Contains(long now) => now >= window.LatestVisible && now < window.FirstFuture;

    private static ReadOnlySpan<AttachmentObservation> Observations(IReadOnlyList<AttachmentObservation> source)
    {
        if (source is List<AttachmentObservation> list && list.GetType() == typeof(List<AttachmentObservation>))
            return CollectionsMarshal.AsSpan(list);
        return source is AttachmentObservation[] array ? array : source.ToArray();
    }

    internal static unsafe RustCodexAttachments Select(IReadOnlyList<AttachmentObservation> source, long now)
    {
        var observations = Observations(source);
        var count = observations.Length;
        int blocks = (count >> 6) + ((count & 63) != 0 ? 1 : 0);
        ulong[]? large = null;
        Span<ulong> masks = blocks <= 64 ? stackalloc ulong[blocks] : [];
        var mixed = false;
        long* times = stackalloc long[64];
        var window = new Window { LatestVisible = long.MinValue, FirstFuture = long.MaxValue };
        var visible = 0;
        for (int block = 0; block < blocks; block++)
        {
            var start = block << 6;
            var length = Math.Min(64, count - start);
            for (int i = 0; i < length; i++) times[i] = observations[start + i].OccurredAt.UtcTicks;
            var mask = SelectBlock(times, (uint)length, now, ref window);
            int selected = BitOperations.PopCount(mask);
            if (!mixed && (visible != 0 || selected != 0) && (visible != start || selected != length))
            {
                // Allocate a retained mask only when both visible and future
                // observations exist. All preceding blocks share one value.
                if (masks.IsEmpty) masks = large = new ulong[blocks];
                masks[..block].Fill(visible == 0 ? 0 : ulong.MaxValue);
                mixed = true;
            }
            if (mixed) masks[block] = mask;
            visible += selected;
        }
        return new(window, visible, mixed ? large ?? masks.ToArray() : null);
    }

    internal AttachmentObservation[] Materialize(IReadOnlyList<AttachmentObservation> source)
    {
        if (visible == 0) return [];
        // Callers receive their own array. Mutating a previous ScanResult cannot
        // change the immutable selection or the scanner's private source list.
        if (selection is null) return source.ToArray();
        var observations = Observations(source);
        var result = new AttachmentObservation[visible];
        var used = 0;
        for (int block = 0; block < selection.Length; block++)
        {
            var mask = selection[block];
            if (mask == ulong.MaxValue)
            {
                observations.Slice(block << 6, 64).CopyTo(result.AsSpan(used));
                used += 64;
                continue;
            }
            while (mask != 0)
            {
                var index = (block << 6) + BitOperations.TrailingZeroCount(mask);
                result[used++] = observations[index];
                mask &= mask - 1;
            }
        }
        return result;
    }
}
#endif
