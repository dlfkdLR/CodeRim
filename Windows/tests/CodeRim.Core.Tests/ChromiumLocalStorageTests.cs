using System.Buffers.Binary;
using System.Text;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ChromiumLocalStorageTests : IDisposable
{
    private const string Origin = "https://platform.deepseek.com";
    private static readonly string[] Keys = ["userToken"];
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "chromium-storage-" + Guid.NewGuid().ToString("N"));
    public ChromiumLocalStorageTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private static byte[] Cat(params byte[][] bytes) => bytes.SelectMany(x => x).ToArray();
    private static byte[] U32(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); return b; }
    private static byte[] U64(ulong value) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); return b; }
    private static byte[] Var(ulong value)
    { var bytes = new List<byte>(); while (value >= 128) { bytes.Add((byte)(value | 128)); value >>= 7; } bytes.Add((byte)value); return bytes.ToArray(); }
    private static byte[] String(byte[] bytes) => Cat(Var((ulong)bytes.Length), bytes);
    private static uint Crc(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var b in bytes) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x82f63b78); }
        crc = ~crc; return unchecked(((crc >> 15) | (crc << 17)) + 0xa282ead8);
    }
    private static byte[] Physical(byte[] body, byte type = 1)
    { var length = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)body.Length)); return Cat(U32(Crc(Cat([type], body))), length, [type], body); }
    private static byte[] Log(params byte[][] records)
    {
        using var stream = new MemoryStream();
        foreach (var record in records)
        {
            var at = 0; var first = true;
            do
            {
                var left = 32768 - (int)(stream.Length % 32768);
                if (left < 7) { stream.Write(new byte[left]); left = 32768; }
                var count = Math.Min(left - 7, record.Length - at); var last = at + count == record.Length;
                var type = first ? last ? (byte)1 : (byte)2 : last ? (byte)4 : (byte)3;
                stream.Write(Physical(record.AsSpan(at, count).ToArray(), type)); at += count; first = false;
            } while (at < record.Length);
        }
        return stream.ToArray();
    }
    private static byte[] Key(string origin = Origin, bool utf16 = false) => Cat(Encoding.UTF8.GetBytes("_" + origin + "\0"), [utf16 ? (byte)0 : (byte)1], utf16 ? Encoding.Unicode.GetBytes("userToken") : "userToken"u8.ToArray());
    private static byte[] Value(string text = "current-account", bool utf16 = false) => Cat([utf16 ? (byte)0 : (byte)1], utf16 ? Encoding.Unicode.GetBytes(text) : Encoding.Latin1.GetBytes(text));
    private static byte[] Batch(ulong seq, params (byte Type, byte[] Key, byte[] Value)[] entries)
        => Cat(U64(seq), U32((uint)entries.Length), entries.SelectMany(x => Cat([x.Type], String(x.Key), x.Type == 1 ? String(x.Value) : [])).ToArray());
    private void Write(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(directory, name), bytes);
    private void Setup(byte[]? records = null, ulong log = 3, ulong previous = 0, byte[]? manifestSuffix = null)
    {
        Write("CURRENT", "MANIFEST-000004\n"u8.ToArray());
        var manifest = Cat([1], String("leveldb.BytewiseComparator"u8.ToArray()), [2], Var(log), [9], Var(previous), [3], Var(1000), [4], Var(100), manifestSuffix ?? []);
        Write("MANIFEST-000004", Log(manifest));
        Write(log.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".log", Log(records ?? Batch(1, (1, "VERSION"u8.ToArray(), "1"u8.ToArray()), (1, Key(), Value()))));
    }
    private IReadOnlyDictionary<string, string> Read(Action? afterCapture = null) => ChromiumLocalStorageSnapshot.Read(directory, Origin, Keys, afterCapture, TestContext.Current.CancellationToken);
    private ChromiumStorageFailure Failure(Action? action = null) => Assert.Throws<ChromiumStorageException>(action ?? (() => Read())).Failure;
    [Fact] public void ReadsCurrentValueAndDoesNotWriteSourceFiles()
    {
        Setup(); var before = Directory.GetFiles(directory).ToDictionary(x => x, File.ReadAllBytes);
        Assert.Equal("current-account", Read()["userToken"]);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(directory).Order()); foreach (var item in before) Assert.Equal(item.Value, File.ReadAllBytes(item.Key));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void CurrentDeleteAndReloginUseSequence(bool lastIsDelete)
    {
        Setup(Batch(1, (1, "VERSION"u8.ToArray(), "1"u8.ToArray()), (1, Key(), Value("old")), (0, Key(), []), (1, Key(), Value("new"))));
        if (lastIsDelete) Write("000005.log", Log(Batch(5, (0, Key(), []))));
        var values = Read(); if (lastIsDelete) Assert.Empty(values); else Assert.Equal("new", values["userToken"]);
    }
    [Fact] public void SequenceWinsOverModificationTimes()
    {
        Setup(); Write("000005.log", Log(Batch(20, (1, Key(), Value("new"))))); Write("000006.log", Log(Batch(10, (1, Key(), Value("old")))));
        File.SetLastWriteTimeUtc(Path.Combine(directory, "000005.log"), DateTime.UtcNow.AddDays(-10)); Assert.Equal("new", Read()["userToken"]);
    }
    [Fact] public void IgnoresObsoleteLogAndIncludesPreviousAndNewerLogNumbers()
    {
        Setup(Batch(1, (1, "VERSION"u8.ToArray(), "1"u8.ToArray())), previous: 2);
        Write("000001.log", Log(Batch(90, (1, Key(), Value("obsolete"))))); Write("000002.log", Log(Batch(20, (1, Key(), Value("previous")))));
        Assert.Equal("previous", Read()["userToken"]); Write("000007.log", Log(Batch(30, (1, Key(), Value("rotated"))))); Assert.Equal("rotated", Read()["userToken"]);
    }
    [Theory] [InlineData("http://platform.deepseek.com")] [InlineData("https://platform.deepseek.com/^0https://other.example")] [InlineData("https://platform.deepseek.com:444")] [InlineData("https://other.deepseek.com")]
    public void DoesNotCollapseStorageSecurityBoundaries(string origin)
    { Setup(Batch(1, (1, "VERSION"u8.ToArray(), "1"u8.ToArray()), (1, Key(origin), Value()))); Assert.Empty(Read()); }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void SupportsOnlyExplicitStringEncodings(bool utf16)
    { Setup(Batch(1, (1, "VERSION"u8.ToArray(), "1"u8.ToArray()), (1, Key(), Value("café", utf16)))); Assert.Equal("café", Read()["userToken"]); }
    [Fact] public void RejectsLegacyAsciiKeyEncoding()
    { Setup(Batch(1, (1, "VERSION"u8.ToArray(), "1"u8.ToArray()), (1, Key(), Value("one")), (1, Key(utf16: true), Value("two")))); Assert.Equal(ChromiumStorageFailure.UnsupportedFormat, Failure()); }
    [Theory] [InlineData(2, 65)] [InlineData(0, 65)] [InlineData(0, 0, 216)]
    public void RejectsUnknownOrInvalidValueEncoding(int a, int b, int c = -1)
    { var value = new[] { a, b, c }.Where(x => x >= 0).Select(x => (byte)x).ToArray(); Setup(Batch(1, (1, "VERSION"u8.ToArray(), "1"u8.ToArray()), (1, Key(), value))); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Theory] [InlineData("2")] [InlineData("")] [InlineData("01")]
    public void RejectsUnknownChromiumSchema(string schema)
    { Setup(Batch(1, (1, "VERSION"u8.ToArray(), Encoding.ASCII.GetBytes(schema)))); Assert.Equal(ChromiumStorageFailure.UnsupportedFormat, Failure()); }
    [Fact] public void RejectsMissingVersionInsteadOfScanningRawTokens()
    { Setup(Batch(1, (1, Key(), Value()))); Assert.Equal(ChromiumStorageFailure.UnsupportedFormat, Failure()); }
    [Fact] public void ChecksLogAndManifestCrc()
    {
        Setup(); var path = Path.Combine(directory, "000003.log"); var bytes = File.ReadAllBytes(path); bytes[0] ^= 1; File.WriteAllBytes(path, bytes); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure());
        Setup(); path = Path.Combine(directory, "MANIFEST-000004"); bytes = File.ReadAllBytes(path); bytes[0] ^= 1; File.WriteAllBytes(path, bytes); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure());
    }
    [Theory] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void NeverDecodesUnfinishedOrMisorderedFragments(byte type)
    { Setup(); Write("000003.log", Physical(Batch(1, (1, Key(), Value())), type)); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Fact] public void CompleteMultiBlockRecordWorks()
    { Setup(Batch(1, (1, "VERSION"u8.ToArray(), "1"u8.ToArray()), (1, "unrelated"u8.ToArray(), new byte[70000]), (1, Key(), Value()))); Assert.Equal("current-account", Read()["userToken"]); }
    [Theory] [InlineData(0)] [InlineData(3)]
    public void WriteBatchCountMustMatch(uint count)
    { var batch = Batch(1, (1, "VERSION"u8.ToArray(), "1"u8.ToArray()), (1, Key(), Value())); U32(count).CopyTo(batch, 8); Setup(batch); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Fact] public void DuplicateSequenceContradictionFails()
    { Setup(); Write("000005.log", Log(Batch(2, (1, Key(), Value("other"))))); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Fact] public void SequenceOverflowFails()
    { Setup(Batch((1UL << 56) - 1, (1, "VERSION"u8.ToArray(), "1"u8.ToArray()), (1, Key(), Value()))); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Fact] public void OversizedLatestValueCannotRevealOlderCredential()
    { Setup(); Write("000005.log", Log(Batch(20, (1, Key(), new byte[65537])))); Assert.Equal(ChromiumStorageFailure.PolicyLimit, Failure()); }
    [Fact] public void CancelledReadDoesNotOpenEvenAMissingPath()
    { using var cancel = new CancellationTokenSource(); cancel.Cancel(); Assert.Throws<OperationCanceledException>(() => ChromiumLocalStorageSnapshot.Read("missing", Origin, Keys, cancel.Token)); }
    [Fact] public void CancellationAfterCaptureStopsBeforeReturning()
    { Setup(); using var cancel = new CancellationTokenSource(); Assert.Throws<OperationCanceledException>(() => ChromiumLocalStorageSnapshot.Read(directory, Origin, Keys, cancel.Cancel, cancel.Token)); }
    [Fact] public void ChangedDirectoryMembershipFailsClosed()
    { Setup(); Assert.Equal(ChromiumStorageFailure.BusyOrChanged, Failure(() => Read(() => Write("000005.log", Log(Batch(20, (1, Key(), Value("new")))))))); }
    [Fact] public void LockedStoreReturnsBusy()
    { Setup(); using var stream = new FileStream(Path.Combine(directory, "000003.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.Equal(ChromiumStorageFailure.BusyOrChanged, Failure()); }
    [Theory] [InlineData("../MANIFEST-000004\n")] [InlineData("MANIFEST-000004\r\n")] [InlineData("MANIFEST-000004")] [InlineData("MANIFEST-0\n")]
    public void CurrentOnlyAllowsSingleBoundedManifestName(string current)
    { Setup(); Write("CURRENT", Encoding.ASCII.GetBytes(current)); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Fact] public void UnknownManifestTagFailsExplicitly()
    { Setup(manifestSuffix: [100]); Assert.Equal(ChromiumStorageFailure.UnsupportedFormat, Failure()); }
    [Fact] public void DoesNotImportFromPartiallyCopiedStoreWithoutRecoveryLog()
    { Setup(); File.Delete(Path.Combine(directory, "000003.log")); Assert.Equal(ChromiumStorageFailure.Missing, Failure()); }
    [Fact] public void TooManyDirectoryEntriesAreBounded()
    { Setup(); for (var n = 0; n < 513; n++) Write("unrelated-" + n, []); Assert.Equal(ChromiumStorageFailure.PolicyLimit, Failure()); }
    [Fact] public void SparseOversizedFileIsRejectedBeforeAllocation()
    { Setup(); using (var file = File.OpenWrite(Path.Combine(directory, "000003.log"))) file.SetLength(32 * 1024 * 1024 + 1); Assert.Equal(ChromiumStorageFailure.PolicyLimit, Failure()); }

    private static byte[] Internal(byte[] key, ulong sequence, byte type = 1) => Cat(key, U64((sequence << 8) | type));
    private static byte[] DataBlock(params (byte[] Key, byte[] Value)[] entries)
    {
        using var content = new MemoryStream(); var offsets = new List<uint>();
        foreach (var (key, value) in entries) { offsets.Add((uint)content.Length); content.Write(Cat([0], Var((ulong)key.Length), Var((ulong)value.Length), key, value)); }
        if (offsets.Count == 0) offsets.Add(0);
        return Cat(content.ToArray(), offsets.SelectMany(U32).ToArray(), U32((uint)offsets.Count));
    }
    private static byte[] Snappy(byte[] bytes, int copyType = 0)
    {
        using var content = new MemoryStream(); content.Write(Var((ulong)bytes.Length)); var at = 0;
        while (at < bytes.Length)
        {
            var run = 0; if (copyType > 0 && at > 0) while (at + run < bytes.Length && bytes[at + run] == bytes[at - 1] && run < (copyType == 1 ? 11 : 64)) run++;
            if (run >= 4)
            { content.WriteByte((byte)(((copyType == 1 ? run - 4 : run - 1) << 2) | copyType)); content.WriteByte(1); if (copyType > 1) content.WriteByte(0); if (copyType == 3) { content.WriteByte(0); content.WriteByte(0); } at += run; }
            else { content.WriteByte(0); content.WriteByte(bytes[at++]); }
        }
        return content.ToArray();
    }
    private static byte[] WithTrailer(byte[] raw, bool compressed, int copy = 0)
    { var bytes = compressed ? Snappy(raw, copy) : raw; var type = compressed ? (byte)1 : (byte)0; return Cat(bytes, [type], U32(Crc(Cat(bytes, [type])))); }
    private byte[] SetupTable(bool compressed = false, int copy = 0, byte type = 1, bool deleted = false, bool alternativeExtension = false, byte[]? encodedBlock = null, Func<byte[], byte[]>? rawTransform = null)
    {
        var key = Internal(Key(), 20, type); var raw = DataBlock((key, type == 0 ? [] : Value(new string('A', 180)))); if (rawTransform is not null) raw = rawTransform(raw);
        var block = encodedBlock is null ? WithTrailer(raw, compressed, copy) : Cat(encodedBlock, [1], U32(Crc(Cat(encodedBlock, [1]))));
        var meta = WithTrailer(DataBlock(), false); var index = WithTrailer(DataBlock((key, Cat(Var(0), Var((ulong)block.Length - 5)))), false);
        var handles = Cat(Var((ulong)block.Length), Var((ulong)meta.Length - 5), Var((ulong)(block.Length + meta.Length)), Var((ulong)index.Length - 5));
        var table = Cat(block, meta, index, handles, new byte[40 - handles.Length], U64(0xdb4775248b80fb57));
        var added = Cat([7], Var(0), Var(8), Var((ulong)table.Length), String(key), String(key));
        Setup(manifestSuffix: deleted ? Cat(added, [6], Var(0), Var(8)) : added);
        Write(alternativeExtension ? "000008.sst" : "000008.ldb", table); return table;
    }
    [Theory] [InlineData(false, 0)] [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)]
    public void ReadsActiveRawAndAllSnappyCopyTypes(bool compressed, int copy)
    { SetupTable(compressed, copy); Assert.Equal(new string('A', 180), Read()["userToken"]); }
    [Fact] public void ReadsLegacySstExtension()
    { SetupTable(alternativeExtension: true); Assert.Equal(new string('A', 180), Read()["userToken"]); }
    [Fact] public void SstTombstoneSuppressesOlderLogValue()
    { SetupTable(type: 0); Assert.Empty(Read()); }
    [Fact] public void LaterLogValueWinsOverSstTombstone()
    { SetupTable(type: 0); Write("000009.log", Log(Batch(30, (1, Key(), Value("relogin"))))); Assert.Equal("relogin", Read()["userToken"]); }
    [Fact] public void OrphanTableIsNotScanned()
    { Setup(); Write("000008.ldb", "SYNTHETIC_OLD_TOKEN"u8.ToArray()); Assert.Equal("current-account", Read()["userToken"]); }
    [Fact] public void RemovedTableIsNotScanned()
    {
        SetupTable(); var manifest = File.ReadAllBytes(Path.Combine(directory, "MANIFEST-000004")); Write("MANIFEST-000004", Cat(manifest, Log(Cat([6], Var(0), Var(8)))));
        Write("000008.ldb", "corrupt obsolete file"u8.ToArray()); Assert.Equal("current-account", Read()["userToken"]);
    }
    [Fact] public void ActiveTableMustExist()
    { SetupTable(); File.Delete(Path.Combine(directory, "000008.ldb")); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void ChecksTableCrcAndFooter(int offset)
    { var bytes = SetupTable(); bytes[offset == -1 ? bytes.Length - 1 : offset] ^= 1; Write("000008.ldb", bytes); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Fact] public void CaseAndPortOriginArgumentsCannotBroadenSelection()
    { Setup(); Assert.Throws<ArgumentException>(() => ChromiumLocalStorageSnapshot.Read(directory, "http://platform.deepseek.com", Keys, TestContext.Current.CancellationToken)); Assert.Throws<ArgumentException>(() => ChromiumLocalStorageSnapshot.Read(directory, Origin + "/", Keys, TestContext.Current.CancellationToken)); }

    [Fact] public void SnappyExpansionIsBoundedBeforeAllocation()
    { SetupTable(encodedBlock: Var(8 * 1024 * 1024 + 1)); Assert.Equal(ChromiumStorageFailure.PolicyLimit, Failure()); }
    [Theory] [InlineData(10, 1, 0)] [InlineData(10, 2, 1, 0)] [InlineData(2, 0, 65)]
    public void SnappyBackreferencesAndDeclaredSizeAreValidated(int a, int b, int c, int d = -1)
    { var payload = new[] { a, b, c, d }.Where(x => x >= 0).Select(x => (byte)x).ToArray(); SetupTable(encodedBlock: payload); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Fact] public void RestartArrayCannotEscapeItsDataBlock()
    { SetupTable(rawTransform: raw => { U32(uint.MaxValue).CopyTo(raw, raw.Length - 4); return raw; }); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Fact] public void RestartOffsetsMustReferenceEntryBoundaries()
    { SetupTable(rawTransform: raw => { U32(1).CopyTo(raw, raw.Length - 8); return raw; }); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Fact] public void TableBlockHandlesCannotOverlap()
    { var table = SetupTable(); var footer = table.Length - 48; table[footer] = 0; Write("000008.ldb", table); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Fact] public void InvalidNewestEncodingDoesNotFallBackToAnOlderValue()
    { Setup(); Write("000005.log", Log(Batch(20, (1, Key(), new byte[] { 2, 1 })))); Assert.Equal(ChromiumStorageFailure.InvalidData, Failure()); }
    [Fact] public void ReferencedSnapshotContentCannotChangeDuringRead()
    { Setup(); Assert.Equal(ChromiumStorageFailure.BusyOrChanged, Failure(() => Read(() => Write("000003.log", Log(Batch(20, (1, "VERSION"u8.ToArray(), "1"u8.ToArray()), (1, Key(), Value("other")))))))); }
    [Fact] public void CurrentSwitchDuringSnapshotIsNotAccepted()
    { Setup(); Assert.Equal(ChromiumStorageFailure.BusyOrChanged, Failure(() => Read(() => Write("CURRENT", "MANIFEST-000009\n"u8.ToArray())))); }
    [Fact] public void SelectedStoreCannotBeAReparseLink()
    {
        Setup(); var link = Path.Combine(directory, "selected-link");
        try { Directory.CreateSymbolicLink(link, directory); }
        catch (UnauthorizedAccessException) { Assert.Skip("Creating synthetic symlinks is unavailable on this host."); }
        try { Assert.Equal(ChromiumStorageFailure.UnsupportedFormat, Failure(() => ChromiumLocalStorageSnapshot.Read(link, Origin, Keys, TestContext.Current.CancellationToken))); }
        finally { Directory.Delete(link); }
    }

    [Theory] [InlineData(true, true)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(false, false)]
    public void MixedEncodingDeletionOrReloginIsNeverMerged(bool olderUtf16, bool deleteLatest)
    {
        Setup(Batch(1, (1, "VERSION"u8.ToArray(), "1"u8.ToArray()), (1, Key(utf16: olderUtf16), Value("old"))));
        Write("000005.log", Log(Batch(20, (deleteLatest ? (byte)0 : (byte)1, Key(utf16: !olderUtf16), deleteLatest ? [] : Value("new")))));
        Assert.Equal(ChromiumStorageFailure.UnsupportedFormat, Failure());
    }
    [Fact] public void EvenAnOlderAliasTombstoneMakesTheReferencedSnapshotUnsupported()
    {
        Setup(Batch(1, (1, "VERSION"u8.ToArray(), "1"u8.ToArray()), (0, Key(utf16: true), []), (1, Key(), Value("new"))));
        Assert.Equal(ChromiumStorageFailure.UnsupportedFormat, Failure());
    }
    [Fact] public void ObsoleteUnreferencedAliasDoesNotContaminateCurrentSnapshot()
    { Setup(); Write("000001.log", Log(Batch(90, (0, Key(utf16: true), [])))); Assert.Equal("current-account", Read()["userToken"]); }
}
