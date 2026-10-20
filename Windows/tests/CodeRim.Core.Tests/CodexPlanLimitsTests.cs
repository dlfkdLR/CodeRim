using CodeRim.Core.Domain;

namespace CodeRim.Core.Tests;

public sealed class CodexPlanLimitsTests
{
    [Theory]
    [InlineData("pro")]
    [InlineData("PRO")]
    [InlineData(" Pro ")]
    public void ProHidesOnlyTheFiveHourBandAndRecomputesTheHeadline(string plan)
    {
        LimitWindow[] windows = [new("before", "Before", 1, DurationMinutes: 269),
            new("lower", "Lower", 90, DurationMinutes: 270), new("session", "5 hours", 95, DurationMinutes: 300),
            new("upper", "Upper", 99, DurationMinutes: 330), new("after", "After", 2, DurationMinutes: 331),
            new("weekly", "Weekly", 40, DurationMinutes: 10080), new("unknown", "Unknown")];
        var visible = CodexPlanLimits.VisibleWindows(windows, plan);
        Assert.Equal(["before", "after", "weekly", "unknown"], visible.Select(x => x.Id));
        Assert.Equal("weekly", new ProviderReading("codex", ReadingState.Ready, visible).Headline?.Id);
        Assert.Equal(7, windows.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plus")]
    [InlineData("prolite")]
    [InlineData("Pro 20x")]
    [InlineData("pro-team")]
    public void OtherOrUnknownRawPlansRetainReportedQuotas(string? plan)
    {
        LimitWindow[] windows = [new("session", "5 hours", 95, DurationMinutes: 300), new("weekly", "Weekly", 40, DurationMinutes: 10080)];
        Assert.Same(windows, CodexPlanLimits.VisibleWindows(windows, plan));
    }

    [Fact]
    public void OnlyFiveHourWindowsAreNeverBlanked()
    {
        LimitWindow[] windows = [new("session", "5 hours", 0, DurationMinutes: 300)];
        Assert.Same(windows, CodexPlanLimits.VisibleWindows(windows, "pro"));
        Assert.Empty(CodexPlanLimits.VisibleWindows([], "pro"));
    }
}
