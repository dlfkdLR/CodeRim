using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;
using Microsoft.Data.Sqlite;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static readonly string[] AnalyticsFixtureTitles = ["Explicit task", "Desktop task", "Stored third"];
    private static void AnalyticsLabelSql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static async Task AnalyticsLabelsRegression(DashboardStore store, AppSettingsStore settings, string directory)
    {
        var previousUsage = store.Usage.GetValueOrDefault("codex"); var previousEvents = store.Events.GetValueOrDefault("codex");
        var previousMetadata = store.SessionDetails.GetValueOrDefault("codex"); var previousLabels = store.CodexAnalyticsLabels;
        var previousSettings = settings.Current; var root = Path.Combine(directory, "analytics-label-fixture-" + Guid.NewGuid().ToString("N"));
        Window? window = null; Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            Require(store.Synthetic, "Analytics labels fixture needs the isolated preview store"); Directory.CreateDirectory(root);
            var ids = new[] { "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222", "33333333-3333-4333-8333-333333333333" };
            var keys = ids.Select(id => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))).ToArray(); var state = Path.Combine(root, "state.sqlite"); var desktop = Path.Combine(root, "desktop.sqlite");
            AnalyticsLabelSql(state, """
                CREATE TABLE threads(id TEXT PRIMARY KEY,title TEXT,cwd TEXT,rollout_path TEXT,name TEXT,project_id TEXT,archived INTEGER);
                CREATE TABLE projects(id TEXT PRIMARY KEY,name TEXT);
                INSERT INTO projects VALUES('a','Alpha'),('b','Beta'),('c','Gamma');
                """);
            AnalyticsLabelSql(state, $"""
                INSERT INTO threads VALUES('{ids[0]}','Stored parent','C:/fixture/Folder','unused','Explicit task','a',1),
                    ('{ids[1]}','Stored child','C:/fixture/Folder','unused',NULL,'b',0),
                    ('{ids[2]}','Stored third','C:/fixture/Folder','unused',NULL,'c',0);
                """);
            AnalyticsLabelSql(desktop, $"""
                CREATE TABLE local_thread_catalog(host_id TEXT,thread_id TEXT,display_title TEXT,source_updated_at INTEGER);
                INSERT INTO local_thread_catalog VALUES('local','{ids[0]}','Lower priority title',1),('local','{ids[1]}','Desktop task',1),
                    ('remote','{ids[2]}','Remote task must not leak',2);
                """);
            var stateBytes = File.ReadAllBytes(state); var desktopBytes = File.ReadAllBytes(desktop); var now = DateTimeOffset.Now.AddMinutes(-1);
            store.Events["codex"] = keys.Select((key, index) => new UsageEvent("label-" + index, now, new(10 * (index + 1), 0, 0),
                "gpt-5.6-sol", "Codex", key, "codex", "generic-project")).ToArray();
            store.SessionDetails["codex"] = keys.Select((key, index) => new SessionDetails(key, index == 1 ? keys[0] : null, []) { ProjectName = "Recorded folder" }).ToArray();
            store.Usage["codex"] = UsageScanner.Aggregate(store.Events["codex"], now, settings.Current.WeekStart, false);
            store.CodexAnalyticsLabels = CodexActivityCatalogue.ReadAnalyticsLabels(keys, state, desktop);
            Require(store.CodexAnalyticsLabels.Count == 3, "Read-only label query omitted archived or local tasks");
            settings.Save(previousSettings with { UsageProvider = "codex", AgentDetailsEnabled = true, CostEstimatesEnabled = false });
            var pane = new UsagePane(store, settings, "codex", _ => { }) { Width = 360 };
            window = new Window { Content = pane, Width = 410, Height = 740, Title = "Analytics title fixture" }; window.Show(); await Idle();
            Button ButtonFor(string id) => Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == id);
            string[] Texts(DependencyObject item) => Descendants<TextBlock>(item).Select(x => x.Text).ToArray();
            string[] Header() => Texts(Descendants<FrameworkElement>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.entity.header"));
            async Task Click(string id) { ButtonFor(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(); }
            pane.ShowSessions(); await Idle();
            foreach (var (key, name) in keys.Zip(AnalyticsFixtureTitles))
                Require(Texts(ButtonFor("usage.session." + key)).Contains(name, StringComparer.Ordinal), "Session list title priority differs from Mac");
            Capture(pane, Path.Combine(directory, "windows-analytics-task-titles.png"));
            await Click("usage.session." + keys[0]); Require(Header() is ["Explicit task", "10", "tokens"], "Title enrichment changed the parent identity or count");
            Require(AutomationProperties.GetName(ButtonFor("usage.subagent." + keys[1])) == "Desktop task: 20 tokens", "Sub-agent link omitted the desktop title");
            await Click("usage.subagent." + keys[1]); Require(Header() is ["Desktop task", "20", "tokens"], "Child detail title or own count differs");
            pane.Back(); await Idle(); pane.Back(); await Idle(); pane.Back(); await Idle();
            await Click("usage.destination.projects");
            Require(Texts(ButtonFor("usage.project.generic-project")).Contains("Alpha · Beta (+1)", StringComparer.Ordinal), "Generic project did not combine sorted reference names");
            await Click("usage.project.generic-project"); Require(Header() is ["Alpha · Beta (+1)", "60", "tokens"], "Project naming changed usage aggregation");
            Capture(pane, Path.Combine(directory, "windows-analytics-project-titles.png"));
            store.CodexAnalyticsLabels = new Dictionary<string, CodexAnalyticsLabel>(); pane.Update(); await Idle();
            Require(Header() is ["Codex", "60", "tokens"], "Missing label source did not restore the numeric history fallback");
            pane.Back(); await Idle(); pane.Back(); await Idle(); pane.ShowSessions(); await Idle(); await Click("usage.session." + keys[0]);
            Require(Header() is ["Recorded folder", "10", "tokens"], "Missing task title did not retain persisted session naming");
            Require(File.ReadAllBytes(state).SequenceEqual(stateBytes) && File.ReadAllBytes(desktop).SequenceEqual(desktopBytes), "Analytics naming modified the source databases");
            File.WriteAllText(Path.Combine(directory, "windows-analytics-labels.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Stored/name/desktop priority, archived inclusion and remote exclusion", "Mounted session list and child detail use resolved titles",
                    "Sorted generic project summary retains exact own totals", "Missing optional labels retain history names and numeric data", "Source databases unchanged" } }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => window?.Close()); Restore(() => store.CodexAnalyticsLabels = previousLabels);
            Restore(() => { if (previousUsage is null) store.Usage.Remove("codex"); else store.Usage["codex"] = previousUsage; });
            Restore(() => { if (previousEvents is null) store.Events.Remove("codex"); else store.Events["codex"] = previousEvents; });
            Restore(() => { if (previousMetadata is null) store.SessionDetails.Remove("codex"); else store.SessionDetails["codex"] = previousMetadata; });
            Restore(() => settings.Save(previousSettings)); Restore(() => { if (Directory.Exists(root)) Directory.Delete(root, true); });
        }
        if (cleanup.Count > 0) throw new AggregateException("Analytics title fixture cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
