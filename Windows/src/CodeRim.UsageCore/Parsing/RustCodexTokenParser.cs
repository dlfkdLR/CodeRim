using System.Runtime.InteropServices;
using System.Text;
using CodeRim.Core.Domain;
using static CodeRim.Core.Domain.RustAccountingLibrary;

namespace CodeRim.Core.Parsing;

internal static class RustCodexTokenParser
{
    // This is the shared cr_token_json layout. A zero timestamp length means that
    // the original Windows parser must handle this record and its diagnostics.
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TokenRow(RawUsage Last, RawUsage Cumulative,
        long HasLast, long HasCumulative, long Ordinal, long HasOrdinal, nuint Offset, nuint Length);

    static RustCodexTokenParser() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(BridgeName, EntryPoint = "cr_parse_token_json", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe TokenRow Parse(byte* input, nuint length);

    internal static unsafe TokenObservation? TryParse(ReadOnlySpan<byte> line)
    {
        if (line.IndexOf("\"token_count\""u8) < 0) return null;

        TokenRow row;
        // Pin the exact span (including its offset) for one synchronous call.
        // Rust returns numeric counters and indices and retains no pointer.
        fixed (byte* bytes = line)
            row = Parse(bytes, (nuint)line.Length);
        if (row.Length == 0) return null;
        if (row.Length > 64 || row.Offset > (nuint)line.Length || row.Length > (nuint)line.Length - row.Offset)
            throw new InvalidDataException("Rust returned an invalid timestamp range.");

        Span<char> timestamp = stackalloc char[(int)row.Length];
        Encoding.ASCII.GetChars(line.Slice((int)row.Offset, (int)row.Length), timestamp);
        if (CodexJsonlParser.ParseTimestamp(timestamp) is not { } date) return null;
        return new TokenObservation(date, row.HasOrdinal == 0 ? null : row.Ordinal,
            row.HasLast == 0 ? null : row.Last.ToUsage(), row.HasCumulative == 0 ? null : row.Cumulative.ToUsage());
    }
}
