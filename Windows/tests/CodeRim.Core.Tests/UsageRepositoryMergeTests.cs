using CodeRim.Core.Domain;
using CodeRim.Core.Services;


namespace CodeRim.Core.Tests;

/// Two valid copies of one event merge into a valid row: one copy's split of its input is kept whole.
public sealed class UsageRepositoryMergeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coderim-merge-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);

    public UsageRepositoryMergeTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private static UsageEvent Copy(TokenUsage usage) => new("same-event", Now, usage, "gpt-5.6");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiesThatSplitTheSameInputDifferentlyStayAValidSplit(bool reversed)
    {
        var reads = Copy(new(20, 20, 5, 0));   // all 20 input tokens were cache reads
        var writes = Copy(new(20, 0, 5, 20));  // all 20 were cache writes
        var repository = new UsageRepository(Path.Combine(root, "usage.sqlite"));
        repository.Merge("codex", [reversed ? writes : reads]);
        repository.Merge("codex", [reversed ? reads : writes]);
        var stored = Assert.Single(repository.Read("codex")).Usage;
        Assert.True(stored.IsValid, stored.ToString());
        Assert.Equal(new TokenUsage(20, 20, 5, 0), stored); // the same answer whichever copy came first
        Assert.Equal(0, repository.LastWriteRepairedEvents);
    }

    [Fact]
    public void ALaterRevisionWithMoreInputReplacesTheWholeSplit()
    {
        var repository = new UsageRepository(Path.Combine(root, "usage.sqlite"));
        repository.Merge("codex", [Copy(new(100, 80, 10))]);
        repository.Merge("codex", [Copy(new(120, 50, 12))]);
        var stored = Assert.Single(repository.Read("codex")).Usage;
        Assert.Equal(new TokenUsage(120, 50, 12), stored with { CacheWriteInputTokens = stored.CacheWriteInputTokens });
        Assert.True(stored.IsValid);
    }
}
