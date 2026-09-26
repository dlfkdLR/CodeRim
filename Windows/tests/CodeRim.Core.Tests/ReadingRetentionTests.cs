using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ReadingRetentionTests
{
    private static readonly ProviderReading Previous = new("codex", ReadingState.Ready, [new("weekly", "Weekly", 42)], DateTimeOffset.Parse("2026-09-18T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    [Theory]
    [InlineData(ReadingState.Error)]
    [InlineData(ReadingState.Unavailable)]
    public void TemporaryFailureRetainsLastReadingAndTimestamp(ReadingState state)
    {
        var result = ReadingRetention.Merge(new("codex", state, [], Message: "Try later"), Previous);
        Assert.Equal(ReadingState.Stale, result.State); Assert.Equal(42, result.Headline!.UsedPercent);
        Assert.Equal(Previous.UpdatedAt, result.UpdatedAt); Assert.Equal("Try later", result.Message);
    }
    [Theory]
    [InlineData(ReadingState.NeedsAuth)]
    [InlineData(ReadingState.Disabled)]
    [InlineData(ReadingState.Unsupported)]
    public void InvalidAccountDoesNotRetainAnotherReading(ReadingState state)
    {
        Assert.Empty(ReadingRetention.Merge(new("codex", state, []), Previous).Windows);
    }
    [Fact]
    public void DifferentProviderIsNeverRetained() => Assert.Empty(ReadingRetention.Merge(new("claude", ReadingState.Error, []), Previous).Windows);
    [Theory]
    [InlineData("copilot")] [InlineData("cursor")] [InlineData("grok")]
    [InlineData("commandcode")] [InlineData("opencode")] [InlineData("glm")]
    [InlineData("ollama")] [InlineData("gemini")] [InlineData("ollama-local")]
    public void BorrowedReferenceProvidersDiscardFailedQuotaIndependentlyOfDetectedAccount(string id)
    {
        var previous = Previous with { Id = id, Account = new("previous@example.invalid", "Local") };
        foreach (var state in new[] { ReadingState.Error, ReadingState.Unavailable, ReadingState.NeedsAuth, ReadingState.Unsupported, ReadingState.Disabled })
        {
            var incoming = new ProviderReading(id, state, [], Message: "Current failure", Account: new("current@example.invalid", "Local"));
            var result = ReadingRetention.Merge(incoming, previous);
            Assert.Same(incoming, result); Assert.Empty(result.Windows); Assert.Null(result.UpdatedAt);
            Assert.Equal("current@example.invalid", result.Account!.Label);
        }
    }
    [Theory]
    [InlineData("codex")] [InlineData("claude")] [InlineData("poe")] [InlineData("openrouter")]
    public void UnrelatedProvidersKeepTheirTransientFailureHistory(string id)
    {
        var previous = Previous with { Id = id };
        var result = ReadingRetention.Merge(new(id, ReadingState.Error, []), previous);
        Assert.Equal(ReadingState.Stale, result.State); Assert.Equal(previous.Windows, result.Windows);
        Assert.Equal(previous.UpdatedAt, result.UpdatedAt);
    }
    [Theory]
    [InlineData(NotchEdge.Left)]
    [InlineData(NotchEdge.Right)]
    [InlineData(NotchEdge.Top)]
    [InlineData(NotchEdge.Bottom)]
    public void AllScaleAndProviderCountsFitDesktop(NotchEdge edge)
    {
        foreach (var available in new[] { 600d, 768d, 1080d })
        foreach (var scale in new[] { 0.8, 1d, 1.25 })
        foreach (var count in new[] { 0, 1, 4, 70 })
        {
            var fit = NotchMetrics.Fit(edge, count, scale, available);
            Assert.True(fit.Length + NotchMetrics.ControlExtent * scale <= available + 0.01);
            Assert.True(fit.Capacity >= 1);
        }
    }
}
