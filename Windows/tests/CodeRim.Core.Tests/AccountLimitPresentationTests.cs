using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class AccountLimitPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PrimaryWeeklyPrecedesFiveHoursAndAdditionalLimits()
    {
        LimitWindow[] windows = [new("z.primary", "z", DurationMinutes: 300, AccountLimitName: "Zulu"),
            new("codex.primary", "5 hours", DurationMinutes: 300),
            new("a.primary", "a", DurationMinutes: 300, AccountLimitName: "Alpha"),
            new("CODEX.secondary", "Weekly", DurationMinutes: 10080)];
        Assert.Equal(["CODEX.secondary", "codex.primary"], AccountLimitPresentation.VisibleWindows(windows, false).Select(x => x.Id));
        Assert.Equal(["CODEX.secondary", "a.primary", "codex.primary", "z.primary"], AccountLimitPresentation.VisibleWindows(windows, true).Select(x => x.Id));
        Assert.Equal("Claude", AccountLimitPresentation.Name(new("seven_day", "Weekly"), "claude"));
        Assert.Equal("GPT-5.3-Codex-Spark", AccountLimitPresentation.Name(new("codex_bengalfox.primary", "5 hours"), "codex"));
        Assert.Equal("Codex", AccountLimitPresentation.Name(new("weekly", "Weekly"), "codex"));
        Assert.Equal(4, windows.Length);
    }

    [Theory]
    [InlineData(0, "0 minutes")]
    [InlineData(1, "1 minute")]
    [InlineData(269, "269 minutes")]
    [InlineData(270, "5 hours")]
    [InlineData(330, "5 hours")]
    [InlineData(331, "331 minutes")]
    [InlineData(1440, "1 day")]
    [InlineData(2880, "2 days")]
    [InlineData(9000, "Weekly")]
    [InlineData(11000, "Weekly")]
    [InlineData(11001, "7 days")]
    public void PeriodLabelsFollowReportedDuration(int minutes, string expected)
        => Assert.Equal(expected, AccountLimitPresentation.WindowLabel(minutes));

    [Theory]
    [InlineData(75, LimitPaceState.Ahead, "25% above even pace", 50)]
    [InlineData(50, LimitPaceState.OnPace, "On even pace", null)]
    [InlineData(50.9, LimitPaceState.OnPace, "On even pace", null)]
    [InlineData(49, LimitPaceState.Reserve, "1% below even pace", null)]
    [InlineData(0, LimitPaceState.Reserve, "50% below even pace", null)]
    [InlineData(150, LimitPaceState.Ahead, "50% above even pace", 0)]
    [InlineData(-5, LimitPaceState.Reserve, "50% below even pace", null)]
    public void PaceUsesElapsedWindowAndClampedObservedUse(double used, LimitPaceState expected, string summary, int? runOutMinutes)
    {
        var pace = AccountLimitPresentation.Pace(new("q", "Q", used, Now.AddMinutes(150), 300), Now)!;
        Assert.Equal(expected, pace.State); Assert.Equal(summary, pace.Summary);
        if (runOutMinutes is { } minutes) Assert.Equal(Now.AddMinutes(minutes), pace.ProjectedExhaustion);
        else if (used <= 50 || used < 0) Assert.Null(pace.ProjectedExhaustion);
        else Assert.True(pace.ProjectedExhaustion < Now.AddMinutes(150));
    }

    [Theory]
    [InlineData(0, 1, 50)]
    [InlineData(527041, 1, 50)]
    [InlineData(300, 0, 50)]
    [InlineData(300, -1, 50)]
    [InlineData(300, 301, 50)]
    [InlineData(300, 292, 50)]
    [InlineData(300, 150, double.NaN)]
    [InlineData(300, 150, double.PositiveInfinity)]
    public void PaceWaitsForThreePercentAndRejectsExpiredOrUnsupportedData(int minutes, int untilReset, double used)
        => Assert.Null(AccountLimitPresentation.Pace(new("q", "Q", used, Now.AddMinutes(untilReset), minutes), Now));

    [Fact]
    public void PaceThresholdAndExtremeDatesDoNotOverflow()
    {
        Assert.NotNull(AccountLimitPresentation.Pace(new("q", "Q", 5, Now.AddMinutes(291), 300), Now));
        Assert.Null(AccountLimitPresentation.Pace(new("q", "Q", 1, null, 300), Now));
        Assert.Null(AccountLimitPresentation.Pace(new("q", "Q", null, Now.AddMinutes(150), 300), Now));
        Assert.Null(AccountLimitPresentation.Pace(new("q", "Q", double.Epsilon, DateTimeOffset.MaxValue, 300), DateTimeOffset.MaxValue.AddMinutes(-150))!.ProjectedExhaustion);
        Assert.NotNull(AccountLimitPresentation.Pace(new("q", "Q", 50, DateTimeOffset.MinValue.AddMinutes(150), 300), DateTimeOffset.MinValue));
    }

    [Fact]
    public void ExtremeOffsetUsesTheInstantWithoutOverflowingItsLocalClock()
    {
        var now = new DateTimeOffset(9999, 12, 31, 23, 30, 0, TimeSpan.FromHours(14));
        var pace = AccountLimitPresentation.Pace(new("q", "Q", 75, DateTimeOffset.MaxValue, 1440), now)!;
        Assert.NotNull(pace.ProjectedExhaustion);
        Assert.InRange(pace.ProjectedExhaustion!.Value, now.ToUniversalTime(), DateTimeOffset.MaxValue);
    }

    [Theory]
    [InlineData(-100, "just now")]
    [InlineData(4, "just now")]
    [InlineData(5, "5 sec ago")]
    [InlineData(59, "59 sec ago")]
    [InlineData(60, "1 min ago")]
    [InlineData(3599, "59 min ago")]
    [InlineData(3600, "1 hr ago")]
    [InlineData(86399, "23 hr ago")]
    [InlineData(86400, "1 day ago")]
    [InlineData(172800, "2 days ago")]
    public void FreshnessUsesActualTimeAndStalePrefix(int age, string elapsed)
    {
        Assert.Equal("Updated " + elapsed, AccountLimitPresentation.Freshness(Now.AddSeconds(-age), Now));
        Assert.Equal("Last known · updated " + elapsed, AccountLimitPresentation.Freshness(Now.AddSeconds(-age), Now, true));
    }

    [Fact]
    public void CodexDisplayMetadataSurvivesParsingAndSnapshotRoundTrip()
    {
        using var json = JsonDocument.Parse("""{"rateLimitsByLimitId":{"codex":{"limitName":" Codex Team ","primary":{"usedPercent":25,"windowDurationMins":300}},"model_x":{"limitName":"bad\nname","modelName":"Model X","primary":{"usedPercent":40,"windowDurationMins":10080}}}}""");
        var windows = ProviderParsers.Codex(json.RootElement);
        Assert.Equal("Codex Team", AccountLimitPresentation.Name(windows[0], "codex"));
        Assert.Equal("Model X", AccountLimitPresentation.Name(windows[1], "codex"));
        var encoded = JsonSerializer.Serialize(new CompanionSnapshot(1, Now, [new("codex", "Codex", true, null, new("codex", ReadingState.Ready, windows, Now))]), CompanionFile.JsonOptions);
        var restored = JsonSerializer.Deserialize<CompanionSnapshot>(encoded, CompanionFile.JsonOptions)!;
        Assert.Equal(windows, restored.Providers[0].Limits.Windows);
        var legacy = JsonSerializer.Deserialize<LimitWindow>("""{"id":"codex.primary","name":"5 hours","usedPercent":25,"durationMinutes":300}""", CompanionFile.JsonOptions)!;
        Assert.Null(legacy.AccountLimitName); Assert.Equal("Codex", AccountLimitPresentation.Name(legacy, "codex"));
    }

    [Fact]
    public void OversizedUtf8NamesFallBackWithoutChangingQuotaValues()
    {
        var payload = JsonSerializer.Serialize(new { rateLimits = new { limitName = new string('한', 86), modelName = "\t", primary = new { usedPercent = 33, windowDurationMins = 300 } } });
        using var json = JsonDocument.Parse(payload);
        var window = Assert.Single(ProviderParsers.Codex(json.RootElement));
        Assert.Null(window.AccountLimitName); Assert.Equal("Codex", AccountLimitPresentation.Name(window, "codex"));
        Assert.Equal(33, window.UsedPercent);
    }
}
