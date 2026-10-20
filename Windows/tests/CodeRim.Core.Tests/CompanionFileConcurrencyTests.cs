using System.Diagnostics;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class CompanionFileConcurrencyTests : IDisposable
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "coderim-snapshot-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(directory, "snapshot.json");
    private static CompanionSnapshot Snapshot(string text)
    {
        var now = DateTimeOffset.Now;
        return new(1, now, [new("codex", "Codex", true, null, new("codex", ReadingState.Ready, [], now, Message: text))]);
    }
    private static void AssertNativeRenameFailure(Exception error)
        => Assert.True(error switch
        {
            UnauthorizedAccessException => (error.HResult & 0xffff) == 5,
            IOException => (error.HResult & 0xffff) is 32 or 33,
            _ => false
        }, "The native rename exception type and error code must remain paired.");

    [Theory(Skip = "Requires Windows rename sharing semantics", SkipUnless = nameof(IsWindows))]
    [InlineData(false)] [InlineData(true)]
    public void TemporaryRenameLockIsRetriedWithoutPublishingPartialData(bool sourceLocked)
    {
        CompanionFile.Write(Snapshot("before"), PathName);
        var temporary = Path.Combine(directory, "staging.tmp");
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(Snapshot("after"), CompanionFile.JsonOptions));
        using var reader = new FileStream(sourceLocked ? temporary : PathName, FileMode.Open, FileAccess.Read, FileShare.Read);
        var conflicts = 0;
        CompanionFile.Publish(temporary, PathName, error =>
        {
            AssertNativeRenameFailure(error);
            Assert.Equal("before", Assert.Single(CompanionFile.Read(PathName).Providers).Limits.Message);
            conflicts++;
            reader.Dispose();
        });
        Assert.True(conflicts >= 1, "The real blocked rename must be observed before releasing its lock.");
        Assert.Equal("after", Assert.Single(CompanionFile.Read(PathName).Providers).Limits.Message);
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void NonSharingFailureIsNotRetried()
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "staging.tmp"); File.WriteAllText(source, "complete");
        var observed = 0;
        Assert.Throws<DirectoryNotFoundException>(() => CompanionFile.Publish(source, Path.Combine(directory, "missing", "snapshot.json"), _ => observed++));
        Assert.Equal(0, observed);
    }

    [Fact]
    public void MissingStagingFileIsNotRetried()
    {
        CompanionFile.Write(Snapshot("before"), PathName);
        var observed = 0;
        Assert.Throws<FileNotFoundException>(() => CompanionFile.Publish(Path.Combine(directory, "missing.tmp"), PathName, _ => observed++));
        Assert.Equal(0, observed);
        Assert.Equal("before", Assert.Single(CompanionFile.Read(PathName).Providers).Limits.Message);
    }

    [Theory]
    [InlineData("utf8")] [InlineData("utf16")] [InlineData("utf16be")]
    public void ExistingBomEncodedSnapshotsRemainReadable(string format)
    {
        Directory.CreateDirectory(directory);
        var encoding = format switch { "utf8" => new System.Text.UTF8Encoding(true), "utf16" => System.Text.Encoding.Unicode, _ => System.Text.Encoding.BigEndianUnicode };
        File.WriteAllText(PathName, System.Text.Json.JsonSerializer.Serialize(Snapshot("한국어 😀"), CompanionFile.JsonOptions), encoding);
        Assert.Equal("한국어 😀", Assert.Single(CompanionFile.Read(PathName).Providers).Limits.Message);
    }

    [Theory(Skip = "Requires Windows rename sharing semantics", SkipUnless = nameof(IsWindows))]
    [InlineData(false)] [InlineData(true)]
    public void PersistentReaderLockIsBoundedAndPreservesPreviousSnapshot(bool sourceLocked)
    {
        CompanionFile.Write(Snapshot("before"), PathName);
        // Write owns and cleans its randomly named staging file. For a source
        // lock, publish an explicitly held staging file and retain it on failure.
        var temporary = Path.Combine(directory, "staging.tmp");
        if (sourceLocked) File.WriteAllText(temporary, "complete staging bytes");
        using var reader = new FileStream(sourceLocked ? temporary : PathName, FileMode.Open, FileAccess.Read, FileShare.Read);
        var conflicts = 0;
        var timer = Stopwatch.StartNew();
        var error = Record.Exception(() =>
        {
            if (sourceLocked) CompanionFile.Publish(temporary, PathName, _ => conflicts++);
            else CompanionFile.Write(Snapshot("after"), PathName);
        });
        Assert.NotNull(error);
        AssertNativeRenameFailure(error);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), "Publication must have a finite retry bound.");
        Assert.Equal("before", Assert.Single(CompanionFile.Read(PathName).Providers).Limits.Message);
        if (sourceLocked)
        {
            Assert.Equal(7, conflicts);
            Assert.Equal("complete staging bytes", File.ReadAllText(temporary));
        }
        else Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact(Skip = "Windows read-only replacement permissions", SkipUnless = nameof(IsWindows))]
    public void PermanentReadOnlyDestinationRemainsProtectedAfterBoundedFailure()
    {
        CompanionFile.Write(Snapshot("before"), PathName);
        File.SetAttributes(PathName, File.GetAttributes(PathName) | FileAttributes.ReadOnly);
        try
        {
            var timer = Stopwatch.StartNew();
            var error = Assert.Throws<UnauthorizedAccessException>(() => CompanionFile.Write(Snapshot("after"), PathName));
            Assert.Equal(5, error.HResult & 0xffff);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
            Assert.Equal("before", Assert.Single(CompanionFile.Read(PathName).Providers).Limits.Message);
            Assert.True(File.GetAttributes(PathName).HasFlag(FileAttributes.ReadOnly));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally { File.SetAttributes(PathName, File.GetAttributes(PathName) & ~FileAttributes.ReadOnly); }
    }

    [Fact]
    public async Task ConcurrentReadersObserveOnlyCompletePublishedSnapshots()
    {
        var first = new string('a', 256 * 1024); var second = new string('b', first.Length);
        CompanionFile.Write(Snapshot(first), PathName);
        using var start = new ManualResetEventSlim();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var workers = Enumerable.Range(0, 4).Select(index => Task.Run(() =>
        {
            start.Wait(cancellation.Token);
            for (var iteration = 0; iteration < 20; iteration++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (index == 0) CompanionFile.Write(Snapshot(iteration % 2 == 0 ? first : second), PathName);
                else
                {
                    var observed = Assert.Single(CompanionFile.Read(PathName).Providers).Limits.Message;
                    Assert.True(observed == first || observed == second, "A reader observed an incomplete or mixed snapshot.");
                }
            }
        }, cancellation.Token)).ToArray();
        var all = Task.WhenAll(workers);
        start.Set();
        try { await all.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken); }
        finally
        {
            cancellation.Cancel();
            if (!all.IsCompleted) { try { await all; } catch (OperationCanceledException) { } }
        }
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
