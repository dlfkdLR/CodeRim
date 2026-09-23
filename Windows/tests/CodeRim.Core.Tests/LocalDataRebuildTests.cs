using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class LocalDataRebuildTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "coderim-rebuild-" + Guid.NewGuid().ToString("N"));
    private readonly UsageRepository repository;
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-09-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    public LocalDataRebuildTests() { Directory.CreateDirectory(directory); repository = new UsageRepository(Path.Combine(directory, "usage.sqlite")); }
    private static UsageEvent Event(string id, long input = 100, string provider = "codex", int minute = 0)
        => new(id, Time.AddMinutes(minute), new(input, 0, 10, 0), "gpt-6-astra", "project", "session", provider);
    [Fact]
    public void RebuildReplacesInflatedDerivedCountersAndOldLinksWithoutTouchingOtherProvider()
    {
        repository.Merge("codex", [Event("same", 900), Event("removed")], [new("old-session", null, [new("image", Time, 2)])]);
        repository.Merge("claude", [Event("claude", provider: "claude")]);
        var result = repository.Rebuild("codex", [Event("same", 120)], [new("session", "parent", [new("new-image", Time, 1)])]);
        Assert.Equal(120, Assert.Single(result).Usage.InputTokens);
        var details = Assert.Single(repository.ReadSessionDetails("codex")); Assert.Equal("parent", details.ParentId);
        Assert.Equal("new-image", Assert.Single(details.Attachments).Id);
        Assert.Single(repository.Read("claude"));
    }
    [Fact]
    public void RebuildPreservesClearCutoffAndExplicitEventExclusions()
    {
        repository.Merge("codex", [Event("excluded")], [new("session", null, [new("excluded-image", Time, 1)])]);
        repository.Clear("codex", Time.AddMinutes(1));
        var rebuilt = repository.Rebuild("codex", [Event("excluded", minute: 2), Event("pre-cutoff"), Event("new", minute: 3)],
            [new("session", null, [new("excluded-image", Time.AddMinutes(3), 1), new("old-image", Time, 1), new("new-image", Time.AddMinutes(3), 2)])]);
        Assert.Equal("new", Assert.Single(rebuilt).EventKey);
        Assert.Equal("new-image", Assert.Single(Assert.Single(repository.ReadSessionDetails("codex")).Attachments).Id);
    }
    [Fact]
    public void FailedEnumerationRollsBackDeletedRowsAndPartialWrites()
    {
        repository.Merge("codex", [Event("original")], [new("original-session", null, [])]);
        static IEnumerable<UsageEvent> Broken()
        {
            yield return Event("replacement");
            throw new IOException("fixture read failure");
        }
        Assert.Throws<IOException>(() => repository.Rebuild("codex", Broken()));
        Assert.Equal("original", Assert.Single(repository.Read("codex")).EventKey);
        Assert.Equal("original-session", Assert.Single(repository.ReadSessionDetails("codex")).Id);
    }
    [Fact]
    public void StatisticsUseProviderRowsAndPersistAcrossRestart()
    {
        repository.Merge("codex", [Event("first"), Event("last", minute: 2)]);
        repository.Merge("claude", [Event("unrelated", provider: "claude", minute: 100)]);
        var restored = new UsageRepository(Path.Combine(directory, "usage.sqlite")).Statistics("codex");
        Assert.Equal(2, restored.RecordCount); Assert.True(restored.DatabaseBytes > 0);
        Assert.Equal(Time, restored.OldestRecord); Assert.Equal(Time.AddMinutes(2), restored.NewestRecord);
        repository.Clear("codex", Time.AddMinutes(3));
        var empty = repository.Statistics("codex"); Assert.Equal(0, empty.RecordCount); Assert.Null(empty.OldestRecord); Assert.Null(empty.NewestRecord);
    }
    [Fact]
    public async Task RebuildScannerRejectsRedirectedRootWithoutReplacingItsData()
    {
        var source = Path.Combine(directory, "source"); var redirected = Path.Combine(directory, "redirected"); Directory.CreateDirectory(source);
        Directory.CreateSymbolicLink(redirected, source);
        var scanner = new UsageScanner("codex", [redirected], requireCompleteSources: true);
        await Assert.ThrowsAsync<IOException>(() => scanner.ScanAsync(WeekStart.Monday, TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData("codex", "malformed fixture line")]
    [InlineData("codex", "{}")]
    [InlineData("codex", "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"}}")]
    [InlineData("claude", "malformed fixture line")]
    [InlineData("claude", "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":1}}}")]
    public async Task StrictRebuildMarksIgnoredCorruptionAsPartial(string provider, string line)
    {
        File.WriteAllText(Path.Combine(directory, "session.jsonl"), line + "\n");
        var scanner = new UsageScanner(provider, [directory], requireCompleteSources: true);
        var scan = await scanner.ScanAsync(WeekStart.Monday, TestContext.Current.CancellationToken);
        Assert.Equal(1, scan.SourceCount); Assert.Equal(DataQuality.Partial, scan.Snapshot.Quality);
    }
    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task StrictRebuildAllowsWhitespaceAndOrdinaryNonusageMessages(string provider)
    {
        var usage = provider == "codex" ? """
            {"type":"event_msg","timestamp":"2026-09-01T00:00:01Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":20},"last_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":20}}}}
            """ : """
            {"type":"assistant","sessionId":"fixture","timestamp":"2026-09-01T00:00:01Z","message":{"id":"fixture-message","role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":100,"output_tokens":20}}}
            """;
        File.WriteAllText(Path.Combine(directory, "session.jsonl"), "\n \t\r\n{\"type\":\"user\"}\n" + usage + "\n");
        var scan = await new UsageScanner(provider, [directory], requireCompleteSources: true).ScanAsync(WeekStart.Monday, TestContext.Current.CancellationToken);
        Assert.Equal(DataQuality.Exact, scan.Snapshot.Quality); Assert.Equal(120, Assert.Single(scan.Events).Usage.TotalTokens);
    }
    [Theory]
    [InlineData("codex", "")]
    [InlineData("codex", "\n \t\r\n")]
    [InlineData("claude", "")]
    public async Task EmptyFileMakesMixedSourceInventoryIncomplete(string provider, string empty)
    {
        var good = provider == "codex" ? """
            {"type":"event_msg","timestamp":"2026-09-01T00:00:01Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":20},"last_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":20}}}}
            """ : """
            {"type":"assistant","sessionId":"fixture","timestamp":"2026-09-01T00:00:01Z","message":{"id":"fixture-message","role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":100,"output_tokens":20}}}
            """;
        File.WriteAllText(Path.Combine(directory, "good.jsonl"), good + "\n");
        File.WriteAllText(Path.Combine(directory, "empty.jsonl"), empty);
        var scan = await new UsageScanner(provider, [directory], requireCompleteSources: true).ScanAsync(WeekStart.Monday, TestContext.Current.CancellationToken);
        Assert.Equal(2, scan.SourceCount); Assert.Single(scan.Events); Assert.Equal(DataQuality.Partial, scan.Snapshot.Quality);
    }
    public void Dispose() => Directory.Delete(directory, true);
}
