using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task ClaudeAgentUsageRegression(DashboardStore store, AppSettingsStore settings, string directory)
    {
        var previousUsage = store.Usage.GetValueOrDefault("claude"); var previousEvents = store.Events.GetValueOrDefault("claude");
        var previousMetadata = store.SessionDetails.GetValueOrDefault("claude"); var previousSettings = settings.Current;
        var root = Path.Combine(directory, "claude-agent-fixture-" + Guid.NewGuid().ToString("N"));
        Window? window = null; Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            Require(store.Synthetic && store.ClaudeAvailable, "Claude agent fixture needs the isolated preview integration");
            var source = Path.Combine(root, "sources"); Directory.CreateDirectory(source); var time = DateTimeOffset.Now.AddMinutes(-1);
            string Row(string id, int input, string? agent = null) => JsonSerializer.Serialize(new {
                type = "assistant", timestamp = time, sessionId = "fixture-parent", agentId = agent, cwd = "C:/fixture/Reference",
                message = new { id, role = "assistant", model = "claude-sonnet-4-6", usage = new { input_tokens = input, output_tokens = 0 } } });
            File.WriteAllLines(Path.Combine(source, "parent.jsonl"), [Row("parent", 100), Row("child", 50)]);
            File.WriteAllLines(Path.Combine(source, "agent-child.jsonl"), [Row("child", 50), Row("child", 50, "child")]);
            var scan = await new UsageScanner("claude", [source]).ScanAsync(settings.Current.WeekStart);
            var repository = new UsageRepository(Path.Combine(root, "usage.sqlite")); repository.Merge("claude", scan.Events, scan.Sessions);
            store.Events["claude"] = repository.Read("claude"); store.SessionDetails["claude"] = repository.ReadSessionDetails("claude");
            store.Usage["claude"] = UsageScanner.Aggregate(store.Events["claude"], DateTimeOffset.Now, settings.Current.WeekStart, false);
            Require(store.Usage["claude"].AllTime.TotalTokens == 150 && store.SessionDetails["claude"].Count == 2, "Claude import lost or duplicated agent usage");
            var child = store.SessionDetails["claude"].Single(x => x.ParentId is not null); var parent = child.ParentId!;
            settings.Save(previousSettings with { UsageProvider = "claude", AgentDetailsEnabled = true, AttachmentMetadataEnabled = true, CostEstimatesEnabled = true });
            var pane = new UsagePane(store, settings, "claude", _ => { }) { Width = 360 };
            window = new Window { Content = pane, Width = 410, Height = 720, Title = "Claude agent analytics fixture" }; window.Show(); await Idle();
            Button ButtonFor(string id) => Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == id);
            string[] Header() => Descendants<TextBlock>(Descendants<FrameworkElement>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.entity.header")).Select(x => x.Text).ToArray();
            async Task Click(string id) { ButtonFor(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(); }
            pane.ShowSessions(); await Idle(); await Click("usage.session." + parent);
            Require(Header() is ["Reference", "100", "tokens"], "Claude parent detail includes its child's tokens");
            var childButton = ButtonFor("usage.subagent." + child.Id);
            Require(AutomationProperties.GetName(childButton) == "Reference: 50 tokens", "Claude child link has wrong name or usage");
            Require(!Descendants<FrameworkElement>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.entity.cost")
                && !Descendants<TextBlock>(pane).Any(x => x.Text == "Whole-session images"), "Claude detail exposed unsupported Codex-only fields");
            Capture(pane, Path.Combine(directory, "windows-claude-parent-detail.png"));
            await Click("usage.subagent." + child.Id);
            Require(Header() is ["Reference", "50", "tokens"], "Claude child detail does not isolate its own usage");
            Capture(pane, Path.Combine(directory, "windows-claude-child-detail.png"));
            pane.Back(); await Idle(); settings.Save(settings.Current with { AgentDetailsEnabled = false }); pane.Update(); await Idle();
            Require(Header() is ["Reference", "100", "tokens"]
                && !Descendants<Button>(pane).Any(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.subagent.", StringComparison.Ordinal)),
                "Hiding Claude agent detail changed totals or kept child links");
            File.WriteAllText(Path.Combine(directory, "windows-claude-agent-usage.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Transcript scan and persisted parent/child links", "Copied history deduplication and 150 total tokens",
                    "Mounted parent100/child50 navigation and Back", "Claude capability and agent detail preference boundaries" } }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { window?.Close(); } catch (Exception error) { cleanup.Add(error); }
            try { if (previousUsage is null) store.Usage.Remove("claude"); else store.Usage["claude"] = previousUsage; } catch (Exception error) { cleanup.Add(error); }
            try { if (previousEvents is null) store.Events.Remove("claude"); else store.Events["claude"] = previousEvents; } catch (Exception error) { cleanup.Add(error); }
            try { if (previousMetadata is null) store.SessionDetails.Remove("claude"); else store.SessionDetails["claude"] = previousMetadata; } catch (Exception error) { cleanup.Add(error); }
            try { settings.Save(previousSettings); } catch (Exception error) { cleanup.Add(error); }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception error) { cleanup.Add(error); }
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Claude agent fixture and cleanup failed", cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Claude agent fixture cleanup failed", cleanup);
    }
}
