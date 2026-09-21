using System.Text.Json;
using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;
public sealed class ActivityReaderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "coderim-activity-" + Guid.NewGuid().ToString("N"));
    public ActivityReaderTests() => Directory.CreateDirectory(directory);
    private string FilePath(string name = "session") => Path.Combine(directory, name + ".jsonl");
    private static string Codex(string type, DateTimeOffset at) => JsonSerializer.Serialize(new { type = "event_msg", timestamp = at, payload = new { type } }) + "\n";
    private static string Claude(string type, DateTimeOffset at) => JsonSerializer.Serialize(new { type, timestamp = at, message = new { stop_reason = "end_turn" } }) + "\n";
    [Fact]
    public void LongTranscriptKeepsStartBeyondEightMegabytesWithoutMaterializingHugeLines()
    {
        var now = DateTimeOffset.UtcNow; var path = FilePath();
        File.WriteAllText(path, Codex("task_started", now.AddHours(-8)) + new string('x', ActivityReader.MaximumTailBytes + 5000) + "\n");
        Assert.Equal("busy", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
        Assert.Equal("busy", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
        File.AppendAllText(path, Codex("task_complete", now));
        Assert.Equal("idle", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
    }
    [Fact]
    public void IncompleteTailBecomesVisibleOnlyAfterNewlineAndTruncationInvalidatesCache()
    {
        var now = DateTimeOffset.UtcNow; var path = FilePath();
        File.WriteAllText(path, Codex("task_started", now));
        Assert.Equal("busy", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
        var completion = Codex("task_complete", now);
        File.AppendAllText(path, completion[..^1]);
        Assert.Equal("busy", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
        File.AppendAllText(path, "\n");
        Assert.Equal("idle", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
        File.WriteAllText(path, Codex("task_started", now));
        Assert.Equal("busy", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
        File.WriteAllText(path, "");
        Assert.Null(ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken));
    }
    [Fact]
    public void BoundaryRewriteWithSameLengthAndModifiedTimeCannotKeepOldActivity()
    {
        var now = DateTimeOffset.UtcNow; var path = FilePath();
        var padding = new string(' ', 300) + "\n";
        File.WriteAllText(path, padding + Codex("task_started", now) + padding);
        Assert.Equal("busy", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, padding + Codex("turn_aborted", now) + padding);
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal("idle", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
    }
    [Fact]
    public void FileFreshnessBoundsOrphanedCodexStartsAndCompletionGraceRemainsBounded()
    {
        var now = DateTimeOffset.UtcNow; var path = FilePath();
        File.WriteAllText(path, Codex("task_started", now.AddHours(-8)));
        Assert.Equal("busy", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
        File.SetLastWriteTimeUtc(path, now.AddHours(-7).UtcDateTime);
        Assert.Null(ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken));
        File.WriteAllText(path, Codex("task_complete", now.AddSeconds(-91)));
        Assert.Null(ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken));
        File.WriteAllText(path, Codex("task_started", now.AddMinutes(2)));
        Assert.Null(ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken));
    }
    [Fact]
    public void LiveClaudeRegistryCanResolveOldCompletionAndNextTurn()
    {
        var now = DateTimeOffset.UtcNow; var path = FilePath();
        File.WriteAllText(path, Claude("assistant", now.AddDays(-1)));
        Assert.Equal("idle", ActivityReader.ReadClaude(path, now, TestContext.Current.CancellationToken)?.State);
        File.AppendAllText(path, Claude("user", now));
        Assert.Equal("busy", ActivityReader.ReadClaude(path, now, TestContext.Current.CancellationToken)?.State);
    }
    [Fact]
    public void BackwardsChunkBoundariesAndMalformedOversizedRecordsDoNotHideCompletion()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var offset in new[] { 65535, 65536, 65537, 131072 })
        {
            var path = FilePath(offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllText(path, new string('x', offset) + "\n" + Codex("task_complete", now) + "{}\npartial");
            Assert.Equal("idle", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RewrittenMiddleCompletionInvalidatesWarmAppendCursor(bool append)
    {
        var now = DateTimeOffset.UtcNow; var path = FilePath();
        var start = Codex("task_started", now);
        var completion = Codex("task_complete", now);
        var end = new string(' ', 1000) + "\n";
        File.WriteAllText(path, start + new string(' ', completion.Length - 1) + "\n" + end);
        Assert.Equal("busy", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, start + completion + end + (append ? "{}\n" : ""));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal("idle", ActivityReader.ReadCodex(path, now, TestContext.Current.CancellationToken)?.State);
    }
    [Fact]
    public void CancelledReadStopsBeforeScanningTranscript()
    {
        var path = FilePath(); File.WriteAllText(path, "{}\n");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ActivityReader.ReadCodex(path, DateTimeOffset.UtcNow, cancellation.Token));
    }
    public void Dispose() => Directory.Delete(directory, true);
}
