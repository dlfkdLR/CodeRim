using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Tests;

public sealed class SessionMetadataPersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "coderim-session-details-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_788_220_800);
    private string Database => Path.Combine(directory, "usage.sqlite");
    public SessionMetadataPersistenceTests() => Directory.CreateDirectory(directory);
    private static UsageEvent Event(string id, string provider = "codex", int hour = 1, string name = "Project") =>
        new(id, Start.AddHours(hour), new(100, 0, 10, 0), "gpt-5.6-sol", name, "session", provider, "project-id");
    private static SessionDetails Metadata(string name = "Project", int hour = 0) => new("session", "parent", []) {
        StartedAt = Start, ProjectName = name, ProjectObservedAt = Start.AddHours(hour) };

    [Fact]
    public async Task CodexRetainsInitialStartAndLastContextEvenWithoutUsageInThatContext()
    {
        var source = Path.Combine(directory, "source"); Directory.CreateDirectory(source);
        File.WriteAllLines(Path.Combine(source, "session.jsonl"), [
            """{"type":"session_meta","timestamp":"2026-09-01T00:00:00Z","payload":{"id":"private-session","cwd":"C:/private-parent/Initial"}}""",
            """{"type":"event_msg","timestamp":"2026-09-01T01:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":10},"last_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":10}}}}""",
            """{"type":"turn_context","timestamp":"2026-09-02T00:00:00Z","payload":{"cwd":"C:/private-parent/Renamed"}}""",
            """{"type":"session_meta","timestamp":"2026-09-03T00:00:00Z","payload":{"id":"private-replayed-session","cwd":"C:/private-parent/Replayed"}}""",
            """{"type":"unrelated","payload":{"cwd":"C:/private-parent/Injected","text":"session_meta"}}"""
        ]);
        var scan = await new UsageScanner([source]).ScanAsync(WeekStart.Monday, TestContext.Current.CancellationToken);
        var metadata = Assert.Single(scan.Sessions);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), metadata.StartedAt);
        Assert.Equal("Renamed", metadata.ProjectName);
        Assert.Equal(metadata.StartedAt.GetValueOrDefault().AddDays(1), metadata.ProjectObservedAt);
        Assert.Equal("Initial", Assert.Single(scan.Events).Project);
        Assert.Equal(110, scan.Snapshot.AllTime.TotalTokens);
        var repository = new UsageRepository(Database); repository.Merge("codex", scan.Events, scan.Sessions);
        var restored = Assert.Single(new UsageRepository(Database).ReadSessionDetails("codex"));
        Assert.Equal(metadata.Id, restored.Id); Assert.Equal(metadata.StartedAt, restored.StartedAt);
        Assert.Equal(metadata.ProjectName, restored.ProjectName); Assert.Equal(metadata.ProjectObservedAt, restored.ProjectObservedAt);
        foreach (var path in Directory.GetFiles(directory, "usage.sqlite*"))
        {
            var bytes = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("private-", bytes, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExistingSchemaRetainsLinksImagesAndTokensWithoutInventingStart()
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE session_links(provider TEXT NOT NULL,id TEXT NOT NULL,parentId TEXT,PRIMARY KEY(provider,id));
                INSERT INTO session_links VALUES('codex','session','parent');
                CREATE TABLE attachments(provider TEXT NOT NULL,id TEXT NOT NULL,session TEXT NOT NULL,time INTEGER NOT NULL,count INTEGER NOT NULL,PRIMARY KEY(provider,id));
                INSERT INTO attachments VALUES('codex','image','session',1000,2);
                """;
            command.ExecuteNonQuery();
        }
        var repository = new UsageRepository(Database);
        var old = Assert.Single(repository.ReadSessionDetails("codex"));
        Assert.Equal("parent", old.ParentId); Assert.Equal(2, Assert.Single(old.Attachments).Count);
        Assert.Null(old.StartedAt); Assert.Null(old.ProjectName);
        repository.Merge("codex", [Event("token")], [Metadata()]);
        var current = Assert.Single(new UsageRepository(Database).ReadSessionDetails("codex"));
        Assert.Equal(Start, current.StartedAt); Assert.Equal("Project", current.ProjectName);
        Assert.Equal(110, Assert.Single(repository.Read("codex")).Usage.TotalTokens);
        Assert.Equal(2, Assert.Single(current.Attachments).Count);
    }

    [Fact]
    public void OldOrMissingMetadataCannotEraseNewNameOrOriginalCodexStart()
    {
        var repository = new UsageRepository(Database);
        repository.Merge("codex", [Event("token")], [Metadata("New name", 4)]);
        repository.Merge("codex", [], [Metadata("Old name", 1) with { StartedAt = Start.AddDays(1) }, new("session", null, [])]);
        var restored = Assert.Single(new UsageRepository(Database).ReadSessionDetails("codex"));
        Assert.Equal("New name", restored.ProjectName); Assert.Equal(Start.AddHours(4), restored.ProjectObservedAt);
        Assert.Equal(Start, restored.StartedAt); Assert.Equal("parent", restored.ParentId);
        repository.Merge("codex", [], [Metadata("Newest name", 5)]);
        Assert.Equal("Newest name", Assert.Single(repository.ReadSessionDetails("codex")).ProjectName);
    }

    [Fact]
    public void EqualTimeMetadataIsStableAcrossInputOrderAndProvidersStaySeparate()
    {
        var repository = new UsageRepository(Database);
        repository.Merge("codex", [], [Metadata("Zebra"), Metadata("Apple")]);
        repository.Merge("codex", [], [Metadata("Apple"), Metadata("Zebra")]);
        repository.Merge("claude", [Event("claude", "claude", 8, "Claude project")], [Metadata("Ignored scanned name")]);
        Assert.Equal("Apple", Assert.Single(repository.ReadSessionDetails("codex")).ProjectName);
        var claude = Assert.Single(repository.ReadSessionDetails("claude"));
        Assert.Equal("Claude project", claude.ProjectName); Assert.Equal(Start.AddHours(8), claude.StartedAt);
        repository.Merge("claude", [Event("older-claude", "claude", 2, "Earlier")], [Metadata()]);
        claude = Assert.Single(repository.ReadSessionDetails("claude"));
        Assert.Equal(Start.AddHours(2), claude.StartedAt); Assert.Equal("Claude project", claude.ProjectName);
    }

    [Fact]
    public void RebuildRollbackAndClearCoverMetadataWithoutCrossProviderDeletion()
    {
        var repository = new UsageRepository(Database);
        repository.Merge("codex", [Event("original")], [Metadata()]);
        repository.Merge("claude", [Event("claude", "claude")], [Metadata()]);
        static IEnumerable<SessionDetails> Broken()
        {
            yield return Metadata("Replacement"); throw new IOException("fixture failed metadata enumeration");
        }
        Assert.Throws<IOException>(() => repository.Rebuild("codex", [Event("replacement")], Broken()));
        Assert.Equal("original", Assert.Single(repository.Read("codex")).EventKey);
        Assert.Equal("Project", Assert.Single(repository.ReadSessionDetails("codex")).ProjectName);
        repository.Rebuild("codex", [], []);
        Assert.Empty(repository.ReadSessionDetails("codex")); Assert.Single(repository.ReadSessionDetails("claude"));
        repository.Merge("codex", [Event("fresh")], [Metadata()]); repository.Clear("codex", Start.AddHours(4));
        Assert.Empty(repository.ReadSessionDetails("codex")); Assert.Single(repository.ReadSessionDetails("claude"));
    }

    [Fact]
    public void ClaudeMetadataUsesOnlyRetainedUsageAfterClear()
    {
        var repository = new UsageRepository(Database);
        repository.Merge("claude", [Event("old", "claude")], [Metadata()]);
        repository.Clear("claude", Start.AddHours(4));
        repository.Merge("claude", [Event("old", "claude"), Event("fresh", "claude", 8, "New project")], [Metadata("Old project")]);
        var restored = Assert.Single(repository.ReadSessionDetails("claude"));
        Assert.Equal(Start.AddHours(8), restored.StartedAt); Assert.Equal("New project", restored.ProjectName);
        Assert.Equal(110, Assert.Single(repository.Read("claude")).Usage.TotalTokens);
    }

    [Fact]
    public async Task ClaudeScannerProjectsEarliestRecordedUsageAndLatestKnownFolder()
    {
        File.WriteAllLines(Path.Combine(directory, "session.jsonl"), [
            """{"type":"assistant","sessionId":"private-session","timestamp":"2026-09-02T00:00:00Z","cwd":"C:/private-parent/New","message":{"id":"new","role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":100,"output_tokens":10}}}""",
            """{"type":"assistant","sessionId":"private-session","timestamp":"2026-09-01T00:00:00Z","cwd":"C:/private-parent/Old","message":{"id":"old","role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":100,"output_tokens":10}}}"""
        ]);
        var scan = await new UsageScanner("claude", [directory]).ScanAsync(WeekStart.Monday, TestContext.Current.CancellationToken);
        var details = Assert.Single(scan.Sessions);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), details.StartedAt);
        Assert.Equal("New", details.ProjectName); Assert.Equal(details.StartedAt.GetValueOrDefault().AddDays(1), details.ProjectObservedAt);
        Assert.Equal(220, scan.Snapshot.AllTime.TotalTokens);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
