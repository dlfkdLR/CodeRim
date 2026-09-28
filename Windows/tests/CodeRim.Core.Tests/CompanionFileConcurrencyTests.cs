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
            Assert.Equal(32, error.HResult & 0xffff);
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

    [Theory]
    [InlineData("utf8")] [InlineData("utf16")] [InlineData("utf16be")]
    public void ExistingBomEncodedSnapshotsRemainReadable(string format)
    {
        Directory.CreateDirectory(directory);
        var encoding = format switch { "utf8" => new System.Text.UTF8Encoding(true), "utf16" => System.Text.Encoding.Unicode, _ => System.Text.Encoding.BigEndianUnicode };
        File.WriteAllText(PathName, System.Text.Json.JsonSerializer.Serialize(Snapshot("한국어 😀"), CompanionFile.JsonOptions), encoding);
        Assert.Equal("한국어 😀", Assert.Single(CompanionFile.Read(PathName).Providers).Limits.Message);
    }

    [Fact(Skip = "Requires Windows rename sharing semantics", SkipUnless = nameof(IsWindows))]
    public void PersistentReaderLockIsBoundedAndPreservesPreviousSnapshot()
    {
        CompanionFile.Write(Snapshot("before"), PathName);
        using var reader = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read);
        var timer = Stopwatch.StartNew();
        var error = Assert.Throws<IOException>(() => CompanionFile.Write(Snapshot("after"), PathName));
        Assert.Equal(32, error.HResult & 0xffff);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), "Publication must have a finite retry bound.");
        Assert.Equal("before", Assert.Single(CompanionFile.Read(PathName).Providers).Limits.Message);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
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
