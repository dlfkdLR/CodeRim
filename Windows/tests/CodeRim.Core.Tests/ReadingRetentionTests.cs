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
