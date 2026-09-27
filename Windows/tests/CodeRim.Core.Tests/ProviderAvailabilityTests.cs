using CodeRim.Core.Domain;

namespace CodeRim.Core.Tests;

public sealed class ProviderAvailabilityTests
{
    [Theory]
    [InlineData("copilot")] [InlineData("cursor")] [InlineData("grok")]
    [InlineData("commandcode")] [InlineData("opencode")] [InlineData("glm")]
    [InlineData("ollama")] [InlineData("gemini")] [InlineData("ollama-local")]
    public void ReferenceLifecycleDistinguishesStartupAccountSuccessAndFailure(string id)
    {
        Assert.False(ProviderAvailability.ShowsInNotch(id, null, false));
        Assert.True(ProviderAvailability.ShowsInNotch(id, null, true));
        foreach (var phase in new[] { ReadingState.Loading, ReadingState.Stale })
        {
            var startup = new ProviderReading(id, phase, []);
            Assert.False(ProviderAvailability.ShowsInNotch(id, startup, false));
            Assert.True(ProviderAvailability.ShowsInNotch(id, startup, true));
        }
        foreach (var phase in new[] { ReadingState.Ready, ReadingState.Partial })
            Assert.True(ProviderAvailability.ShowsInNotch(id, new(id, phase, []), false));
        foreach (var phase in new[] { ReadingState.Error, ReadingState.Unavailable, ReadingState.NeedsAuth, ReadingState.Unsupported, ReadingState.Disabled })
        {
            var failed = new ProviderReading(id, phase, [], Account: new("detected@example.invalid", "Local"));
            Assert.False(ProviderAvailability.ShowsInNotch(id, failed, true));
        }
    }
    [Theory]
    [InlineData("codex")] [InlineData("claude")] [InlineData("poe")] [InlineData("openrouter")]
    public void OtherReferenceProvidersRemainVisibleWithoutReadings(string id)
    {
        Assert.True(ProviderAvailability.ShowsInNotch(id, null, false));
        Assert.True(ProviderAvailability.ShowsInNotch(id, new(id, ReadingState.Error, []), false));
    }
}
