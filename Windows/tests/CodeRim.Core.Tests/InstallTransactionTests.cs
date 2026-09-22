using System.Text.Json.Nodes;
using System.Text;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class InstallTransactionTests
{
    [Fact]
    public void InstallsThenUpdatesOnlyBinariesAndCleansOwnedWork()
    {
        using var fixture = new InstallFixture(); var sentinel = Path.Combine(fixture.Root, "settings-vault-history.fixture"); File.WriteAllText(sentinel, "synthetic protected data");
        var previous = fixture.InstallOld(); var next = InstallFixture.Manifest("2.2.1", files: InstallFixture.Bytes("new"));
        InstallTransaction.Apply(fixture.InstallRoot, fixture.Archive(InstallFixture.Bytes("new")), next, previous, token: TestContext.Current.CancellationToken);
        next.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken); Assert.Equal("synthetic protected data", File.ReadAllText(sentinel));
        AssertClean(fixture); InstallFileSystem.AssertPrivateDirectory(fixture.InstallRoot);
        Assert.Equal(InstallRecoveryResult.None, InstallTransaction.Recover(fixture.InstallRoot, TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData("unknown")][InlineData("modified")][InlineData("receipt-missing")][InlineData("unmanaged")][InlineData("same-version")][InlineData("architecture")]
    public void RefusesReplacementWithoutChangingUnownedOrModifiedFiles(string scenario)
    {
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld();
        if (scenario == "unknown") File.WriteAllText(Path.Combine(fixture.InstallRoot, "notes.txt"), "user-owned fixture");
        if (scenario == "modified") File.AppendAllText(Path.Combine(fixture.InstallRoot, "CodeRim.exe"), "modified");
        if (scenario == "receipt-missing") File.Delete(Path.Combine(fixture.InstallRoot, InstallPayloadManifest.ReceiptName));
        var before = InstallFixture.HashTree(fixture.InstallRoot);
        var next = InstallFixture.Manifest(scenario == "same-version" ? "2.2.0" : "2.2.1", scenario == "architecture" ? "x64" : "arm64");
        Assert.Throws<InvalidDataException>(() => InstallTransaction.Apply(fixture.InstallRoot, fixture.Archive(), next, scenario == "unmanaged" ? null : previous, token: TestContext.Current.CancellationToken));
        Assert.Equal(before.OrderBy(pair => pair.Key), InstallFixture.HashTree(fixture.InstallRoot).OrderBy(pair => pair.Key));
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)]
    [InlineData(3)][InlineData(4)]
    public void InjectedFailureRestoresOldUntilCommitThenKeepsVerifiedNew(int checkpoint)
    {
        var failAt = (InstallCheckpoint)checkpoint;
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld(); var next = InstallFixture.Manifest("2.2.1", files: InstallFixture.Bytes("new"));
        Assert.Throws<IOException>(() => InstallTransaction.Apply(fixture.InstallRoot, fixture.Archive(InstallFixture.Bytes("new")), next, previous,
            phase => { if (phase == failAt) throw new IOException("Synthetic failure."); }, TestContext.Current.CancellationToken));
        (failAt == InstallCheckpoint.Committed ? next : previous).VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken); AssertClean(fixture);
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)]
    [InlineData(3)][InlineData(4)]
    public void RecoversPersistedFileLayoutCapturedAtEveryTransition(int checkpoint)
    {
        var interruptedAt = (InstallCheckpoint)checkpoint;
        using var fixture = new InstallFixture(); using var saved = new InstallFixture(); var previous = fixture.InstallOld(); var next = InstallFixture.Manifest("2.2.1", files: InstallFixture.Bytes("new"));
        var work = InstallTransaction.WorkPath(fixture.InstallRoot);
        Assert.Throws<IOException>(() => InstallTransaction.Apply(fixture.InstallRoot, fixture.Archive(InstallFixture.Bytes("new")), next, previous, phase =>
        {
            if (phase != interruptedAt) return;
            if (Directory.Exists(fixture.InstallRoot)) InstallFixture.CopyTree(fixture.InstallRoot, Path.Combine(saved.Root, "install"));
            InstallFixture.CopyTree(work, Path.Combine(saved.Root, "work")); throw new IOException("Synthetic interruption snapshot.");
        }, TestContext.Current.CancellationToken));
        Directory.Delete(fixture.InstallRoot, true); Directory.Delete(work, true);
        if (Directory.Exists(Path.Combine(saved.Root, "install"))) InstallFixture.CopyTree(Path.Combine(saved.Root, "install"), fixture.InstallRoot);
        InstallFixture.CopyTree(Path.Combine(saved.Root, "work"), work);
        var result = InstallTransaction.Recover(fixture.InstallRoot, TestContext.Current.CancellationToken);
        Assert.Equal(interruptedAt == InstallCheckpoint.Committed ? InstallRecoveryResult.Completed : InstallRecoveryResult.RolledBack, result);
        (interruptedAt == InstallCheckpoint.Committed ? next : previous).VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken); AssertClean(fixture);
    }
    [Fact]
    public void CleanupCanResumeAfterSomeOwnedBackupFilesWereRemoved()
    {
        using var fixture = new InstallFixture(); using var saved = new InstallFixture(); var previous = fixture.InstallOld(); var next = InstallFixture.Manifest("2.2.1", files: InstallFixture.Bytes("new"));
        var work = InstallTransaction.WorkPath(fixture.InstallRoot);
        InstallTransaction.Apply(fixture.InstallRoot, fixture.Archive(InstallFixture.Bytes("new")), next, previous, phase =>
        {
            if (phase == InstallCheckpoint.Committed) InstallFixture.CopyTree(work, Path.Combine(saved.Root, "work"));
        }, TestContext.Current.CancellationToken);
        Directory.Delete(work, true); InstallFixture.CopyTree(Path.Combine(saved.Root, "work"), work);
        var journal = Path.Combine(work, "journal.json"); var value = JsonNode.Parse(File.ReadAllText(journal))!; value["State"] = "Cleaning"; File.WriteAllText(journal, value.ToJsonString());
        var backup = Path.Combine(Directory.GetDirectories(work, "operation-*").Single(), "backup"); File.Delete(Path.Combine(backup, "CodeRim.exe"));
        Assert.Equal(InstallRecoveryResult.Completed, InstallTransaction.Recover(fixture.InstallRoot, TestContext.Current.CancellationToken));
        next.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken); AssertClean(fixture);
    }
    [Fact]
    public void UnknownBackupIsPreservedAndDoesNotUndoCommittedNewBinaries()
    {
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld(); var next = InstallFixture.Manifest("2.2.1", files: InstallFixture.Bytes("new")); string? unknown = null;
        Assert.Throws<AggregateException>(() => InstallTransaction.Apply(fixture.InstallRoot, fixture.Archive(InstallFixture.Bytes("new")), next, previous, phase =>
        {
            if (phase != InstallCheckpoint.Committed) return;
            var backup = Path.Combine(Directory.GetDirectories(InstallTransaction.WorkPath(fixture.InstallRoot), "operation-*").Single(), "backup");
            unknown = Path.Combine(backup, "user-note.txt"); File.WriteAllText(unknown, "preserve");
        }, TestContext.Current.CancellationToken));
        Assert.Equal("preserve", File.ReadAllText(unknown!)); next.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(InstallTransaction.WorkPath(fixture.InstallRoot), "journal.json")));
    }
    [Fact]
    public void CancellationAfterPreparationRollsBackAndDoesNotModifyOldBytes()
    {
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld(); using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => InstallTransaction.Apply(fixture.InstallRoot, fixture.Archive(), InstallFixture.Manifest("2.2.1"), previous,
            phase => { if (phase == InstallCheckpoint.Prepared) cancel.Cancel(); }, cancel.Token));
        previous.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken); AssertClean(fixture);
    }
    [Fact]
    public async Task OnlyOneTransactionCanOwnTheSameInstallRoot()
    {
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld(); var archive = fixture.Archive(); var next = InstallFixture.Manifest("2.2.1");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var pending = Task.Run(() => InstallTransaction.Apply(fixture.InstallRoot, archive, next, previous, phase =>
        {
            if (phase == InstallCheckpoint.Prepared) { entered.Set(); release.Wait(TestContext.Current.CancellationToken); }
        }, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Throws<IOException>(() => InstallTransaction.Apply(fixture.InstallRoot, archive, next, previous, token: TestContext.Current.CancellationToken));
        }
        finally { release.Set(); }
        await pending; next.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken); AssertClean(fixture);
    }
    [Fact]
    public void AlreadyCanceledInstallAndRecoveryDoNotCreateWorkFiles()
    {
        using var fixture = new InstallFixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var archive = fixture.Archive();
        Assert.Throws<OperationCanceledException>(() => InstallTransaction.Apply(fixture.InstallRoot, archive, InstallFixture.Manifest(), token: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => InstallTransaction.Recover(fixture.InstallRoot, cancellation.Token));
        Assert.False(Directory.Exists(InstallTransaction.WorkPath(fixture.InstallRoot))); Assert.False(Directory.Exists(fixture.InstallRoot));
    }
    [Fact]
    public void CorruptArchiveCleansOnlyItsPartialStageAndRetainsOldInstallation()
    {
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld();
        Assert.Throws<InvalidDataException>(() => InstallTransaction.Apply(fixture.InstallRoot, fixture.Archive(scenario: "hash"), InstallFixture.Manifest("2.2.1"), previous, token: TestContext.Current.CancellationToken));
        previous.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken); AssertClean(fixture);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void FailedFirstInstallDoesNotLeaveAnInstallation(int checkpoint)
    {
        using var fixture = new InstallFixture();
        Assert.Throws<IOException>(() => InstallTransaction.Apply(fixture.InstallRoot, fixture.Archive(), InstallFixture.Manifest(), checkpoint: phase =>
        { if ((int)phase == checkpoint) throw new IOException("Synthetic first-install failure."); }, token: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(fixture.InstallRoot)); AssertClean(fixture);
    }
    [Fact]
    public void EscapingJournalIdentityIsRejectedBeforeAnyPayloadMutation()
    {
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld(); var before = InstallFixture.HashTree(fixture.InstallRoot);
        var journal = new JsonObject { ["Id"] = "../outside", ["State"] = "Committed", ["NewManifest"] = InstallFixture.Manifest("2.2.1").ToJson(), ["OldManifest"] = previous.ToJson() };
        File.WriteAllText(Path.Combine(InstallTransaction.WorkPath(fixture.InstallRoot), "journal.json"), journal.ToJsonString());
        Assert.Throws<InvalidDataException>(() => InstallTransaction.Recover(fixture.InstallRoot, TestContext.Current.CancellationToken));
        Assert.Equal(before.OrderBy(pair => pair.Key), InstallFixture.HashTree(fixture.InstallRoot).OrderBy(pair => pair.Key));
    }
    [Fact]
    public void UnknownStagedFilesAreNotRemovedDuringFailureRecovery()
    {
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld(); string? unknown = null;
        Assert.Throws<AggregateException>(() => InstallTransaction.Apply(fixture.InstallRoot, fixture.Archive(), InstallFixture.Manifest("2.2.1"), previous, phase =>
        {
            if (phase != InstallCheckpoint.Prepared) return;
            var stage = Path.Combine(Directory.GetDirectories(InstallTransaction.WorkPath(fixture.InstallRoot), "operation-*").Single(), "stage");
            unknown = Path.Combine(stage, "unknown-user-file"); File.WriteAllText(unknown, "preserve unknown"); throw new IOException("Synthetic failure.");
        }, TestContext.Current.CancellationToken));
        Assert.Equal("preserve unknown", File.ReadAllText(unknown!)); previous.VerifyTree(fixture.InstallRoot, TestContext.Current.CancellationToken);
    }
    [Fact]
    public void ReparseAncestorCannotRedirectAnInstallation()
    {
        using var fixture = new InstallFixture(); var target = Path.Combine(fixture.Root, "target"); InstallFileSystem.CreatePrivateDirectory(target);
        var link = Path.Combine(fixture.Root, "link");
        // A directory junction exercises the same ancestor reparse boundary without changing Windows privileges or Developer Mode.
        if (OperatingSystem.IsWindows()) CreateJunction(link, target);
        else
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch (UnauthorizedAccessException) { Assert.Skip("Creating a synthetic symlink is not permitted on this host."); }
        }
        try
        {
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.Throws<IOException>(() => InstallTransaction.Apply(Path.Combine(link, "App"), fixture.Archive(), InstallFixture.Manifest(), token: TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFileSystemEntries(target));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsAlternateStreamsArePreservedInsteadOfDeleted(bool directoryStream)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows alternate stream preservation requires native Windows.");
        using var fixture = new InstallFixture(); var previous = fixture.InstallOld();
        var stream = (directoryStream ? fixture.InstallRoot : Path.Combine(fixture.InstallRoot, "CodeRim.exe")) + ":user-note";
        File.WriteAllText(stream, "synthetic user-owned alternate stream");
        Assert.Throws<InvalidDataException>(() => InstallTransaction.Apply(fixture.InstallRoot, fixture.Archive(), InstallFixture.Manifest("2.2.1"), previous, token: TestContext.Current.CancellationToken));
        Assert.Equal("synthetic user-owned alternate stream", File.ReadAllText(stream));
    }
    private static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        using var handle = CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var data = new byte[16 + substitute.Length + 2 + display.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), checked((ushort)display.Length));
        substitute.CopyTo(data, 16); display.CopyTo(data, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int length, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
    private static void AssertClean(InstallFixture fixture)
    {
        var work = InstallTransaction.WorkPath(fixture.InstallRoot);
        Assert.False(File.Exists(Path.Combine(work, "journal.json"))); Assert.Empty(Directory.GetDirectories(work, "operation-*"));
        Assert.True(File.Exists(Path.Combine(work, "commit.lock"))); Assert.True(File.Exists(Path.Combine(work, "owner")));
    }
}
