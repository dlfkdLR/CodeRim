#if CODERIM_RUST_ACCOUNTING
using System.Runtime.InteropServices;
using System.Text;
using CodeRim.Core.Domain;
using static CodeRim.Core.Domain.RustAccountingLibrary;

namespace CodeRim.Core.Parsing;

internal static unsafe class RustClaudeEventParser
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Field(uint Offset, uint Length)
    {
        internal string? Read(ReadOnlySpan<byte> input, ReadOnlySpan<byte> scratch)
        {
            if (Length == 0) return null;
            var bytes = (Offset & 0x8000_0000) == 0 ? input : scratch;
            var start = (int)(Offset & 0x7fff_ffff);
            if (start > bytes.Length || Length > bytes.Length - start)
                throw new InvalidDataException("Invalid Claude accounting field range.");
            return Encoding.UTF8.GetString(bytes.Slice(start, (int)Length));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Event(RawUsage Usage, Field Id, Field Session, Field Timestamp, Field Model, Field Agent, Field Cwd);

    static RustClaudeEventParser() => RustAccountingLibrary.EnsureResolverInstalled();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(BridgeName, EntryPoint = "cr_windows_claude_event", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint Parse(byte* input, nuint length, byte* scratch, nuint capacity, Event* metadata);

    internal static bool TryParse(ReadOnlySpan<byte> line, byte[]? projectKey, string? sourceName, out UsageEvent? result)
    {
        result = null;
        Span<byte> scratch = stackalloc byte[1024];
        Event row;
        uint status;
        fixed (byte* input = line)
        fixed (byte* buffer = scratch)
            status = Parse(input, (nuint)line.Length, buffer, (nuint)scratch.Length, &row);
        if (status == 0) return false;
        if (status == 2) return true;
        if (status != 1) throw new InvalidDataException("Invalid Claude accounting projection.");
        if (ClaudeJsonlParser.TryParseDate(row.Timestamp.Read(line, scratch), out var date))
            result = ClaudeJsonlParser.CreateEvent(row.Id.Read(line, scratch)!, row.Session.Read(line, scratch)!,
                row.Model.Read(line, scratch)!, date, row.Usage.ToUsage(), row.Agent.Read(line, scratch),
                row.Cwd.Read(line, scratch), projectKey, sourceName);
        return true;
    }
}
#endif
