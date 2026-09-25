using System.Globalization;
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
    private static async Task AnalyticsDetailsRegression(DashboardStore store, AppSettingsStore settings, string directory)
    {
        var previousUsage = store.Usage.GetValueOrDefault("codex"); var previousEvents = store.Events.GetValueOrDefault("codex");
        var previousMetadata = store.SessionDetails.GetValueOrDefault("codex"); var previousSettings = settings.Current;
        Window? window = null; Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            settings.Save(previousSettings with { UsageProvider = "codex", CostEstimatesEnabled = true, AgentDetailsEnabled = true, AttachmentMetadataEnabled = true });
            var now = DateTimeOffset.Now;
            store.Events["codex"] = [
                new("detail-parent", now, new(100, 20, 27, 0), "gpt-5.6-sol", "Reference project", "parent", "codex", "reference"),
                new("detail-unknown", now, new(10, 0, 0, 0), "unpriced-fixture", "Reference project", "parent", "codex", "reference"),
                new("detail-child", now.AddDays(-1), new(20, 0, 5, 0), "gpt-5.6-sol", "Child project", "child", "codex", "child-project"),
                new("detail-old-child", now.AddDays(-9), new(3, 0, 0, 0), "gpt-5.6-sol", "Older child", "old-child", "codex", "older")];
            store.SessionDetails["codex"] = [new("parent", null, [new("attachment", now.AddDays(-15), 4)]), new("child", "parent", []), new("old-child", "parent", [])];
            store.Usage["codex"] = UsageScanner.Aggregate(store.Events["codex"], now, settings.Current.WeekStart, false) with { Quality = DataQuality.Partial };
            var pane = new UsagePane(store, settings, "codex", _ => { }) { Width = 500 };
            window = new Window { Content = pane, Width = 550, Height = 800, Title = "Analytics details fixture" }; window.Show(); await Idle();
            Button FindButton(string id) => Descendants<Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == id);
            FrameworkElement Element(string id) => Descendants<FrameworkElement>(pane).Single(x => AutomationProperties.GetAutomationId(x) == id);
            string[] Texts(DependencyObject root) => Descendants<TextBlock>(root).Select(x => x.Text).ToArray();
            void Value(string label, string expected)
            {
                var row = Descendants<Grid>(pane).Single(x => Texts(x) is [var first, _] && first == label);
                Require(Texts(row)[1] == expected, "Entity value does not match " + label);
            }
            async Task Click(string id) { FindButton(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(); }
            void ReferenceDetail()
            {
                Require(!Descendants<Button>(pane).Any(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.bucket.", StringComparison.Ordinal))
                    && !Descendants<RadioButton>(pane).Any(x => x.GroupName == "UsageRange"), "Entity detail retained extra charts or a period picker");
                Require(Texts(Element("usage.entity.header")) is ["Reference project", "137", "tokens"], "Entity header did not retain its name and own exact usage");
                var cost = Texts(Element("usage.entity.cost"));
                Require(cost.Any(x => x.StartsWith("Estimated API cost subtotal · ~", StringComparison.Ordinal) && x.EndsWith(" · partial history", StringComparison.Ordinal))
                    && cost.Contains("Excludes 10 tokens · unpriced-fixture", StringComparer.Ordinal), "Entity cost lost its partial history or pricing exclusions");
                Value("Input", "110"); Value("Cached input", "20"); Value("Output", "27");
                Require(Texts(Element("usage.entity.model.gpt-5.6-sol")) is ["gpt-5.6-sol", "127"]
                    && Texts(Element("usage.entity.model.unpriced-fixture")) is ["unpriced-fixture", "10"], "Entity model totals include another entity or omit a model");
            }
            await Click("usage.destination.projects"); await Click("usage.project.reference"); ReferenceDetail();
            Value("Sessions", "1");
            Require(!Texts(pane).Contains("Last activity", StringComparer.Ordinal), "Project metadata is not project-specific");
            Capture(pane, System.IO.Path.Combine(directory, "windows-project-detail-reference.png"));
            pane.Back(); await Idle(); pane.Back(); await Idle();
            await Click("usage.destination.sessions"); await Click("usage.session.parent"); ReferenceDetail();
            Value("Last activity", now.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)); Value("Direct sub-agents", "1"); Value("Whole-session images", "4");
            var agents = Descendants<Button>(pane).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("usage.subagent.", StringComparison.Ordinal)).ToArray();
            Require(agents.Length == 1 && AutomationProperties.GetName(agents[0]) == "Child project: 25 tokens", "Sub-agent row lost its range, project name or separate token total");
            foreach (var width in new[] { 360d, 450d, 650d })
            {
                window.Width = width + 50; pane.Width = width; pane.UpdateLayout(); await Idle();
                var detail = Element("usage.entity.detail");
                foreach (var text in Descendants<TextBlock>(detail))
                {
                    var bounds = text.TransformToAncestor(detail).TransformBounds(new Rect(text.RenderSize));
                    Require(bounds.Left >= -.1 && bounds.Right <= detail.ActualWidth + .1, "Entity detail text exceeds its narrow content area");
                }
                Capture(pane, System.IO.Path.Combine(directory, "windows-session-detail-width-" + width.ToString(CultureInfo.InvariantCulture) + ".png"));
            }
            await Click("usage.subagent.child");
            Require(Texts(Element("usage.entity.header")) is ["Child project", "25", "tokens"], "Sub-agent navigation did not isolate its own usage");
            pane.Back(); await Idle(); ReferenceDetail();
            settings.Save(settings.Current with { CostEstimatesEnabled = false, AgentDetailsEnabled = false, AttachmentMetadataEnabled = false }); pane.Update(); await Idle();
            Require(!Descendants<FrameworkElement>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.entity.cost")
                && !Texts(pane).Any(x => x is "Direct sub-agents" or "Sub-agents" or "Whole-session images"), "Disabled detail fields remain visible");
            settings.Save(settings.Current with { CostEstimatesEnabled = true });
            store.Events["codex"] = [new("unpriced-only", now, new(10, 0, 0, 0), "unpriced-fixture", "Reference project", "parent", "codex", "reference")]; pane.Update(); await Idle();
            Require(Texts(Element("usage.entity.cost")) is ["Estimated cost unavailable"], "Unpriced detail fabricated a numeric estimate");
            store.Events["codex"] = [new("large-detail", now, new(long.MaxValue, 0, 0, 0), "unpriced-fixture", "Reference project", "parent", "codex", "reference")];
            window.Width = 410; pane.Width = 360; pane.Update(); await Idle();
            var header = Element("usage.entity.header");
            var number = Descendants<TextBlock>(header).Single(x => x.Text == long.MaxValue.ToString("N0", CultureInfo.CurrentCulture));
            var numberBounds = number.TransformToAncestor(header).TransformBounds(new Rect(number.RenderSize));
            Require(numberBounds.Right <= header.ActualWidth + .1 && numberBounds.Width / number.ActualWidth >= .749,
                "Entity total overflows or shrinks below the reference minimum scale");
            Capture(pane, System.IO.Path.Combine(directory, "windows-session-detail-large-number.png"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "windows-analytics-details.json"), System.Text.Json.JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Project/session reference headers and exact own totals", "Compact partial cost and excluded pricing", "Inherited range without extra charts or picker",
                    "Project sessions and session activity/image metadata", "Direct child names, own totals, range filtering and Back", "Model breakdown isolates the entity",
                    "Detail visibility preferences and unavailable pricing", "Mounted 360/450/650 horizontal text bounds", "Large entity total and 0.75 minimum scale" } }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { window?.Close(); } catch (Exception error) { cleanup.Add(error); }
            try { if (previousUsage is null) store.Usage.Remove("codex"); else store.Usage["codex"] = previousUsage; } catch (Exception error) { cleanup.Add(error); }
            try { if (previousEvents is null) store.Events.Remove("codex"); else store.Events["codex"] = previousEvents; } catch (Exception error) { cleanup.Add(error); }
            try { if (previousMetadata is null) store.SessionDetails.Remove("codex"); else store.SessionDetails["codex"] = previousMetadata; } catch (Exception error) { cleanup.Add(error); }
            try { settings.Save(previousSettings); } catch (Exception error) { cleanup.Add(error); }
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Analytics detail fixture and cleanup failed", cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Analytics detail fixture cleanup failed", cleanup);
    }
}
