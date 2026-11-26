#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;
#endif

namespace CodeRim.Core.Parsing;

internal static class BoundedLineChunk
{
    internal const uint Oversized = 1u << 31;
#if CODERIM_RUST_ACCOUNTING
    [StructLayout(LayoutKind.Sequential)]
#endif
    internal readonly record struct Result(uint Consumed, uint Count, uint Pending);

#if CODERIM_RUST_ACCOUNTING
    internal static Result Inspect(ReadOnlySpan<byte> bytes, uint pending, uint[] ends)
    {
        int first = bytes.IndexOf((byte)'\n');
        if (first < 0)
            return new((uint)bytes.Length, 0, Math.Min(pending + (uint)bytes.Length, CodexJsonlParser.MaximumLineBytes + 1));
        if (first == bytes.Length - 1)
        {
            ends[0] = (uint)first | (pending + (uint)first > CodexJsonlParser.MaximumLineBytes ? Oversized : 0);
            return new((uint)bytes.Length, 1, 0);
        }
        return Native.Inspect(bytes, pending, ends);
    }

    private static class Native
    {
        static Native() => RustAccountingLibrary.EnsureResolverInstalled();

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(RustAccountingLibrary.BridgeName, EntryPoint = "cr_windows_bounded_lines",
            CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern unsafe Result Project(byte* input, uint length, uint pending, uint limit, uint* ends, uint capacity);

        internal static unsafe Result Inspect(ReadOnlySpan<byte> bytes, uint pending, uint[] ends)
        {
            fixed (byte* input = bytes)
            fixed (uint* output = ends)
                return Project(input, (uint)bytes.Length, pending, CodexJsonlParser.MaximumLineBytes, output, (uint)ends.Length);
        }
    }
#else
    internal static Result Inspect(ReadOnlySpan<byte> bytes, uint pending, uint[] ends)
    {
        var start = 0;
        var count = 0;
        while (start < bytes.Length)
        {
            var relative = bytes[start..].IndexOf((byte)'\n');
            if (relative < 0) break;
            var end = start + relative;
            ends[count++] = (uint)end | (pending + relative > CodexJsonlParser.MaximumLineBytes ? Oversized : 0);
            start = end + 1;
            pending = 0;
            if (count == ends.Length) return new((uint)start, (uint)count, 0);
        }
        return new((uint)bytes.Length, (uint)count,
            Math.Min(pending + (uint)(bytes.Length - start), CodexJsonlParser.MaximumLineBytes + 1));
    }
#endif
}
