using System.Text.Json;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class AccountPlanDisplayTests
{
    private static string Profile(string email = "a@example.invalid", string org = "workspace-a", string? userTier = "default_claude_max_5x", string? orgTier = "default_claude_max_20x")
        => JsonSerializer.Serialize(new { oauthAccount = new { emailAddress = email, organizationUuid = org, userRateLimitTier = userTier, organizationRateLimitTier = orgTier } });
    [Fact]
    public void ClaudeMaxShowsOnlyTheMatchingAccountsTier()
    {
        Assert.Equal("Max 5x", AccountPlanDisplay.Name("claude", "max", Profile(), "a@example.invalid", "workspace-a"));
        Assert.Equal("Max 20x", AccountPlanDisplay.Name("claude", "max", Profile(userTier: null), "a@example.invalid", "workspace-a"));
        Assert.Equal("Max", AccountPlanDisplay.Name("claude", "max", Profile(), "b@example.invalid", "workspace-a"));
        Assert.Equal("Max", AccountPlanDisplay.Name("claude", "max", Profile(), "a@example.invalid", "workspace-b"));
        Assert.Equal("Max", AccountPlanDisplay.Name("claude", "max", Profile(userTier: "other"), "a@example.invalid", "workspace-a"));
        Assert.Equal("Pro", AccountPlanDisplay.Name("claude", "pro", Profile(), "a@example.invalid", "workspace-a"));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("broken JSON")]
    [InlineData("{\"oauthAccount\":{\"emailAddress\":5}}")]
    public void OptionalProfileErrorsRetainTheKnownSubscription(string? profile)
        => Assert.Equal("Max", AccountPlanDisplay.Name("claude", "max", profile, "a@example.invalid", "workspace-a"));
    [Fact]
    public void LabelsRemainBoundedAndCodexNamesStayUnchanged()
    {
        Assert.Equal("Pro 5x", AccountPlanDisplay.Name("codex", "prolite"));
        Assert.Equal("Pro 20x", AccountPlanDisplay.Name("codex", "pro"));
        Assert.Null(AccountPlanDisplay.Name("claude", "bad\nlabel"));
        Assert.Null(AccountPlanDisplay.Name("claude", new string('x', 81)));
        Assert.Equal("Max", AccountPlanDisplay.Name("claude", "MAX"));
        Assert.Equal("Max", AccountPlanDisplay.Name("claude", "max", new string('x', 262145), "a@example.invalid", "workspace-a"));
    }
}
