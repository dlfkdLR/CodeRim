using System.Text.Json;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodeRim.Core.Tests;

public sealed partial class ActivityGroupsTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CatalogueUuidCasingDoesNotLoseParentOrDesktopTitles(bool upperStored, bool upperLive)
    {
        const string parent = "abcdefab-1111-4111-8111-abcdefabcdef";
        const string child = "bcdefabc-2222-4222-8222-bcdefabcdefa";
        static string Casing(string value, bool upper) => upper ? value.ToUpperInvariant() : value;
        WithDatabase((connection, path) =>
        {
            Insert(connection, Casing(parent, upperStored), "Stored parent", null);
            Insert(connection, Casing(child, upperStored), "Stored child", Casing(parent, !upperStored));
            connection.Close();
            var desktop = path + ".desktop";
            using (var catalogue = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = desktop, Pooling = false }.ToString()))
            {
                catalogue.Open(); using var command = catalogue.CreateCommand();
                command.CommandText = "CREATE TABLE local_thread_catalog(host_id TEXT,thread_id TEXT,display_title TEXT,source_updated_at REAL); INSERT INTO local_thread_catalog VALUES('local',$id,'Desktop parent',1)";
                command.Parameters.AddWithValue("$id", Casing(parent, upperStored)); command.ExecuteNonQuery();
            }
            var stateBefore = File.ReadAllBytes(path); var desktopBefore = File.ReadAllBytes(desktop);
            var live = Task(Casing(child, upperLive), "waiting");
            var row = Assert.Single(CodexActivityCatalogue.Enrich([live], path, desktop, TestContext.Current.CancellationToken));
            Assert.Equal("Stored child", row.Detail);
            Assert.Equal(parent, row.ParentThreadId, ignoreCase: true);
            Assert.Equal("Desktop parent", row.ParentThreadTitle);
            Assert.Equal((live.Id, live.UsageSessionId, live.State, live.Since), (row.Id, row.UsageSessionId, row.State, row.Since));
            Assert.Equal(stateBefore, File.ReadAllBytes(path)); Assert.Equal(desktopBefore, File.ReadAllBytes(desktop));
        });
    }

    [Fact]
    public void ConflictingCatalogueUuidSpellingsRetainOriginalActivity()
    {
        const string id = "abcdefab-1111-4111-8111-abcdefabcdef";
        WithDatabase((connection, path) =>
        {
            Insert(connection, id, "First task", null);
            Insert(connection, id.ToUpperInvariant(), "Conflicting task", null);
            var original = Task(id);
            Assert.Equal(original, Assert.Single(CodexActivityCatalogue.Enrich([original], path, TestContext.Current.CancellationToken)));
        });
    }

    [Fact]
    public void DisplayMetadataUsesSavedNamesAndLocalDesktopTitlesWithoutChangingActivity()
    {
        WithDatabase((connection, path) =>
        {
            Insert(connection, Parent, "Old parent title", null, name: "Explicit task name");
            Insert(connection, Child, "Old child title", Parent);
            Insert(connection, Grandchild, "codex", Parent, sourceOverride: JsonSerializer.Serialize(new {
                subagent = new { thread_spawn = new { parent_thread_id = Parent, agent_nickname = "Ada" } } }));
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    ALTER TABLE threads ADD COLUMN project_id TEXT;
                    CREATE TABLE projects(id TEXT PRIMARY KEY,name TEXT);
                    INSERT INTO projects VALUES('saved-project','Saved project name');
                    UPDATE threads SET project_id='saved-project';
                    """;
                command.ExecuteNonQuery();
            }
            connection.Close();
            var desktop = path + ".desktop";
            using (var catalog = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = desktop, Pooling = false }.ToString()))
            {
                catalog.Open();
                using var schema = catalog.CreateCommand();
                schema.CommandText = "CREATE TABLE local_thread_catalog(host_id TEXT,thread_id TEXT,display_title TEXT,source_updated_at REAL)";
                schema.ExecuteNonQuery();
                void Add(string host, string id, string title, int time)
                {
                    using var insert = catalog.CreateCommand(); insert.CommandText = "INSERT INTO local_thread_catalog VALUES($host,$id,$title,$time)";
                    insert.Parameters.AddWithValue("$host", host); insert.Parameters.AddWithValue("$id", id);
                    insert.Parameters.AddWithValue("$title", title); insert.Parameters.AddWithValue("$time", time); insert.ExecuteNonQuery();
                }
                Add("local", Parent, "Desktop parent", 2);
                Add("local", Child, "Earlier desktop title", 1);
                Add("local", Child, "Renamed desktop task", 2);
                Add("remote-host", Child, "Foreign host collision", 3);
                Add("remote-host", Grandchild, "Another remote task", 3);
            }
            var before = File.ReadAllBytes(path); var desktopBefore = File.ReadAllBytes(desktop);
            var input = new[] { Task(Parent), Task(Child, "waiting"), Task(Grandchild) };
            var rows = CodexActivityCatalogue.Enrich(input, path, desktop, TestContext.Current.CancellationToken);
            Assert.Equal(["Explicit task name", "Renamed desktop task", "Ada"], rows.Select(x => x.Detail));
            Assert.All(rows, x => Assert.Equal("Saved project name", x.Name));
            Assert.Equal("Explicit task name", rows[1].ParentThreadTitle);
            Assert.Equal(input.Select(x => (x.State, x.Since, x.UsageSessionId)), rows.Select(x => (x.State, x.Since, x.UsageSessionId)));
            Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(desktopBefore, File.ReadAllBytes(desktop));
            File.Delete(desktop);
            rows = CodexActivityCatalogue.Enrich(input, path, desktop, TestContext.Current.CancellationToken);
            Assert.Equal("Old child title", rows[1].Detail);
        });
    }

    [Theory]
    [InlineData("C:\\Users\\me\\Documents\\Codex\\2026-09-21\\new-chat", "")]
    [InlineData("/Users/me/Documents/Codex/2026-09-21/unf", "")]
    [InlineData("C:\\Users\\me\\projects\\unf", "unf")]
    [InlineData("/Users/me/Documents/Codex/repo", "repo")]
    [InlineData("/Users/me/Documents/Codex/2026-09-21/chat/work/real-project", "real-project")]
    [InlineData("C:\\Users\\me\\.codex", "Visible task")]
    [InlineData("C:\\Users\\me\\Documents\\Codex\\2026-9-21\\short-date", "short-date")]
    public void ProjectlessWorkspacesDoNotAppearAsSavedProjects(string cwd, string expected)
    {
        WithDatabase((connection, path) =>
        {
            Insert(connection, Child, "Visible task", null);
            using var update = connection.CreateCommand(); update.CommandText = "UPDATE threads SET cwd=$cwd";
            update.Parameters.AddWithValue("$cwd", cwd); update.ExecuteNonQuery();
            var row = Assert.Single(CodexActivityCatalogue.Enrich([Task(Child)], path, TestContext.Current.CancellationToken));
            Assert.Equal(expected, row.Name); Assert.Equal("Visible task", row.Detail);
        });
    }

    [Fact]
    public void UntrustedOptionalTablesCannotReplaceSafeTaskNames()
    {
        WithDatabase((connection, path) =>
        {
            Insert(connection, Child, "Original task", null);
            using var setup = connection.CreateCommand();
            setup.CommandText = "ALTER TABLE threads ADD COLUMN project_id TEXT; UPDATE threads SET project_id='project'; CREATE VIEW projects AS SELECT 'project' AS id,'Not a stored project' AS name";
            setup.ExecuteNonQuery();
            var desktop = path + ".desktop";
            using (var catalog = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = desktop, Pooling = false }.ToString()))
            {
                catalog.Open(); using var schema = catalog.CreateCommand();
                // The only interpolated value is the fixed synthetic UUID above.
                schema.CommandText = "CREATE VIEW local_thread_catalog AS SELECT 'local' AS host_id,'" + Child + "' AS thread_id,'Unsafe title' AS display_title,1 AS source_updated_at";
                schema.ExecuteNonQuery();
            }
            var row = Assert.Single(CodexActivityCatalogue.Enrich([Task(Child, "waiting")], path, desktop, TestContext.Current.CancellationToken));
            Assert.Equal("Original task", row.Detail); Assert.Equal("Project", row.Name); Assert.Equal("waiting", row.State);
        });
    }
}
