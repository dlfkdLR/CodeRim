using System.Text.Json;
using CodeRim.Core.Parsing;
using CodeRim.Core.Services;
using CodeRim.Core.Domain;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Tests;

public sealed class ActivityGroupsTests
{
    private const string Parent = "11111111-1111-4111-8111-111111111111";
    private const string Child = "22222222-2222-4222-8222-222222222222";
    private const string Grandchild = "33333333-3333-4333-8333-333333333333";
    private static SessionActivity Task(string id, string state = "busy", string? parent = null, string? host = null) =>
        new(id + host, "codex", "Project", state, DateTimeOffset.UnixEpoch) {
            CodexThreadId = id, ParentThreadId = parent, ParentThreadTitle = "Main task", Detail = "Task " + id[..8], RemoteHostId = host,
            UsageSessionId = ClaudeJsonlParser.Hash(id) };

    [Fact]
    public void FinishedParentContextOwnsItsChildrenWithoutInventingLiveActivity()
    {
        var child = Task(Child, parent: Parent);
        var group = Assert.Single(SessionPresentation.Groups([child]));
        Assert.True(group.Parent.ContextOnly); Assert.Equal(Parent, group.Parent.Session.CodexThreadId);
        Assert.Equal("Main task", group.Parent.Session.Detail); Assert.Equal(DateTimeOffset.MinValue, group.Parent.Session.Since);
        Assert.Equal(child, Assert.Single(group.Children).Session); Assert.Equal(1, group.Children[0].Depth);
        var totals = SessionPresentation.TokenTotals([group.Parent.Session, child], "codex",
            [new("parent", DateTimeOffset.UnixEpoch, new(10, 0, 1), SessionId: ClaudeJsonlParser.Hash(Parent)),
             new("child", DateTimeOffset.UnixEpoch, new(20, 0, 2), SessionId: ClaudeJsonlParser.Hash(Child))],
            [new(ClaudeJsonlParser.Hash(Child), ClaudeJsonlParser.Hash(Parent), [])], TestContext.Current.CancellationToken);
        Assert.Equal(33, Assert.Single(totals).Value); Assert.Contains(group.Parent.Session.Id, totals.Keys);
    }
    [Fact]
    public void ChildrenSetGroupPriorityAndHostIdentityKeepsDifferentMachinesSeparate()
    {
        var groups = SessionPresentation.Groups([Task(Parent, "idle"), Task(Child, "waiting", Parent), Task(Grandchild), Task(Parent, host: "ssh:other")]);
        Assert.Equal(3, groups.Count); Assert.Equal(Parent, groups[0].Parent.Session.Id);
        Assert.False(groups[0].Parent.ContextOnly); Assert.Single(groups[0].Children);
        Assert.Empty(groups.Single(x => x.Parent.Session.RemoteHostId is not null).Children);
    }
    [Fact]
    public void CyclesDuplicatesAndSelfParentsCannotHideTasksOrRecurseForever()
    {
        var a = Task(Parent, parent: Child); var b = Task(Child, parent: Parent); var c = Task(Grandchild, parent: Grandchild);
        var rows = SessionPresentation.Groups([a, a, b, c]).SelectMany(x => new[] { x.Parent }.Concat(x.Children)).ToArray();
        Assert.Equal(3, rows.Length); Assert.Equal(3, rows.Select(x => x.Session.Id).Distinct().Count());
        Assert.DoesNotContain(rows, x => x.ContextOnly);
    }
    [Fact]
    public void ContextParentKeepsTheImportersRawUsageIdentity()
    {
        const string uppercase = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA";
        var parent = Assert.Single(SessionPresentation.Groups([Task(Child, parent: uppercase)])).Parent;
        Assert.Equal(ClaudeJsonlParser.Hash(uppercase), parent.Session.UsageSessionId);
        Assert.Equal(uppercase.ToLowerInvariant(), parent.Session.CodexThreadId);
    }
    [Fact]
    public void RemoteContextNeverAcquiresLocalUsageIdentity()
    {
        var parent = Assert.Single(SessionPresentation.Groups([Task(Child, parent: Parent, host: "ssh:other")])).Parent;
        Assert.True(parent.ContextOnly); Assert.Null(parent.Session.UsageSessionId); Assert.Equal("ssh:other", parent.Session.RemoteHostId);
    }
    [Fact]
    public void CatalogueResolvesArchivedAncestorsButNeverChangesStateOrWritesTheDatabase()
    {
        WithDatabase((connection, path) =>
        {
            Insert(connection, Parent, "Main saved task", null, name: "Renamed task");
            Insert(connection, Child, "Finished intermediate", Parent);
            Insert(connection, Grandchild, "Active agent", Child);
            // Finish the fixture writer before taking byte-for-byte snapshots. Windows
            // correctly refuses File.ReadAllBytes while SQLite holds its write handle.
            connection.Close();
            var before = File.ReadAllBytes(path); var original = Task(Grandchild, "waiting");
            var enriched = Assert.Single(CodexActivityCatalogue.Enrich([original], path, TestContext.Current.CancellationToken));
            Assert.Equal("Project", enriched.Name); Assert.Equal("Active agent", enriched.Detail);
            Assert.Equal(Parent, enriched.ParentThreadId); Assert.Equal("Renamed task", enriched.ParentThreadTitle);
            Assert.Equal(original.State, enriched.State); Assert.Equal(original.Since, enriched.Since); Assert.Equal(original.UsageSessionId, enriched.UsageSessionId);
            Assert.Equal(before, File.ReadAllBytes(path));
            var remote = original with { RemoteHostId = "remote" };
            Assert.Equal(remote, Assert.Single(CodexActivityCatalogue.Enrich([remote], path, TestContext.Current.CancellationToken)));
        });
    }
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"not-an-id\"}}}")]
    [InlineData("{\"subagent\":[]}")]
    public void MalformedParentMetadataKeepsTheTaskVisible(string source)
    {
        WithDatabase((connection, path) =>
        {
            Insert(connection, Child, "Visible", null, sourceOverride: source);
            var task = Assert.Single(CodexActivityCatalogue.Enrich([Task(Child)], path, TestContext.Current.CancellationToken));
            Assert.Null(task.ParentThreadId); Assert.Equal("Visible", task.Detail);
        });
    }
    [Fact]
    public void CatalogueCycleOrViewCannotSupplyAFalseParent()
    {
        WithDatabase((connection, path) =>
        {
            Insert(connection, Parent, "A", Child); Insert(connection, Child, "B", Parent);
            Assert.Null(Assert.Single(CodexActivityCatalogue.Enrich([Task(Child)], path)).ParentThreadId);
            using var command = connection.CreateCommand(); command.CommandText = "ALTER TABLE threads RENAME TO stored; CREATE VIEW threads AS SELECT * FROM stored"; command.ExecuteNonQuery();
            var original = Task(Child); Assert.Equal(original, Assert.Single(CodexActivityCatalogue.Enrich([original], path)));
        });
    }
    private static void Insert(SqliteConnection connection, string id, string title, string? parent, string? name = null, string? sourceOverride = null)
    {
        using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO threads VALUES($id,$title,'C:\\Work\\Project',$name,$source)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$title", title); command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", sourceOverride ?? JsonSerializer.Serialize(new { subagent = new { thread_spawn = new { parent_thread_id = parent } } })); command.ExecuteNonQuery();
    }
    private static void WithDatabase(Action<SqliteConnection, string> test)
    {
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "coderim-task-groups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "state.sqlite");
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); connection.Open();
            using var schema = connection.CreateCommand(); schema.CommandText = "CREATE TABLE threads(id TEXT PRIMARY KEY,title TEXT,cwd TEXT,name TEXT,source TEXT)"; schema.ExecuteNonQuery();
            test(connection, path);
        }
        finally { Directory.Delete(directory, true); }
    }
}
