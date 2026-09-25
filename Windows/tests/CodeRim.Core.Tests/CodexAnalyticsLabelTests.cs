using CodeRim.Core.Parsing;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Tests;

public sealed class CodexAnalyticsLabelTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "coderim-analytics-labels-" + Guid.NewGuid().ToString("N"));
    private const string First = "11111111-1111-4111-8111-111111111111";
    private const string Second = "22222222-2222-4222-8222-222222222222";
    private const string Third = "33333333-3333-4333-8333-333333333333";
    private string State => Path.Combine(directory, "state.sqlite");
    private string Desktop => Path.Combine(directory, "desktop.sqlite");
    public CodexAnalyticsLabelTests() => Directory.CreateDirectory(directory);
    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private void Schema() => Execute(State, """
        CREATE TABLE threads(id TEXT PRIMARY KEY, title TEXT, cwd TEXT, rollout_path TEXT, name TEXT, agent_nickname TEXT, project_id TEXT, source TEXT, archived INTEGER);
        CREATE TABLE projects(id TEXT PRIMARY KEY,name TEXT);
        """);
    private void Thread(string id, string? title = null, string? name = null, string cwd = "C:/workspace/Folder", string? project = null, string? nickname = null, int archived = 0)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = State, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO threads(id,title,cwd,rollout_path,name,agent_nickname,project_id,archived) VALUES($id,$title,$cwd,'unused',$name,$nickname,$project,$archived)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$cwd", cwd); command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$project", (object?)project ?? DBNull.Value); command.Parameters.AddWithValue("$nickname", (object?)nickname ?? DBNull.Value);
        command.Parameters.AddWithValue("$archived", archived); command.ExecuteNonQuery();
    }
    private IReadOnlyDictionary<string, CodexAnalyticsLabel> Read(params string[] ids) => CodexActivityCatalogue.ReadAnalyticsLabels(ids.Select(ClaudeJsonlParser.Hash).ToArray(), State, Desktop);

    [Fact]
    public void NameDesktopAndStoredTitlePrecedenceIncludesArchivedAndExcludesRemoteTitles()
    {
        Schema(); Thread(First, "Stored one", "Explicit name", project: "saved", archived: 1); Thread(Second, "Stored two"); Thread(Third, "Stored three");
        Execute(State, "INSERT INTO projects VALUES('saved','Saved project')");
        Execute(Desktop, $"""
            CREATE TABLE local_thread_catalog(host_id TEXT,thread_id TEXT,display_title TEXT,source_updated_at INTEGER);
            INSERT INTO local_thread_catalog VALUES('local','{First}','Desktop one',1),('local','{Second}','Desktop two',1),('remote','{Third}','Remote secret title',9);
            """);
        var beforeState = File.ReadAllBytes(State); var beforeDesktop = File.ReadAllBytes(Desktop);
        var labels = Read(First, Second, Third);
        Assert.Equal(3, labels.Count); Assert.Equal(new("Explicit name", "Saved project"), labels[ClaudeJsonlParser.Hash(First)]);
        Assert.Equal(new("Desktop two", "Folder"), labels[ClaudeJsonlParser.Hash(Second)]);
        Assert.Equal(new("Stored three", "Folder"), labels[ClaudeJsonlParser.Hash(Third)]);
        Assert.Equal(beforeState, File.ReadAllBytes(State)); Assert.Equal(beforeDesktop, File.ReadAllBytes(Desktop));
    }

    [Fact]
    public void GenericAndProjectlessNamesFollowReferenceWithoutInventingSessionTitles()
    {
        Schema(); Thread(First, "Real task", cwd: "C:/.codex"); Thread(Second, cwd: "C:/Documents/Codex/2026-09-26/Task", nickname: "Agent name");
        Thread(Third, cwd: "C:/workspace/codex", nickname: "Agent name");
        var labels = Read(First, Second, Third);
        Assert.Equal(new("Real task", "Real task"), labels[ClaudeJsonlParser.Hash(First)]);
        Assert.Equal(new(null, ""), labels[ClaudeJsonlParser.Hash(Second)]);
        Assert.Equal(new(null, "Agent name"), labels[ClaudeJsonlParser.Hash(Third)]);
        Assert.Empty(Read("unmatched-session"));
    }

    [Fact]
    public void UnsafeOrOversizedOptionalLabelsDoNotReplaceKnownMetadata()
    {
        Schema(); Thread(First, "Valid stored", new string('x', 161)); Thread(Second, "bad\nlabel", cwd: "C:/workspace/Folder");
        Thread(Third, "Codex", cwd: "C:/workspace/Folder");
        var labels = Read(First, Second, Third);
        Assert.Equal("Valid stored", labels[ClaudeJsonlParser.Hash(First)].SessionTitle);
        Assert.Null(labels[ClaudeJsonlParser.Hash(Second)].SessionTitle); Assert.Null(labels[ClaudeJsonlParser.Hash(Third)].SessionTitle);
    }

    [Fact]
    public void MissingOptionalDesktopAndProjectsLeaveLocalLabelsUsable()
    {
        Schema(); Thread(First, "Stored title", project: "missing"); Execute(State, "DROP TABLE projects");
        Assert.Equal(new("Stored title", "Folder"), Assert.Single(Read(First)).Value);
    }

    [Fact]
    public void MissingInvalidOrLinkedStateSafelyLeavesFallbackNames()
    {
        Assert.Empty(Read(First));
        Execute(State, $"CREATE VIEW threads AS SELECT '{First}' id,'fake' title,'fake' cwd,'fake' rollout_path");
        Assert.Empty(Read(First)); File.Delete(State);
        var target = Path.Combine(directory, "target.sqlite"); Execute(target, "CREATE TABLE example(id TEXT)");
        File.CreateSymbolicLink(State, target); Assert.Empty(Read(First));
    }

    [Fact]
    public void GeneratedIdentityColumnIsRejectedAndCancellationPropagates()
    {
        Execute(State, "CREATE TABLE threads(raw TEXT,id TEXT GENERATED ALWAYS AS (raw),title TEXT,cwd TEXT,rollout_path TEXT)");
        Assert.Empty(Read(First));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CodexActivityCatalogue.ReadAnalyticsLabels([ClaudeJsonlParser.Hash(First)], State, Desktop, cancellation.Token));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
