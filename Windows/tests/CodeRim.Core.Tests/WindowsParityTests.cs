using System.Net;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Parsing;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class WindowsParityTests
{


    [Fact]
    public void CompanionRejectsInvalidActivityFromDiskAndBeforeWriting()
    {
        var directory = Temp();
        try
        {
            var path = Path.Combine(directory, "snapshot.json");
            var reading = new ProviderReading("openrouter", ReadingState.Ready, [], DateTimeOffset.Now,
                CostUsage: new("USD", 30, "Last 30 days (UTC)", null, [new("not-a-date", null, -5, null, null, null, -2, null)]));
            var snapshot = new CompanionSnapshot(1, DateTimeOffset.Now, [new("openrouter", "OpenRouter", true, null, reading)]);
            Assert.Throws<InvalidDataException>(() => CompanionFile.Write(snapshot, path));
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, CompanionFile.JsonOptions));
            Assert.Throws<InvalidDataException>(() => CompanionFile.Read(path));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task OpenRouterActivitySurvivesScriptMappingAndCompanionRoundTrip()
    {
        var date = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        using var handler = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
            request.RequestUri!.AbsolutePath.EndsWith("/activity", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { data = new[] { new { date, model = "fixture", prompt_tokens = 12345, completion_tokens = 2345, reasoning_tokens = 0, requests = 7, usage = 19.25, byok_usage_inference = 5.75 } } })
                : request.RequestUri.AbsolutePath.EndsWith("/credits", StringComparison.Ordinal)
                    ? """{"data":{"total_credits":100,"total_usage":10}}"""
                    : """{"data":{"usage":1,"limit":10,"limit_remaining":9}}""") });
        using var scripts = new ScriptProviders(handler);
        var reading = await scripts.FetchAsync("openrouter", key => key is "OPENROUTER_API_KEY" or "OPENROUTER_MANAGEMENT_API_KEY" ? "fixture" : null, null, TestContext.Current.CancellationToken);
        var activity = Assert.IsType<ProviderCostUsage>(reading.CostUsage);
        Assert.Equal(25, Assert.Single(activity.Entries).Cost);
        Assert.Equal(5.75, activity.Entries[0].EstimatedCost);
        Assert.Contains(reading.Windows, x => x.Id == "activity-cost" && x.Name.Contains("including estimates", StringComparison.Ordinal));
        Assert.Contains(reading.Windows, x => x.Id == "activity-estimated-cost" && x.Name.Contains("included above", StringComparison.Ordinal));
        Assert.Contains(reading.Windows, x => x.Id == "activity-input" && x.UsedCount == 12345 && x.Unit == "tokens");
        Assert.Contains(reading.Windows, x => x.Id == "activity-requests" && x.UsedCount == 7 && x.Unit == "requests");
        var directory = Temp();
        try
        {
            var path = Path.Combine(directory, "snapshot.json");
            CompanionFile.Write(new(1, DateTimeOffset.Now, [new("openrouter", "OpenRouter", true, null, reading)]), path);
            var restored = Assert.Single(CompanionFile.Read(path).Providers);
            Assert.Null(restored.LocalUsage);
            Assert.Equal(activity.Entries[0], restored.Limits.CostUsage!.Entries[0]);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void PartialQualitySurvivesStaleCompanionRead()
    {
        var directory = Temp(); var now = DateTimeOffset.Now;
        try
        {
            var path = Path.Combine(directory, "snapshot.json");
            var local = new LocalUsage("this-pc", "partial", now.AddHours(-1), now.AddHours(-1), TimeZoneInfo.Local.Id,
                new() { ["today"] = new(100, 0, 10) });
            CompanionFile.Write(new(1, now, [new("codex", "Codex", true, local, new("codex", ReadingState.Ready, [], now))]), path);
            Assert.Equal("partial", Assert.Single(CompanionFile.Read(path).Providers).LocalUsage!.State);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void StrictSubsetComparisonRequiresKnownCacheWrite()
    {
        var before = new TokenUsage(1000, 0, 100, 800);
        Assert.False(new TokenUsage(1100, 0, 110, 100).IsComponentWiseAtLeast(before));
        Assert.False(new TokenUsage(1100, 0, 110).IsComponentWiseAtLeast(before));
    }
    [Fact]
    public void SyntheticSupportsWeeklyAndHourlyQuota()
    {
        using var json = JsonDocument.Parse("""{"weeklyTokenLimit":{"percentRemaining":98},"search":{"hourly":{"limit":250,"requests":2}}}""");
        var windows = HttpProviders.Parse("synthetic", json.RootElement);
        Assert.Equal(2, windows[0].UsedPercent); Assert.Equal(0.8, windows[1].UsedPercent);
    }
    [Theory]
    [InlineData("{\"schemaVersion\":1,\"providers\":null}")]
    [InlineData("{\"schemaVersion\":1,\"generatedAt\":\"2026-09-18T00:00:00Z\",\"providers\":[{\"id\":\"codex\",\"limits\":null}]}")]
    public void InvalidCompanionStructuresAreRejected(string json)
    {
        var directory = Temp();
        try { var path = Path.Combine(directory, "snapshot.json"); File.WriteAllText(path, json); Assert.Throws<InvalidDataException>(() => CompanionFile.Read(path)); }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void BundledScriptsExposeTheirActualSettings()
    {
        Assert.Equal(16, ScriptProviders.Catalog.Count);
        Assert.Contains(ScriptProviders.Catalog["openai"].Settings, x => x.Key == "OPENAI_API_KEY");
    }
    [Fact]
    public void ScriptNetworkPolicyRejectsOtherOriginsAndRedirectTargets()
    {
        using var endpoints = JsonDocument.Parse("""["https://api.vendor.test",{"setting":"BASE","policy":"https-or-loopback-http"}]""");
        Assert.True(ScriptProviders.IsAllowed(new Uri("https://api.vendor.test/usage"), endpoints.RootElement, _ => "http://127.0.0.1:8080"));
        Assert.False(ScriptProviders.IsAllowed(new Uri("https://api.vendor.test.attacker.test/usage"), endpoints.RootElement, _ => null));
        Assert.False(ScriptProviders.IsAllowed(new Uri("https://secret@api.vendor.test/usage"), endpoints.RootElement, _ => null));
        Assert.False(ScriptProviders.IsAllowed(new Uri("http://192.168.1.1/usage"), endpoints.RootElement, _ => "http://192.168.1.1"));
    }
    [Fact]
    public async Task BundledSyntheticScriptRunsThroughTheBoundedHost()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"weeklyTokenLimit":{"percentRemaining":98},"search":{"hourly":{"limit":250,"requests":2}}}""") });
        using var scripts = new ScriptProviders(handler);
        var result = await scripts.FetchAsync("synthetic", _ => "fake-fixture-key", null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, result.State);
        Assert.Contains(result.Windows, window => window.UsedPercent == 2);
    }
    [Theory]
    [InlineData("user", "", true)] [InlineData("assistant", "tool_use", true)] [InlineData("assistant", "end_turn", false)]
    public void ClaudeActivityUsesConversationEvents(string type, string reason, bool busy)
    {
        var json = JsonSerializer.Serialize(new { type, timestamp = "2026-09-18T00:00:00Z", message = new { stop_reason = reason, content = "content is not retained" } });
        Assert.True(ActivityReader.TryClaude(Encoding.UTF8.GetBytes(json), out var actual, out _)); Assert.Equal(busy, actual);
        Assert.False(ActivityReader.TryClaude(Encoding.UTF8.GetBytes("""{"type":"bridge-session","timestamp":"2026-09-18T00:00:00Z"}"""), out _, out _));
    }
    [Fact]
    public async Task ProviderReportedFailureIsReturnedWithoutThrowing()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"code":500,"success":false}""") });
        using var providers = new HttpProviders(handler);
        var result = await providers.FetchAsync("glm", "fixture", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Error, result.State);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OptionalScriptNetworkFailuresPreserveTheAvailableQuota(bool timeout)
    {
        using var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/credits", StringComparison.Ordinal))
            {
                if (timeout) throw new OperationCanceledException("SENSITIVE_TIMEOUT_TEXT");
                throw new HttpRequestException("SENSITIVE_NETWORK_TEXT");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":{"usage":1,"limit":10,"limit_remaining":9}}""") };
        });
        using var scripts = new ScriptProviders(handler);
        var reading = await scripts.FetchAsync("openrouter", key => key == "OPENROUTER_API_KEY" ? "fixture" : null, null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Contains(reading.Windows, x => x.UsedPercent == 10);
        Assert.DoesNotContain("SENSITIVE_", JsonSerializer.Serialize(reading), StringComparison.Ordinal);
    }
    [Fact]
    public void CatalogHasTheSame70UniqueIdentities()
    {
        Assert.Equal(70, ProviderCatalog.All.Count);
        Assert.Equal(70, ProviderCatalog.All.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(ProviderCatalog.Find("gemini-cli")); Assert.NotNull(ProviderCatalog.Find("opencode-zen"));
    }
    [Fact]
    public void ClaudeProjectsDisjointComponentsAndDropsContent()
    {
        var record = Claude("message-1", 100, 200, 300, 40, "C:\\work\\api");
        Assert.Equal(new TokenUsage(600, 200, 40, 300), record.Usage);
        Assert.Equal(640, record.Usage.TotalTokens);
        var json = JsonSerializer.Serialize(record);
        Assert.DoesNotContain("SECRET_PROMPT", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\work", json, StringComparison.Ordinal);
    }
    [Fact]
    public void ClaudeRevisionMergesDisjointMaxima()
    {
        var first = Claude("same", 100, 200, 0, 20);
        var later = Claude("same", 150, 20, 100, 10);
        var result = ClaudeJsonlParser.Merge(first, later);
        Assert.Equal(new TokenUsage(450, 200, 20, 100), result.Usage);
    }
    [Theory]
    [InlineData(-1)] [InlineData(1_000_000_000_001)]
    public void RejectsInvalidClaudeCounts(long count)
    {
        var json = ClaudeLine("x", count, 0, 0, 0);
        Assert.Null(ClaudeJsonlParser.Parse(Encoding.UTF8.GetBytes(json)));
    }
    [Fact]
    public void SameNamedProjectsRemainSeparate()
    {
        var events = new[] { Claude("a", 1, 0, 0, 0, "C:\\work\\api"), Claude("b", 2, 0, 0, 0, "D:\\archive\\api") };
        Assert.Equal(2, UsageAnalytics.Group(events, "project").Count);
        Assert.NotEqual(events[0].ProjectId, events[1].ProjectId);
    }
    [Fact]
    public async Task ClaudeCopiesAreDeduplicatedAcrossFiles()
    {
        var directory = Temp();
        try
        {
            var line = ClaudeLine("same", 10, 20, 30, 40) + "\n";
            await File.WriteAllTextAsync(Path.Combine(directory, "first.jsonl"), line, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "copy.jsonl"), line, TestContext.Current.CancellationToken);
            var scan = await new UsageScanner("claude", [directory]).ScanAsync(WeekStart.Monday, TestContext.Current.CancellationToken);
            Assert.Equal(100, scan.Snapshot.AllTime.TotalTokens); Assert.Single(scan.Events);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void DurableHistoryRetainsRemovedSourcesAndExcludesClearedCopies()
    {
        var directory = Temp();
        try
        {
            var path = Path.Combine(directory, "usage.sqlite"); var repository = new UsageRepository(path);
            var item = Claude("same", 10, 20, 30, 40);
            Assert.Single(repository.Merge("claude", [item, item]));
            Assert.Single(new UsageRepository(path).Merge("claude", []));
            repository.Clear("claude", item.OccurredAt.AddSeconds(1));
            Assert.Empty(repository.Merge("claude", [item with { OccurredAt = item.OccurredAt.AddSeconds(5) }]));
            Assert.Empty(repository.Read("codex"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void RepositoryPreservesUnknownCacheWrites()
    {
        var directory = Temp();
        try
        {
            var repository = new UsageRepository(Path.Combine(directory, "usage.sqlite"));
            var item = new UsageEvent("x", DateTimeOffset.Now, new(100, 10, 20), "gpt-6-astra");
            var row = Assert.Single(repository.Merge("codex", [item]));
            Assert.Null(row.Usage.CacheWriteInputTokens); Assert.Null(UsageAnalytics.Estimate([row]).Amount);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void CacheWriteEnrichmentDoesNotIncreaseOriginalInput()
    {
        var directory = Temp();
        try
        {
            var repository = new UsageRepository(Path.Combine(directory, "usage.sqlite"));
            var initial = new UsageEvent("same", DateTimeOffset.Now, new(1000, 0, 0), "gpt-6-astra");
            repository.Merge("codex", [initial]);
            var updated = Assert.Single(repository.Merge("codex", [initial with { Usage = new(1000, 0, 0, 400) }]));
            Assert.Equal(1000, updated.Usage.InputTokens); Assert.Equal(400, updated.Usage.CacheWriteInputTokens);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void KnownZeroDoesNotEraseKnownCacheWriteAggregates()
    {
        Assert.Equal(50, TokenUsage.Zero.Add(new(100, 0, 0, 50)).CacheWriteInputTokens);
        Assert.True(new TokenUsage(0, 0, 0).IsZero); Assert.True(new TokenUsage(0, 0, 0, 0).IsZero);
    }
    [Fact]
    public void OpenRouterQuotaUsesCurrentResetPeriod()
    {
        using var document = JsonDocument.Parse("""{"data":{"usage":100,"limit":10,"limit_remaining":8,"limit_reset":"daily","usage_daily":2}}""");
        var result = HttpProviders.Parse("openrouter", document.RootElement);
        Assert.Equal(20, result[0].UsedPercent);
    }
    [Fact]
    public void PricingKeepsKnownSubtotalAndExcludesUnknownModels()
    {
        var known = new UsageEvent("a", DateTimeOffset.Now, new(1000, 0, 0, 1000), "gpt-6-astra");
        var unknown = new UsageEvent("b", DateTimeOffset.Now, new(300, 0, 0), "not-priced");
        var summary = UsageAnalytics.Estimate([known, unknown]);
        Assert.Equal(0.0125m, summary.Amount); Assert.True(summary.IsPartial); Assert.Equal(300, summary.ExcludedTokens);
    }
    [Fact]
    public void CodexCacheWriteFieldIsParsed()
    {
        var parsed = CodexJsonlParser.Parse(Encoding.UTF8.GetBytes("""{"type":"event_msg","timestamp":"2026-09-01T00:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1000,"cached_input_tokens":0,"cache_write_input_tokens":1000,"output_tokens":0}}}}"""));
        Assert.Equal(1000, parsed.Token?.CumulativeUsage?.CacheWriteInputTokens);
    }
    [Fact]
    public void CodexDoesNotFallBackToLegacyBucketWhenKeyedBucketsExist()
    {
        using var json = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":90}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":12,"windowDurationMins":300,"resetsAt":1900000000}}}}""");
        var window = Assert.Single(ProviderParsers.Codex(json.RootElement)); Assert.Equal(12, window.UsedPercent); Assert.Equal(300, window.DurationMinutes);
    }
    [Fact]
    public void CopilotNeverInventsPercentForAStandaloneCount()
    {
        using var json = JsonDocument.Parse("""{"quota_snapshots":{"chat":{"remaining":300},"unlimited":{"unlimited":true,"entitlement":999}},"quota_reset_date":"2026-10-01"}""");
        var window = Assert.Single(ProviderParsers.Copilot(json.RootElement)); Assert.Null(window.UsedPercent); Assert.Equal(300, window.RemainingCount); Assert.Equal(2026, window.ResetsAt?.Year);
    }
    [Fact]
    public void GlmCreditsAreNeverTokens()
    {
        using var json = JsonDocument.Parse("""{"success":true,"data":{"limits":[{"type":"CREDIT_LIMIT","unit":3,"number":5,"percentage":25,"currentValue":12345}]}}""");
        var window = Assert.Single(ProviderParsers.Glm(json.RootElement)); Assert.Equal(25, window.UsedPercent); Assert.Null(window.UsedCount); Assert.Null(window.Unit);
    }
    [Fact]
    public void ThresholdsTriggerOnlyOnCrossingFreshReadings()
    {
        var now = DateTimeOffset.Now; var tracker = new ThresholdTracker();
        var reading = new ProviderReading("codex", ReadingState.Ready, [new("five", "5 hours", 81)], now);
        Assert.Equal([80], tracker.Observe(reading, now)); Assert.Empty(tracker.Observe(reading, now));
        Assert.Empty(tracker.Observe(reading with { UpdatedAt = now.AddMinutes(-10), Windows = [new("five", "5 hours", 100)] }, now));
        Assert.Equal([100], tracker.Observe(reading with { Windows = [new("five", "5 hours", 100)] }, now));
        Assert.Empty(tracker.Observe(reading with { Windows = [new("five", "5 hours", 0)] }, now));
        Assert.Equal([80], tracker.Observe(reading, now));
    }
    [Theory]
    [InlineData("task_started", true)] [InlineData("task_complete", false)] [InlineData("turn_aborted", false)]
    public void OnlyExplicitCodexBoundariesCountAsActivity(string kind, bool expected)
    {
        var input = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "event_msg", timestamp = "2026-09-01T00:00:00Z", payload = new { type = kind } }));
        Assert.True(ActivityReader.TryCodex(input, out var active, out _)); Assert.Equal(expected, active);
    }
    [Fact]
    public void ToolOutputCannotSpoofActivity()
    {
        Assert.False(ActivityReader.TryCodex(Encoding.UTF8.GetBytes("""{"type":"response_item","timestamp":"2026-09-01T00:00:00Z","payload":{"type":"task_started"}}"""), out _, out _));
    }
    [Fact]
    public void UsageRingKeepsChosenAccentBelowItsWarningThresholds()
    {
        Assert.Equal("#3B9CFF", NotchGeometry.BandColor(49.999, "#3B9CFF"));
        Assert.Equal("#3B9CFF", NotchGeometry.BandColor(null, "#3B9CFF"));
        Assert.Equal("#F2FF00", NotchGeometry.BandColor(50, "#3B9CFF"));
        Assert.Equal("#FF3F00", NotchGeometry.BandColor(70, "#3B9CFF"));
    }
    [Fact]
    public void NotchPlacementRespectsTaskbarAndNegativeMonitorOrigins()
    {
        var area = new ScreenArea(-1920, 0, 1920, 1040);
        Assert.Equal((-82d, 420d), NotchGeometry.Place(area, 82, 200, NotchEdge.Right, 0));
        Assert.Equal((-1920d, 0d), NotchGeometry.Place(area, 82, 200, NotchEdge.Left, -10000));
        Assert.Equal((-1060d, 960d), NotchGeometry.Place(area, 200, 80, NotchEdge.Bottom, 0));
    }
    [Fact]
    public async Task HttpCredentialsStayOnOneFixedEndpointAndOutputIsBounded()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal("https://api.deepseek.com/user/balance", request.RequestUri?.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"balance_infos":[{"currency":"USD","total_balance":"12.50"}]}""") };
        });
        using var providers = new HttpProviders(handler);
        var reading = await providers.FetchAsync("deepseek", "test-key", TestContext.Current.CancellationToken);
        Assert.Equal(ReadingState.Ready, reading.State); Assert.Null(Assert.Single(reading.Windows).UsedPercent);
        Assert.DoesNotContain("test-key", JsonSerializer.Serialize(reading), StringComparison.Ordinal);
    }
    [Fact]
    public async Task MissingCredentialMakesNoRequest()
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("No HTTP request expected"));
        using var providers = new HttpProviders(handler);
        Assert.Equal(ReadingState.NeedsAuth, (await providers.FetchAsync("copilot", null, TestContext.Current.CancellationToken)).State);
    }
    [Fact]
    public void CompanionSnapshotIsAtomicAndProviderScoped()
    {
        var directory = Temp();
        try
        {
            var now = DateTimeOffset.Now;
            var snapshot = new CompanionSnapshot(1, now, [new("claude", "Claude Code", true, CompanionFile.Local(UsageSnapshot.Empty, now), new("claude", ReadingState.NeedsAuth, []))]);
            var path = Path.Combine(directory, "snapshot.json"); CompanionFile.Write(snapshot, path);
            var read = CompanionFile.Read(path); Assert.Equal("this-pc", Assert.Single(read.Providers).LocalUsage?.Scope);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "CodeRim.Tests." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static UsageEvent Claude(string id, long input, long read, long write, long output, string cwd = "C:\\work\\api") =>
        ClaudeJsonlParser.Parse(Encoding.UTF8.GetBytes(ClaudeLine(id, input, read, write, output, cwd)))!;
    private static string ClaudeLine(string id, long input, long read, long write, long output, string cwd = "C:\\work\\api") =>
        JsonSerializer.Serialize(new { type = "assistant", timestamp = "2026-09-01T00:00:00Z", sessionId = "session", cwd,
            message = new { id, role = "assistant", model = "claude-sonnet-4-5", content = "SECRET_PROMPT", usage = new { input_tokens = input, cache_read_input_tokens = read, cache_creation_input_tokens = write, output_tokens = output } } });
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
