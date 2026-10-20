using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Parsing;
using System.Text;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class ClaudeAgentImportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "coderim-claude-agents-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(directory, "sources");
    public ClaudeAgentImportTests() => Directory.CreateDirectory(Source);
    private static string Row(string id, int input, string? agent = null, string session = "private-parent") => JsonSerializer.Serialize(new {
        type = "assistant", timestamp = "2026-09-01T01:00:00Z", sessionId = session, agentId = agent, cwd = "C:/private-directory/Reference",
        message = new { id, role = "assistant", model = "claude-sonnet-4-6", usage = new { input_tokens = input, output_tokens = 0 } } });
    private async Task<ScanResult> Scan() => await new UsageScanner("claude", [Source]).ScanAsync(WeekStart.Monday, TestContext.Current.CancellationToken);

    [Fact]
    public async Task FilenameAndExplicitAgentIdentityAgreeWithoutDuplicatingTokens()
    {
        File.WriteAllText(Path.Combine(Source, "parent.jsonl"), Row("parent-message", 100) + "\n");
        File.WriteAllLines(Path.Combine(Source, "agent-child.jsonl"), [Row("child-message", 50), Row("child-message", 50, "child"), Row("child-message", 50, "agent-child")]);
        var scan = await Scan();
        Assert.Equal(150, scan.Snapshot.AllTime.TotalTokens); Assert.Equal(2, scan.Events.Count); Assert.Equal(2, scan.Sessions.Count);
        var parent = Assert.Single(scan.Sessions, x => x.ParentId is null); var child = Assert.Single(scan.Sessions, x => x.ParentId is not null);
        Assert.Equal(parent.Id, child.ParentId); Assert.NotEqual(parent.Id, child.Id);
        Assert.Equal(100, Assert.Single(scan.Events, x => x.SessionId == parent.Id).Usage.TotalTokens);
        Assert.Equal(50, Assert.Single(scan.Events, x => x.SessionId == child.Id).Usage.TotalTokens);
        var repository = new UsageRepository(Path.Combine(directory, "usage.sqlite")); repository.Merge("claude", scan.Events, scan.Sessions);
        repository.Merge("claude", scan.Events, scan.Sessions);
        Assert.Equal(150, repository.Read("claude").Sum(x => x.Usage.TotalTokens));
        Assert.Equal(parent.Id, Assert.Single(repository.ReadSessionDetails("claude"), x => x.Id == child.Id).ParentId);
    }

    [Theory]
    [InlineData("0-parent.jsonl", "agent-child.jsonl")]
    [InlineData("z-parent.jsonl", "agent-child.jsonl")]
    public async Task CopiedParentHistoryCannotClaimChildRegardlessOfScanOrder(string parentName, string childName)
    {
        File.WriteAllLines(Path.Combine(Source, parentName), [Row("parent-message", 100), Row("copied-child", 50)]);
        File.WriteAllText(Path.Combine(Source, childName), Row("copied-child", 50) + "\n");
        var scan = await Scan();
        Assert.Equal(150, scan.Snapshot.AllTime.TotalTokens); Assert.Equal(2, scan.Sessions.Count);
        var child = Assert.Single(scan.Sessions, x => x.ParentId is not null);
        Assert.Equal(50, Assert.Single(scan.Events, x => x.SessionId == child.Id).Usage.TotalTokens);
        Assert.Equal(100, Assert.Single(scan.Events, x => x.SessionId == child.ParentId).Usage.TotalTokens);
    }

    private static UsageEvent Parsed(string id, int input, string? agent = null, string parent = "private-parent", string? source = null) =>
        ClaudeJsonlParser.Parse(Encoding.UTF8.GetBytes(Row(id, input, agent, parent)), sourceName: source)!;

    [Fact]
    public void ExplicitAgentOverridesFilenameAndScopesIdentityToParent()
    {
        var explicitAgent = Parsed("one", 1, "selected", source: "agent-fallback");
        Assert.Equal(explicitAgent.SessionId, Parsed("two", 1, "agent-selected").SessionId);
        Assert.NotEqual(explicitAgent.SessionId, Parsed("three", 1, "selected", "other-parent").SessionId);
        Assert.NotEqual(explicitAgent.SessionId, Parsed("four", 1, source: "agent-fallback").SessionId);
        Assert.Equal(Parsed("parent", 1).SessionId, explicitAgent.ImportParentSessionId);
        Assert.DoesNotContain("ImportParentSessionId", JsonSerializer.Serialize(explicitAgent), StringComparison.Ordinal);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(explicitAgent), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../invalid")]
    [InlineData(" ")]
    [InlineData("agent/child")]
    [InlineData("\uD83D\uDE42")]
    public void MalformedAgentFieldIsIgnoredAndValidTranscriptNameStillWorks(string invalid)
    {
        Assert.Equal(Parsed("parent", 1).SessionId, Parsed("bad", 1, invalid).SessionId);
        Assert.Equal(Parsed("child", 1, "child").SessionId, Parsed("fallback", 1, invalid, source: "agent-child").SessionId);
    }

    [Fact]
    public void ExistingParentOwnedHistoryMovesOnceToChildAndRecomputesRetainedMetadata()
    {
        var repository = new UsageRepository(Path.Combine(directory, "usage.sqlite"));
        var parent = Parsed("parent", 100) with { OccurredAt = DateTimeOffset.Parse("2026-09-02T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture) };
        var legacy = Parsed("child", 50) with { Project = "Parent copy project", ProjectId = "parent-copy-id" };
        repository.Merge("claude", [parent, legacy], [new(parent.SessionId, null, [])]);
        Assert.Equal(legacy.OccurredAt, Assert.Single(repository.ReadSessionDetails("claude")).StartedAt);
        var child = Parsed("child", 50, "child") with { Project = "Actual child project", ProjectId = "child-project-id", Model = "claude-opus-4-6" };
        // No parent source is rescanned here; import ownership itself must update
        // the retained parent's metadata as well as the promoted child.
        repository.Merge("claude", [child]); repository.Merge("claude", [legacy]);
        var restored = new UsageRepository(Path.Combine(directory, "usage.sqlite")); var events = restored.Read("claude");
        Assert.Equal(150, events.Sum(x => x.Usage.TotalTokens)); Assert.Equal(2, events.Count);
        Assert.Equal(50, Assert.Single(events, x => x.SessionId == child.SessionId).Usage.TotalTokens);
        var migrated = Assert.Single(events, x => x.SessionId == child.SessionId);
        Assert.Equal("Actual child project", migrated.Project); Assert.Equal("child-project-id", migrated.ProjectId); Assert.Equal("claude-opus-4-6", migrated.Model);
        var fresh = new UsageRepository(Path.Combine(directory, "fresh.sqlite")); fresh.Merge("claude", [ClaudeJsonlParser.Merge(legacy, child)]);
        Assert.Equal(Assert.Single(fresh.Read("claude")), migrated);
        var details = restored.ReadSessionDetails("claude");
        Assert.Equal(parent.OccurredAt, Assert.Single(details, x => x.Id == parent.SessionId).StartedAt);
        Assert.Equal("Reference", Assert.Single(details, x => x.Id == parent.SessionId).ProjectName);
        Assert.Equal(parent.SessionId, Assert.Single(details, x => x.Id == child.SessionId).ParentId);
        restored.Clear("claude", parent.OccurredAt.AddSeconds(1));
        restored.Merge("claude", [child with { OccurredAt = parent.OccurredAt.AddDays(1) }]);
        Assert.Empty(restored.Read("claude"));
    }

    [Fact]
    public void ChildOwnershipCannotReassignAnUnrelatedSessionOnMessageCollision()
    {
        var unrelated = Parsed("collision", 10, parent: "unrelated"); var child = Parsed("collision", 10, "child");
        Assert.Equal(unrelated.SessionId, ClaudeJsonlParser.Merge(unrelated, child).SessionId);
        var repository = new UsageRepository(Path.Combine(directory, "usage.sqlite"));
        repository.Merge("claude", [unrelated]); repository.Merge("claude", [child]);
        Assert.Equal(unrelated.SessionId, Assert.Single(repository.Read("claude")).SessionId);
    }

    [Fact]
    public void ParentIdentifierCannotCollideWithDerivedChildNamespace()
    {
        var child = Parsed("child", 1, "child", "victim");
        var malformed = ClaudeJsonlParser.Parse(Encoding.UTF8.GetBytes(Row("unrelated", 1, session: "claude-agent|victim|agent-child")));
        Assert.NotNull(child); Assert.Null(malformed);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
