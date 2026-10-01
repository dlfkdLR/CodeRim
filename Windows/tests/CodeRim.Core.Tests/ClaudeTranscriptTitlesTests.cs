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
}
