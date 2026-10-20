using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class CompanionLimitPresentationTests
{
    private static ProviderReading Reading(DateTimeOffset now) => new("codex", ReadingState.Ready,
        [new("codex.primary", "5 hours", 95, now.AddMinutes(-1), 300),
         new("codex.secondary", "Weekly", 40, now.AddDays(3), 10080),
         new("rate-limit-reset-credits", "Reset credits", ResetsAt: now.AddMinutes(-1), RemainingCount: 2)], now);

    [Theory]
    [InlineData("pro", 1)]
    [InlineData(" PRO ", 1)]
    [InlineData("prolite", 2)]
    [InlineData("plus", 2)]
    [InlineData("Pro 20x", 2)]
    [InlineData(null, 2)]
    public void CompanionQuotasUseRawPlanAndExcludeResetCredits(string? plan, int expected)
    {
        var raw = Reading(DateTimeOffset.Now);
        var display = CompanionLimitPresentation.ForDisplay(raw, plan);
        Assert.Equal(expected, display.Windows.Count);
        Assert.DoesNotContain(display.Windows, window => window.Id == "rate-limit-reset-credits");
        Assert.Equal(3, raw.Windows.Count);
        if (expected == 1) Assert.Equal("codex.secondary", display.Headline!.Id);
    }

    [Fact]
    public void ProOnlyQuotaFallbackIsNotSuppressedByResetCredits()
    {
        var raw = Reading(DateTimeOffset.Now);
        var display = CompanionLimitPresentation.ForDisplay(raw with { Windows = [raw.Windows[0], raw.Windows[2]] }, "pro");
        Assert.Equal("codex.primary", Assert.Single(display.Windows).Id);
    }

    [Theory]
    [InlineData(ReadingState.Loading)]
    [InlineData(ReadingState.Disabled)]
    [InlineData(ReadingState.NeedsAuth)]
    [InlineData(ReadingState.Unavailable)]
    [InlineData(ReadingState.Error)]
    [InlineData(ReadingState.Partial)]
    public void NonVisibleAccountStateNeverPublishesOldWindows(ReadingState state)
    {
        var display = CompanionLimitPresentation.ForDisplay(Reading(DateTimeOffset.Now) with { State = state }, "plus");
        Assert.Empty(display.Windows); Assert.Null(display.UpdatedAt); Assert.Equal(state, display.State);
    }

    [Fact]
    public void MultipleAccountBucketsKeepDistinctDisplayNamesWithoutChangingCache()
    {
        var raw = Reading(DateTimeOffset.Now) with { Windows =
            [new("codex.primary", "5 hours", 20, DurationMinutes: 300),
             new("codex_bengalfox.primary", "5 hours", 50, DurationMinutes: 300, AccountLimitName: "Spark fixture")] };
        var display = CompanionLimitPresentation.ForDisplay(raw);
        Assert.Collection(display.Windows, first => Assert.Equal("Codex · 5 hours", first.Name),
            second => Assert.Equal("Spark fixture · 5 hours", second.Name));
        Assert.All(raw.Windows, window => Assert.Equal("5 hours", window.Name));
        Assert.Equal(raw.Windows[1].UsedPercent, display.Windows[1].UsedPercent);
        Assert.Equal(raw.Windows[1].Id, display.Windows[1].Id);
    }

    [Fact]
    public void ClaudeKeepsBothPeriodsAndGenericUnitsRemainUntouched()
    {
        var claude = new ProviderReading("claude", ReadingState.Stale,
            [new("five_hour", "Session", 30, DurationMinutes: 300), new("seven_day", "Week", 40, DurationMinutes: 10080)]);
        Assert.Collection(CompanionLimitPresentation.ForDisplay(claude, "pro").Windows,
            first => Assert.Equal("5 hours", first.Name), second => Assert.Equal("Weekly", second.Name));
        var balance = new ProviderReading("openrouter", ReadingState.Ready, [new("balance", "USD", RemainingCount: 200, Unit: "USD")]);
        Assert.Same(balance, CompanionLimitPresentation.ForDisplay(balance));
    }

    [Fact]
    public void SnapshotRoundTripSeparatesPublicFreshQuotasFromRawRestartCache()
    {
        var now = DateTimeOffset.Now; var raw = Reading(now); var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            CompanionFile.Write(new(1, now, [new("codex", "Codex", true, null,
                CompanionLimitPresentation.ForDisplay(raw, "pro"), "owner-a", raw)]), path);
            var restored = Assert.Single(CompanionFile.Read(path).Providers);
            Assert.Equal(ReadingState.Ready, restored.Limits.State);
            Assert.Equal("codex.secondary", Assert.Single(restored.Limits.Windows).Id);
            Assert.Equal(raw.Windows, restored.RestartLimits.Windows);
            Assert.Equal("owner-a", restored.AccountScope);
            Assert.Equal(2, restored.RestartLimits.Windows[2].RemainingCount);
            var serialized = JsonSerializer.Serialize(restored, CompanionFile.JsonOptions);
            Assert.DoesNotContain("restartLimits", serialized, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LegacySnapshotsStillRestoreAllPreviouslySavedQuotas()
    {
        var raw = Reading(DateTimeOffset.Now);
        var provider = new CompanionProvider("codex", "Codex", true, null, raw, "owner-a");
        var restored = JsonSerializer.Deserialize<CompanionProvider>(JsonSerializer.Serialize(provider, CompanionFile.JsonOptions), CompanionFile.JsonOptions)!;
        Assert.Null(restored.CachedLimits);
        Assert.Same(restored.Limits, restored.RestartLimits);
        Assert.Equal(3, restored.RestartLimits.Windows.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidRawCacheCannotBypassSnapshotValidation(bool wrongProvider)
    {
        var now = DateTimeOffset.Now; var raw = Reading(now);
        var invalid = wrongProvider ? raw with { Id = "claude" } : raw with { Windows = [new("bad", "Bad", double.NaN)] };
        var snapshot = new CompanionSnapshot(1, now, [new("codex", "Codex", true, null,
            CompanionLimitPresentation.ForDisplay(raw, "pro"), "owner-a", invalid)]);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try { Assert.Throws<InvalidDataException>(() => CompanionFile.Write(snapshot, path)); Assert.False(File.Exists(path)); }
        finally { File.Delete(path); }
    }
    [Fact]
    public void OversizedCacheCannotReplaceAnExistingReadableSnapshot()
    {
        var now = DateTimeOffset.Now; var raw = Reading(now); var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var snapshot = new CompanionSnapshot(1, now, [new("codex", "Codex", true, null,
                CompanionLimitPresentation.ForDisplay(raw, "pro"), "owner-a", raw)]);
            CompanionFile.Write(snapshot, path); var before = File.ReadAllBytes(path);
            var oversized = raw with { Message = new string('x', 5 * 1024 * 1024) };
            Assert.Throws<InvalidDataException>(() => CompanionFile.Write(snapshot with { Providers =
                [snapshot.Providers[0] with { Limits = oversized, CachedLimits = oversized }] }, path));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(3, Assert.Single(CompanionFile.Read(path).Providers).RestartLimits.Windows.Count);
        }
        finally { File.Delete(path); }
    }

}
