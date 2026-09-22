using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace CodeRim.Core.Services;

// A deliberately restricted, read-only decoder. Never invokes LevelDB recovery on a user's files.
internal sealed class BoundedLevelDbReader(CancellationToken token)
{
    internal const int MaximumFile = 32 * 1024 * 1024;
    internal const int MaximumBytes = 64 * 1024 * 1024;
    private const ulong MaximumSequence = (1UL << 56) - 1;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private long expanded;
    private int records;
    private static readonly uint[] CrcTable = CreateCrcTable();
    internal sealed record Table(int Level, ulong Number, ulong Size, byte[] Smallest, byte[] Largest);
    internal sealed record Version(ulong Log, ulong PreviousLog, ulong LastSequence, Dictionary<ulong, Table> Tables);
    private sealed record Value(ulong Sequence, byte Type, byte[] Bytes);
    private readonly Dictionary<string, Value> values = new(StringComparer.Ordinal);
    private HashSet<string> wanted = new(StringComparer.Ordinal);
    private HashSet<string> unsupportedKeys = new(StringComparer.Ordinal);
    internal static ChromiumStorageException Invalid() => new(ChromiumStorageFailure.InvalidData);
    private static ChromiumStorageException Unsupported() => new(ChromiumStorageFailure.UnsupportedFormat);
    internal static void Limit(bool condition) { if (!condition) throw new ChromiumStorageException(ChromiumStorageFailure.PolicyLimit); }
    internal void Check()
    {
        token.ThrowIfCancellationRequested();
        Limit(elapsed.Elapsed < TimeSpan.FromSeconds(5));
    }
    private void Record() { Check(); Limit(++records <= 200000); }
    private void Expanded(int bytes) { expanded += bytes; Limit(expanded <= MaximumBytes); }
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        { var c = n; for (var bit = 0; bit < 8; bit++) c = (c >> 1) ^ ((c & 1) == 0 ? 0 : 0x82f63b78); table[n] = c; }
        return table;
    }
    private uint Crc(ReadOnlySpan<byte> bytes, byte type, bool typeFirst)
    {
        var crc = uint.MaxValue;
        if (typeFirst) crc = CrcTable[(crc ^ type) & 255] ^ (crc >> 8);
        for (var i = 0; i < bytes.Length; i++)
        { if ((i & 16383) == 0) Check(); crc = CrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8); }
        if (!typeFirst) crc = CrcTable[(crc ^ type) & 255] ^ (crc >> 8);
        crc = ~crc;
        return unchecked(((crc >> 15) | (crc << 17)) + 0xa282ead8);
    }
    private sealed class Cursor(ReadOnlyMemory<byte> data)
    {
        internal ReadOnlyMemory<byte> Data { get; } = data;
        internal int Position { get; set; }
        internal bool End => Position == Data.Length;
        internal byte Byte() => Position < Data.Length ? Data.Span[Position++] : throw Invalid();
        internal ulong Varint()
        {
            ulong value = 0;
            for (var index = 0; index < 10; index++)
            {
                var b = Byte(); if (index == 9 && b > 1) throw Invalid();
                value |= (ulong)(b & 127) << (7 * index);
                if (b < 128) { if (index > 0 && b == 0) throw Invalid(); return value; }
            }
            throw Invalid();
        }
        internal int Length(int max)
        { var length = Varint(); Limit(length <= (ulong)max); return (int)length; }
        internal ReadOnlyMemory<byte> Take(int count)
        { if (count < 0 || count > Data.Length - Position) throw Invalid(); var slice = Data.Slice(Position, count); Position += count; return slice; }
        internal ReadOnlyMemory<byte> Slice(int max) => Take(Length(max));
    }
    private List<byte[]> Log(ReadOnlyMemory<byte> data)
    {
        var result = new List<byte[]>(); MemoryStream? fragment = null;
        try
        {
            for (var block = 0; block < data.Length; block += 32768)
            {
                var end = Math.Min(block + 32768, data.Length); var at = block;
                while (at < end)
                {
                    Check(); var remaining = end - at;
                    if (remaining < 7)
                    { if (data.Span.Slice(at, remaining).IndexOfAnyExcept((byte)0) >= 0) throw Invalid(); break; }
                    var header = data.Span.Slice(at, 7); var crc = BinaryPrimitives.ReadUInt32LittleEndian(header);
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]); var type = header[6]; at += 7;
                    if (crc == 0 && length == 0 && type == 0)
                    { if (data.Span.Slice(at, end - at).IndexOfAnyExcept((byte)0) >= 0) throw Invalid(); break; }
                    if (length > end - at || type is < 1 or > 4) throw Invalid();
                    var bytes = data.Slice(at, length); at += length;
                    if (Crc(bytes.Span, type, true) != crc) throw Invalid();
                    if (type == 1)
                    { if (fragment is not null) throw Invalid(); Expanded(bytes.Length); result.Add(bytes.ToArray()); }
                    else if (type == 2)
                    { if (fragment is not null) throw Invalid(); fragment = new(); fragment.Write(bytes.Span); }
                    else
                    {
                        if (fragment is null) throw Invalid(); Limit(fragment.Length + bytes.Length <= 8 * 1024 * 1024); fragment.Write(bytes.Span);
                        if (type == 4) { Expanded((int)fragment.Length); result.Add(fragment.ToArray()); fragment.Dispose(); fragment = null; }
                    }
                    Limit(result.Count <= 200000);
                }
            }
            // Strict snapshots require a complete last record, unlike tolerant forensic readers.
            if (fragment is not null) throw Invalid(); Check(); return result;
        }
        finally { fragment?.Dispose(); }
    }
    internal Version Manifest(byte[] data)
    {
        var tables = new Dictionary<ulong, Table>(); ulong? log = null, sequence = null, next = null; ulong previous = 0; var comparator = false;
        foreach (var entry in Log(data))
        {
            var input = new Cursor(entry); var scalarTags = new HashSet<ulong>();
            var removed = new List<(int Level, ulong Number)>(); var added = new List<Table>();
            while (!input.End)
            {
                Record(); var tag = input.Varint();
                if (tag is 1 or 2 or 3 or 4 or 9 && !scalarTags.Add(tag)) throw Invalid();
                switch (tag)
                {
                    case 1: if (!input.Slice(128).Span.SequenceEqual("leveldb.BytewiseComparator"u8)) throw Unsupported(); comparator = true; break;
                    case 2: log = input.Varint(); break;
                    case 3: next = input.Varint(); break;
                    case 4: sequence = input.Varint(); if (sequence > MaximumSequence) throw Invalid(); break;
                    case 9: previous = input.Varint(); break;
                    case 5: _ = Level(input); _ = InternalKey(input.Slice(8192).Span); break;
                    case 6: removed.Add((Level(input), input.Varint())); break;
                    case 7:
                        var level = Level(input); var number = input.Varint(); var size = input.Varint();
                        var smallest = input.Slice(8192).ToArray(); var largest = input.Slice(8192).ToArray();
                        _ = InternalKey(smallest); _ = InternalKey(largest);
                        if (number == 0 || size < 48 || CompareInternal(smallest, largest) > 0) throw Invalid();
                        Limit(size <= MaximumFile); added.Add(new(level, number, size, smallest, largest)); break;
                    default: throw Unsupported();
                }
            }
            foreach (var deleted in removed)
            { if (tables.TryGetValue(deleted.Number, out var existing) && existing.Level != deleted.Level) throw Invalid(); tables.Remove(deleted.Number); }
            foreach (var table in added) if (!tables.TryAdd(table.Number, table)) throw Invalid();
            Limit(tables.Count <= 128);
        }
        if (!comparator || log is null || log == 0 || sequence is null || next is null || next <= log || previous >= next || tables.Keys.Any(x => x >= next)) throw Unsupported();
        return new(log.Value, previous, sequence.Value, tables);
    }
    private static int Level(Cursor input) { var value = input.Varint(); if (value > 6) throw Unsupported(); return (int)value; }
    private static (ulong Sequence, byte Type) InternalKey(ReadOnlySpan<byte> key)
    {
        if (key.Length < 8) throw Invalid(); var tag = BinaryPrimitives.ReadUInt64LittleEndian(key[^8..]);
        var type = (byte)(tag & 255); if (type > 1) throw Unsupported(); return (tag >> 8, type);
    }
    private static int CompareInternal(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        _ = InternalKey(left); _ = InternalKey(right);
        var user = left[..^8].SequenceCompareTo(right[..^8]); if (user != 0) return user;
        return BinaryPrimitives.ReadUInt64LittleEndian(right[^8..]).CompareTo(BinaryPrimitives.ReadUInt64LittleEndian(left[^8..]));
    }
    internal Dictionary<string, byte[]> Read(Version version, IReadOnlyDictionary<ulong, byte[]> tables, IEnumerable<byte[]> logs, IEnumerable<byte[]> keys, IEnumerable<byte[]> noncanonicalKeys)
    {
        wanted = keys.Select(Convert.ToHexString).ToHashSet(StringComparer.Ordinal);
        unsupportedKeys = noncanonicalKeys.Select(Convert.ToHexString).ToHashSet(StringComparer.Ordinal); values.Clear();
        foreach (var table in version.Tables.Values)
        { Check(); if (!tables.TryGetValue(table.Number, out var bytes) || (ulong)bytes.Length != table.Size) throw Invalid(); TableData(table, bytes, version.LastSequence); }
        foreach (var bytes in logs) foreach (var record in Log(bytes)) Batch(record);
        Check(); return values.Where(x => x.Value.Type == 1).ToDictionary(x => x.Key, x => x.Value.Bytes, StringComparer.Ordinal);
    }
    private void Apply(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ulong sequence, byte type)
    {
        Record(); Limit(key.Length <= 8192 && value.Length <= 1024 * 1024);
        if (sequence > MaximumSequence || type > 1 || type == 0 && value.Length != 0) throw Invalid();
        var name = Convert.ToHexString(key);
        // ASCII script keys are canonically Latin1 in Chromium. Reject legacy aliases even
        // when deleted/older: merging only live values could resurrect a removed account.
        if (unsupportedKeys.Contains(name)) throw Unsupported();
        if (!wanted.Contains(name)) return;
        if (values.TryGetValue(name, out var old))
        {
            if (old.Sequence > sequence) return;
            if (old.Sequence == sequence)
            { if (old.Type != type || !old.Bytes.AsSpan().SequenceEqual(value)) throw Invalid(); return; }
        }
        values[name] = new(sequence, type, value.ToArray());
    }
    private void Batch(byte[] bytes)
    {
        if (bytes.Length < 12) throw Invalid(); var sequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes); var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        Limit(count <= 200000); if (sequence > MaximumSequence || count > 0 && count - 1 > MaximumSequence - sequence) throw Invalid();
        var cursor = new Cursor(bytes) { Position = 12 }; var entries = new List<(byte Type, ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Value)>();
        for (var n = 0; n < count; n++)
        {
            Check(); var type = cursor.Byte(); if (type > 1) throw Unsupported(); var key = cursor.Slice(8192); var value = type == 1 ? cursor.Slice(1024 * 1024) : ReadOnlyMemory<byte>.Empty; entries.Add((type, key, value));
        }
        if (!cursor.End) throw Invalid();
        foreach (var entry in entries) Apply(entry.Key.Span, entry.Value.Span, sequence++, entry.Type);
    }
    private sealed record Handle(int Offset, int Size) { internal int End => checked(Offset + Size + 5); }
    private static Handle HandleFrom(Cursor input, int end)
    {
        var offset = input.Varint(); var size = input.Varint(); Limit(size <= 8 * 1024 * 1024);
        if (offset > (ulong)end || size + 5 > (ulong)end - offset) throw Invalid(); return new((int)offset, (int)size);
    }
    private byte[] Block(byte[] table, Handle handle)
    {
        Check(); var payload = table.AsSpan(handle.Offset, handle.Size); var type = table[handle.Offset + handle.Size];
        if (Crc(payload, type, false) != BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(handle.Offset + handle.Size + 1, 4))) throw Invalid();
        var bytes = type switch { 0 => payload.ToArray(), 1 => Snappy(payload.ToArray()), _ => throw Unsupported() };
        Expanded(bytes.Length); return bytes;
    }
    private void TableData(Table info, byte[] bytes, ulong lastSequence)
    {
        var footerAt = bytes.Length - 48; var footer = new Cursor(bytes.AsMemory(footerAt, 40));
        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8)) != 0xdb4775248b80fb57) throw Invalid();
        var meta = HandleFrom(footer, footerAt); var index = HandleFrom(footer, footerAt);
        if (footer.Data.Span[footer.Position..].IndexOfAnyExcept((byte)0) >= 0) throw Invalid();
        var used = new List<Handle>();
        void Reserve(Handle handle) { if (used.Any(x => handle.Offset < x.End && x.Offset < handle.End)) throw Invalid(); used.Add(handle); Limit(used.Count <= 65536); }
        Reserve(meta); Reserve(index); _ = Entries(Block(bytes, meta)); var indexes = Entries(Block(bytes, index)); byte[]? previous = null; byte[]? previousIndex = null;
        foreach (var row in indexes)
        {
            _ = InternalKey(row.Key); if (previousIndex is not null && CompareInternal(previousIndex, row.Key) >= 0) throw Invalid(); previousIndex = row.Key;
            var input = new Cursor(row.Value); var handle = HandleFrom(input, footerAt); if (!input.End) throw Invalid(); Reserve(handle);
            var rows = Entries(Block(bytes, handle)); if (rows.Count == 0) throw Invalid();
            foreach (var entry in rows)
            {
                var tag = InternalKey(entry.Key); if (tag.Sequence > lastSequence) throw Invalid();
                if (previous is not null && CompareInternal(previous, entry.Key) >= 0 || CompareInternal(entry.Key, info.Smallest) < 0 || CompareInternal(entry.Key, info.Largest) > 0 || CompareInternal(entry.Key, row.Key) > 0) throw Invalid();
                Apply(entry.Key.AsSpan(0, entry.Key.Length - 8), entry.Value, tag.Sequence, tag.Type); previous = entry.Key;
            }
        }
        if (previous is null) throw Invalid();
    }
    private List<(byte[] Key, byte[] Value)> Entries(byte[] block)
    {
        if (block.Length < 8) throw Invalid(); var count = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(block.Length - 4));
        if (count == 0 || count > (block.Length - 4) / 4) throw Invalid(); var end = block.Length - 4 - (int)count * 4;
        var restarts = new HashSet<int>(); var previousRestart = -1;
        for (var n = 0; n < count; n++)
        { Check(); var start = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(end + (int)n * 4)); if (start > end || start <= previousRestart) throw Invalid(); previousRestart = (int)start; restarts.Add((int)start); }
        if (!restarts.Contains(0)) throw Invalid(); if (end == 0 && count == 1) return [];
        var input = new Cursor(block.AsMemory(0, end)); var result = new List<(byte[], byte[])>(); byte[] previous = [];
        while (!input.End)
        {
            Record(); var at = input.Position; var shared = input.Length(8192); var remaining = input.Length(8192); var length = input.Length(1024 * 1024);
            if (shared > previous.Length || shared + remaining > 8192 || restarts.Remove(at) && shared != 0) throw Invalid();
            var key = new byte[shared + remaining]; previous.AsSpan(0, shared).CopyTo(key); input.Take(remaining).Span.CopyTo(key.AsSpan(shared));
            var value = input.Take(length).ToArray(); result.Add((key, value)); previous = key;
        }
        if (restarts.Count != 0) throw Invalid(); return result;
    }
    private byte[] Snappy(byte[] compressed)
    {
        var input = new Cursor(compressed); var expected = input.Length(8 * 1024 * 1024); var output = new byte[expected]; var at = 0;
        while (!input.End)
        {
            Check(); var tag = input.Byte(); var type = tag & 3; int length; uint offset;
            if (type == 0)
            {
                length = tag >> 2;
                if (length < 60) length++;
                else
                {
                    var extra = length - 59; uint raw = 0;
                    for (var n = 0; n < extra; n++) raw |= (uint)input.Byte() << (8 * n);
                    Limit(raw < 8 * 1024 * 1024); length = (int)raw + 1;
                }
                if (length > expected - at) throw Invalid(); input.Take(length).Span.CopyTo(output.AsSpan(at)); at += length; continue;
            }
            if (type == 1) { length = 4 + ((tag >> 2) & 7); offset = (uint)((tag & 224) << 3) | input.Byte(); }
            else
            { length = 1 + (tag >> 2); offset = input.Byte(); offset |= (uint)input.Byte() << 8; if (type == 3) { offset |= (uint)input.Byte() << 16; offset |= (uint)input.Byte() << 24; } }
            if (offset == 0 || offset > at || length > expected - at) throw Invalid();
            for (var n = 0; n < length; n++) { output[at] = output[at - (int)offset]; at++; }
        }
        if (at != expected) throw Invalid(); return output;
    }
}
