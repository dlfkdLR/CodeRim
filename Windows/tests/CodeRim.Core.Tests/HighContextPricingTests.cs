using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Tests;

public sealed class HighContextPricingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coderim-context-pricing-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    public HighContextPricingTests() => Directory.CreateDirectory(root);
    private static object Tokens(TokenUsage usage) => new { input_tokens = usage.InputTokens, cached_input_tokens = usage.CachedInputTokens,
        output_tokens = usage.OutputTokens, cache_write_input_tokens = usage.CacheWriteInputTokens };
    private static string Row(int minute, TokenUsage cumulative, TokenUsage last) => JsonSerializer.Serialize(new {
        type = "event_msg", timestamp = Now.AddMinutes(minute), payload = new { type = "token_count",
            info = new { total_token_usage = Tokens(cumulative), last_token_usage = Tokens(last) } } });
    private async Task<ScanResult> Scan(params string[] rows)
    {
        var source = Path.Combine(root, "sources"); Directory.CreateDirectory(source);
        File.WriteAllLines(Path.Combine(source, "session.jsonl"), rows.Prepend("""{"type":"session_meta","payload":{"id":"pricing-fixture","model":"gpt-5.6-sol"}}"""));
        return await new UsageScanner("codex", [source]).ScanAsync(WeekStart.Monday, TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task ExactLargeRequestPricesInputCacheWritesAndOutputAfterDatabaseReload()
    {
        var large = new TokenUsage(300000, 100000, 100, 10000);
        var scan = await Scan(Row(0, large, large));
        Assert.Single(scan.Events);
        var repository = new UsageRepository(Path.Combine(root, "usage.sqlite")); repository.Merge("codex", scan.Events);
        var cost = UsageAnalytics.Estimate(repository.Read("codex"));
        Assert.Equal(1.703m, cost.Amount); Assert.False(cost.IsPartial);
    }
    [Theory]
    [InlineData(272000, PricingContext.Standard)]
    [InlineData(272001, PricingContext.HighContext)]
    public async Task RequestBoundaryUsesNormalizedInputNotAggregateTotal(long input, PricingContext expected)
    {
        var usage = new TokenUsage(input, 0, 0, 0); var scan = await Scan(Row(0, usage, usage));
        Assert.Equal(expected, Assert.Single(scan.Events).PricingContext);
        var standard = new TokenUsage(200000, 0, 0, 0);
        var multiple = await Scan(Row(0, standard, standard), Row(1, standard.Add(standard), standard));
        Assert.All(multiple.Events, x => Assert.Equal(PricingContext.Standard, x.PricingContext));
        Assert.Equal(1.6m, UsageAnalytics.Estimate(multiple.Events).Amount);
    }

    [Fact]
    public async Task MissingRequestProofRetainsModelWideGapAcrossRangeAndBuckets()
    {
        var large = new TokenUsage(300000, 0, 0, 0); var standard = new TokenUsage(1000, 0, 0, 0);
        var scan = await Scan(Row(0, TokenUsage.Zero, TokenUsage.Zero), Row(1, large, standard), Row(2, large.Add(standard), standard));
        Assert.Equal(2, scan.Events.Count); Assert.Null(scan.Events[0].PricingContext); Assert.Equal(PricingContext.Standard, scan.Events[1].PricingContext);
        var cost = UsageAnalytics.Estimate(scan.Events); Assert.Null(cost.Amount); Assert.Equal(301000, cost.ExcludedTokens);
        var buckets = AnalyticsTimeline.Build(scan.Events, AnalyticsRange.SevenDays, Now.AddMinutes(3), TimeZoneInfo.Utc);
        Assert.Null(buckets[^1].Cost.Amount); Assert.Equal(301000, buckets[^1].Cost.ExcludedTokens);
    }

    [Fact]
    public async Task MixedTiersAndCounterRestartKeepExactRequestCosts()
    {
        var high = new TokenUsage(300000, 100000, 100, 10000); var standard = new TokenUsage(1000, 0, 0, 0);
        var scan = await Scan(Row(0, high, high), Row(1, high.Add(standard), standard), Row(2, standard, standard));
        Assert.Equal(3, scan.Events.Count); Assert.Equal(PricingContext.HighContext, scan.Events[0].PricingContext);
        Assert.All(scan.Events.Skip(1), x => Assert.Equal(PricingContext.Standard, x.PricingContext));
        var cost = UsageAnalytics.Estimate(scan.Events); Assert.Equal(1.711m, cost.Amount); Assert.False(cost.IsPartial);
        Assert.Equal(cost.Amount, AnalyticsTimeline.Build(scan.Events, AnalyticsRange.SevenDays, Now.AddMinutes(3), TimeZoneInfo.Utc).Sum(x => x.Cost.Amount ?? 0));
    }

    [Fact]
    public async Task LegacyDatabaseMigrationPreservesHistoryAndReimportFillsOptionalContext()
    {
        var high = new TokenUsage(300000, 100000, 100, 10000); var scan = await Scan(Row(0, high, high));
        var item = Assert.Single(scan.Events); var path = Path.Combine(root, "legacy.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE events(provider TEXT NOT NULL,id TEXT NOT NULL,time INTEGER NOT NULL,input INTEGER NOT NULL,cached INTEGER NOT NULL,
                    output INTEGER NOT NULL,written INTEGER,model TEXT NOT NULL,project TEXT NOT NULL,session TEXT NOT NULL,projectId TEXT NOT NULL,PRIMARY KEY(provider,id));
                INSERT INTO events VALUES('codex',$id,$time,190000,100000,100,10000,'gpt-5.6-sol','Unknown project',$session,'unknown');
                """;
            command.Parameters.AddWithValue("$id", item.EventKey); command.Parameters.AddWithValue("$time", item.OccurredAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$session", item.SessionId); command.ExecuteNonQuery();
        }
        var repository = new UsageRepository(path); var before = Assert.Single(repository.Read("codex"));
        Assert.Equal(high, before.Usage); Assert.Null(before.PricingContext); Assert.Null(UsageAnalytics.Estimate([before]).Amount);
        repository.Merge("codex", scan.Events); var reloaded = Assert.Single(new UsageRepository(path).Read("codex"));
        Assert.Equal(item, reloaded); Assert.Equal(1.703m, UsageAnalytics.Estimate([reloaded]).Amount);
        repository.Merge("codex", [before]); Assert.Equal(PricingContext.HighContext, Assert.Single(repository.Read("codex")).PricingContext);
        Assert.DoesNotContain("PricingContext", JsonSerializer.Serialize(reloaded), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContextParticipatesInRollbackRebuildClearAndProviderIsolation()
    {
        var high = new TokenUsage(300000, 100000, 100, 10000); var item = Assert.Single((await Scan(Row(0, high, high))).Events);
        var repository = new UsageRepository(Path.Combine(root, "usage.sqlite")); repository.Merge("codex", [item]);
        repository.Merge("claude", [item with { Provider = "claude" }]); Assert.Null(Assert.Single(repository.Read("claude")).PricingContext);
        IEnumerable<UsageEvent> Failed() { yield return item with { PricingContext = null }; throw new InvalidOperationException("fixture"); }
        Assert.Throws<InvalidOperationException>(() => repository.Rebuild("codex", Failed()));
        Assert.Equal(PricingContext.HighContext, Assert.Single(repository.Read("codex")).PricingContext);
        repository.Rebuild("codex", [item with { PricingContext = null }]); Assert.Null(Assert.Single(repository.Read("codex")).PricingContext);
        repository.Clear("codex", Now.AddMinutes(5)); Assert.Empty(repository.Merge("codex", [item])); Assert.Single(repository.Read("claude"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopiedTranscriptCanEnrichRequestProofInEitherDiscoveryOrder(bool knownFirst)
    {
        var unknownZero = new TokenUsage(0, 0, 0); var high = new TokenUsage(300000, 100000, 100, 10000);
        var unresolved = Assert.Single((await Scan(Row(0, unknownZero, unknownZero), Row(1, high, high))).Events);
        Assert.Null(unresolved.PricingContext); Assert.Null(unresolved.Usage.CacheWriteInputTokens);
        var source = Path.Combine(root, "sources"); var copy = Path.Combine(source, knownFirst ? "a-known.jsonl" : "z-known.jsonl");
        File.WriteAllLines(copy, ["""{"type":"session_meta","payload":{"id":"pricing-fixture","model":"gpt-5.6-sol"}}""", Row(1, high, high)]);
        // Discovery is sorted by path, not file mtime. These names place the
        // known copy before/after session.jsonl to exercise both merge branches.
        var enriched = Assert.Single((await new UsageScanner("codex", [source]).ScanAsync(WeekStart.Monday, TestContext.Current.CancellationToken)).Events);
        Assert.Equal(unresolved.EventKey, enriched.EventKey); Assert.Equal(high, enriched.Usage); Assert.Equal(PricingContext.HighContext, enriched.PricingContext);
        var repository = new UsageRepository(Path.Combine(root, "usage.sqlite")); repository.Merge("codex", [unresolved]);
        repository.Merge("codex", [enriched]); repository.Merge("codex", [unresolved]);
        var reloaded = Assert.Single(repository.Read("codex")); Assert.Equal(high, reloaded.Usage); Assert.Equal(PricingContext.HighContext, reloaded.PricingContext);
        Assert.Equal(1.703m, UsageAnalytics.Estimate([reloaded]).Amount);
    }

    [Fact]
    public async Task OpeningMigratedHistoryDoesNotAcquireAnExtraWriteLock()
    {
        var path = Path.Combine(root, "concurrent.sqlite"); _ = new UsageRepository(path);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var writing = connection.BeginTransaction();
        var opening = Task.Run(() => new UsageRepository(path));
        try
        {
            var completed = await Task.WhenAny(opening, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Same(opening, completed); await opening;
        }
        finally { writing.Rollback(); await opening; }
    }

    [Fact]
    public void InvalidContextNeverMakesTieredModelCheapAndUntieredPricesStayUnchanged()
    {
        var item = new UsageEvent("fixture", Now, new(300000, 0, 0, 0), "gpt-5.6-sol") { PricingContext = (PricingContext)99 };
        Assert.Null(UsageAnalytics.Estimate([item]).Amount);
        Assert.Equal(.225m, UsageAnalytics.Estimate([item with { Model = "gpt-5.4-mini", PricingContext = PricingContext.HighContext }]).Amount);
    }
    public void Dispose() => Directory.Delete(root, true);
}
