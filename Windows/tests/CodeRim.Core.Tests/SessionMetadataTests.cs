using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Core.Providers;
namespace CodeRim.Core.Tests;

public sealed class SessionMetadataTests
{
    [Fact]
    public async Task CountsImagesAndLinksAgentsWithoutPersistingContentsOrChangingTokens()
    {
        var root = Path.Combine(Path.GetTempPath(), "coderim-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var parent = Path.Combine(root, "parent.jsonl");
            File.WriteAllLines(parent, [
                """{"type":"session_meta","payload":{"id":"private-parent-id"}}""",
                """{"type":"response_item","timestamp":"2026-09-01T00:00:01Z","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"private-prompt"},{"type":"input_image","image_url":"private-image-data"},{"type":"input_image","image_url":"private-image-two"}]}}""",
                """{"type":"event_msg","timestamp":"2026-09-01T00:00:02Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":20},"last_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":20}}}}"""
            ]);
            File.WriteAllText(parent, File.ReadAllText(parent).Replace("private-image-data", "data:image/png;base64," + new string('a', 1024), StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "archive.jsonl"), "{}\n" + File.ReadAllText(parent).Replace("\":\"", "\" : \"", StringComparison.Ordinal));
            File.WriteAllLines(Path.Combine(root, "child.jsonl"), [
                """{"type":"session_meta","payload":{"id":"private-child-id","parent_thread_id":"private-parent-id","subagent_history_start_ordinal":0}}""",
                """{"type":"response_item","ordinal":1,"timestamp":"2026-09-01T00:00:02.5Z","payload":{"type":"message","role":"user","content":[{"type":"input_image","image_url":"private-child-image"}]}}""",
                """{"type":"event_msg","ordinal":2,"timestamp":"2026-09-01T00:00:03Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":50,"cached_input_tokens":0,"output_tokens":10},"last_token_usage":{"input_tokens":50,"cached_input_tokens":0,"output_tokens":10}}}}"""
            ]);
            var scan = await new UsageScanner([root]).ScanAsync(WeekStart.Monday, TestContext.Current.CancellationToken);
            Assert.Equal(180, scan.Snapshot.AllTime.TotalTokens);
            var database = Path.Combine(root, "history.sqlite");
            var repository = new UsageRepository(database);
            repository.Merge("codex", scan.Events, scan.Sessions);
            repository.Merge("codex", scan.Events, scan.Sessions);
            var details = repository.ReadSessionDetails("codex");
            Assert.Equal(2, details.Count);
            var owner = Assert.Single(details, x => x.ParentId is null);
            Assert.Equal(2, owner.Attachments.Sum(x => x.Count));
            var child = Assert.Single(details, x => x.ParentId is not null);
            Assert.Equal(owner.Id, child.ParentId);
            Assert.Equal(1, Assert.Single(child.Attachments).Count);
            Assert.Equal(180, repository.Read("codex").Sum(x => x.Usage.TotalTokens));
            var stored = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(database));
            Assert.DoesNotContain("private-", stored, StringComparison.Ordinal);

            repository.Clear("codex", DateTimeOffset.Parse("2026-09-01T00:00:02Z", System.Globalization.CultureInfo.InvariantCulture));
            repository.Merge("codex", scan.Events, scan.Sessions);
            Assert.Empty(repository.ReadSessionDetails("codex").SelectMany(x => x.Attachments));
            Assert.Empty(repository.Read("codex"));
            var freshTime = DateTimeOffset.Parse("2026-09-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            repository.Merge("codex", [new UsageEvent("fresh", freshTime, new(50, 0, 10), SessionId: owner.Id)],
                [new SessionDetails(owner.Id, null, [new("fresh-image", freshTime, 1)])]);
            Assert.Equal(60, Assert.Single(repository.Read("codex")).Usage.TotalTokens);
            Assert.Equal(1, repository.ReadSessionDetails("codex").SelectMany(x => x.Attachments).Sum(x => x.Count));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("codex", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "codex://threads/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("codex", "../settings", null)]
    [InlineData("codex", "https://example.invalid", null)]
    [InlineData("claude", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", null)]
    public void OnlyCodexUuidSessionsCreateDeepLinks(string provider, string id, string? expected)
    {
        var session = new SessionActivity("fixture", provider, "Fixture", "idle", DateTimeOffset.UnixEpoch) { CodexThreadId = id };
        Assert.Equal(expected, session.CodexThreadUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{"rateLimitResetCredits":{"availableCount":0}}""", 0L, null)]
    [InlineData("""{"rateLimitResetCredits":{"count":3}}""", 3L, null)]
    [InlineData("""{"rateLimitResetCredits":{"unlimited":true}}""", null, "Unlimited resets")]
    public void PreservesResetCreditsAsResetsNotTokens(string json, long? count, string? display)
    {
        using var document = JsonDocument.Parse(json);
        var credit = Assert.Single(ProviderParsers.Codex(document.RootElement));
        Assert.Equal("rate-limit-reset-credits", credit.Id);
        Assert.Equal(count, credit.RemainingCount);
        Assert.Equal(display, credit.DisplayValue);
        Assert.Equal("resets", credit.Unit);
        Assert.Null(credit.UsedPercent);
    }
}
