using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ClaudeTranscriptTitlesTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "coderim-title-" + Guid.NewGuid().ToString("N") + ".jsonl");
    public void Dispose() => File.Delete(path);

    [Fact]
    public void FollowsAppendedTitlesAndPrefersACustomOne()
    {
        var titles = new ClaudeTranscriptTitles();
        File.WriteAllText(path, "{\"type\":\"user\"}\n");
        Assert.Null(titles.Title(path));

        File.AppendAllText(path, "{\"type\":\"ai-title\",\"aiTitle\":\"전체 코드 리뷰\",\"sessionId\":\"a\"}\n");
        Assert.Equal("전체 코드 리뷰", titles.Title(path));

        // A half-written line is left for the next read.
        File.AppendAllText(path, "{\"type\":\"custom-title\",\"customTitle\":\"Renamed\"");
        Assert.Equal("전체 코드 리뷰", titles.Title(path));
        File.AppendAllText(path, ",\"sessionId\":\"a\"}\n{\"type\":\"ai-title\",\"aiTitle\":\"Later idea\"}\n");
        Assert.Equal("Renamed", titles.Title(path));
    }

    [Fact]
    public void StartsOverWhenTheTranscriptIsRewritten()
    {
        var titles = new ClaudeTranscriptTitles();
        File.WriteAllText(path, "{\"type\":\"ai-title\",\"aiTitle\":\"A rather long first title\"}\n");
        Assert.Equal("A rather long first title", titles.Title(path));
        File.WriteAllText(path, "{\"type\":\"ai-title\",\"aiTitle\":\"B\"}\n");
        Assert.Equal("B", titles.Title(path));
    }

    [Fact]
    public void BoundsAllocationAndDoesNotRereadAnOversizedUnfinishedTail()
    {
        var titles = new ClaudeTranscriptTitles();
        var padding = new string('x', 8 * 1024 * 1024);
        File.WriteAllText(path, padding + "\n{\"type\":\"ai-title\",\"aiTitle\":\"Near EOF\"}\n");
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal("Near EOF", titles.Title(path));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4 * 1024 * 1024);

        File.AppendAllText(path, padding);
        Assert.Equal("Near EOF", titles.Title(path));
        AssertNoRetainedLineBuffers(titles);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 3; i++) Assert.Equal("Near EOF", titles.Title(path));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 512 * 1024);
        File.AppendAllText(path, "\n{\"type\":\"custom-title\",\"customTitle\":\"After tail\"}\n");
        Assert.Equal("After tail", titles.Title(path));
    }

    [Fact]
    public void HandlesATitleSplitAcrossChunksAndRestartsAfterAnUnfinishedTailIsTruncated()
    {
        var titles = new ClaudeTranscriptTitles();
        File.WriteAllText(path, new string(' ', 64 * 1024 - 20) + "\n{\"type\":\"ai-title\",\"aiTitle\":\"Split title\"}\n");
        Assert.Equal("Split title", titles.Title(path));
        File.AppendAllText(path, new string('x', 2 * 1024 * 1024));
        Assert.Equal("Split title", titles.Title(path));
        File.WriteAllText(path, "{\"type\":123,\"customTitle\":\"ignore\"}\n{\"type\":\"ai-title\",\"aiTitle\":\"Rewritten\"}\n");
        Assert.Equal("Rewritten", titles.Title(path));
    }

    [Fact]
    public void CompletedLargeTranscriptsReleaseBuffersAndOldCursorsAreEvicted()
    {
        var directory = path + ".sessions";
        Directory.CreateDirectory(directory);
        try
        {
            var titles = new ClaudeTranscriptTitles();
            var padding = new string('x', 900 * 1024) + "\n";
            for (var i = 0; i < 80; i++)
            {
                var transcript = Path.Combine(directory, i + ".jsonl");
                File.WriteAllText(transcript, padding + $"{{\"type\":\"ai-title\",\"aiTitle\":\"Title {i}\"}}\n");
                Assert.Equal($"Title {i}", titles.Title(transcript));
            }
            var scans = CachedScans(titles);
            Assert.InRange(scans.Count, 1, 64);
            Assert.False(scans.Contains(Path.Combine(directory, "0.jsonl")));
            AssertNoRetainedLineBuffers(titles);
            Assert.Equal("Title 0", titles.Title(Path.Combine(directory, "0.jsonl")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static System.Collections.IDictionary CachedScans(ClaudeTranscriptTitles titles) =>
        (System.Collections.IDictionary)typeof(ClaudeTranscriptTitles).GetField("scans",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(titles)!;

    private static void AssertNoRetainedLineBuffers(ClaudeTranscriptTitles titles)
    {
        foreach (var scan in CachedScans(titles).Values)
            Assert.Empty((byte[])scan!.GetType().GetField("Carry")!.GetValue(scan)!);
    }
}
