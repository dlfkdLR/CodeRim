using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class UsagePane
{
    // Mirrors the current macOS UsageSettingsOverview. Local history remains
    // explicitly device-scoped; it must never impersonate an account total.
    private void Overview()
    {
        var snapshot = store.Usage.GetValueOrDefault(provider) ?? UsageSnapshot.Empty;
        readings.Children.Add(Heading("Today"));
        if (snapshot.UpdatedAt is null)
        {
            var empty = new StackPanel { MinHeight = 100, Margin = new Thickness(0, 12, 0, 12), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(empty, "usage.empty");
            empty.Children.Add(Ui.Text(store.IsRefreshing ? "Reading local usage" : "No local usage yet", 14, weight: FontWeights.SemiBold));
            empty.Children.Add(Ui.Text(provider == "claude" && snapshot.Quality == DataQuality.Unavailable && !store.IsRefreshing
                ? "Start a Claude Code session, then Refresh." : store.Status, 13, "#A6A6AA"));
            readings.Children.Add(empty);
        }
        else
        {
            var overview = new Grid { Margin = new Thickness(0, 12, 0, 16) };
            AutomationProperties.SetAutomationId(overview, "usage.overview");
            overview.ColumnDefinitions.Add(new ColumnDefinition()); overview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            var total = new StackPanel();
            var totalText = Metric("today", snapshot.Today.TotalTokens, 42);
            totalText.Margin = new Thickness(0); totalText.TextWrapping = TextWrapping.NoWrap;
            total.Children.Add(new Viewbox { Child = totalText, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, MaxHeight = 56 });
            var unit = Ui.Text("tokens", 13, "#A6A6AA"); unit.Margin = new Thickness(0, 6, 0, 0); total.Children.Add(unit);
            total.ToolTip = "Cached input is already included in Input. Total equals Input plus Output.";
            overview.Children.Add(total);
            var breakdown = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            OverviewMetric(breakdown, "Input", snapshot.Today.InputTokens, "M8,14 V2 M3,7 L8,2 L13,7");
            if (settings.Current.ShowCachedInput) OverviewMetric(breakdown, "Cached input", snapshot.Today.CachedInputTokens, "M1,10 L6,5 L9,8 L15,2 M1,14 L6,9 L9,12 L15,6");
            OverviewMetric(breakdown, "Output", snapshot.Today.OutputTokens, "M8,2 V14 M3,9 L8,14 L13,9");
            breakdown.ToolTip = "Cached input is included in Input; it is not added again to the total.";
            Grid.SetColumn(breakdown, 1); overview.Children.Add(breakdown); AdaptOverview(overview, total, breakdown); readings.Children.Add(overview);
            if (settings.Current.AnalyticsEnabled && settings.Current.CostEstimatesEnabled && provider == "codex")
            {
                var now = DateTimeOffset.Now;
                var cost = UsageAnalytics.Estimate((store.Events.GetValueOrDefault(provider) ?? [])
                    .Where(e => e.OccurredAt <= now && e.OccurredAt.LocalDateTime.Date == now.LocalDateTime.Date));
                if (cost.Amount is { } amount)
                {
                    var text = Ui.Text(cost.Label + " · ~$" + amount.ToString("N2", CultureInfo.CurrentCulture)
                        + (snapshot.Quality == DataQuality.Partial || snapshot.RetainsPartialHistory ? " · partial history" : ""), 11, "#A6A6AA");
                    text.Margin = new Thickness(0, 0, 0, 16);
                    text.ToolTip = "Estimated from bundled API pricing; not a bill." + (cost.IsPartial ? $" Excludes {cost.ExcludedTokens:N0} tokens: " + string.Join(", ", cost.ExcludedModels) : "");
                    AutomationProperties.SetAutomationId(text, "usage.today.cost"); readings.Children.Add(text);
                }
            }
        }

        if (provider == "codex" || snapshot.UpdatedAt is not null)
        {
            var accountHistory = provider == "codex" && store.ProfileHistory.Enabled;
            var accountSnapshot = store.ProfileHistory.Snapshot;
            var scope = accountHistory ? "ChatGPT account" : "This PC";
            readings.Children.Add(SettingsUi.Divider()); readings.Children.Add(Heading("History", scope));
            var historyGrid = new Grid { Margin = new Thickness(0, 10, 0, 16) };
            AutomationProperties.SetAutomationId(historyGrid, "usage.history");
            (string Title, string Period, long? Total)[] values = [
                ("This Week", "week", accountHistory ? accountSnapshot?.Week : snapshot.UpdatedAt is null ? null : snapshot.Week.TotalTokens),
                ("This Month", "month", accountHistory ? accountSnapshot?.Month : snapshot.UpdatedAt is null ? null : snapshot.Month.TotalTokens),
                (accountHistory ? "Lifetime" : "Local History", "all-time", accountHistory ? accountSnapshot?.Lifetime : snapshot.UpdatedAt is null ? null : snapshot.AllTime.TotalTokens)];
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i]; historyGrid.ColumnDefinitions.Add(new ColumnDefinition());
                var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                var label = new DockPanel();
                var arrow = Ui.Text("›", 13, "#A6A6AA"); arrow.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(arrow, Dock.Right); label.Children.Add(arrow);
                var name = Ui.Text(value.Item1, 13, "#A6A6AA"); name.Margin = new Thickness(0); label.Children.Add(name); panel.Children.Add(label);
                FrameworkElement metric = value.Total is { } count ? Metric((accountHistory ? "account:" : "local:") + value.Period, count, 20) : Ui.Text("—", 20, weight: FontWeights.SemiBold);
                metric.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(metric);
                var button = Ui.Button("", () => Forward(accountHistory ? "account-period" : "local-period", value.Period));
                button.IsEnabled = accountHistory || snapshot.UpdatedAt is not null;
                AutomationProperties.SetAutomationId(button, "usage.history." + value.Period);
                AutomationProperties.SetName(button, scope + " " + value.Title + ", " + (value.Total is null ? "Unavailable" : TokenFormatter.Format(value.Total.Value, settings.Current.NumberStyle)) + " tokens");
                button.Content = panel; button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Margin = new Thickness(0);
                if (i > 0) { var separator = new Border { Width = 1, Height = 64, HorizontalAlignment = HorizontalAlignment.Left }; separator.SetResourceReference(Border.BackgroundProperty, "DividerBrush"); Grid.SetColumn(separator, i); historyGrid.Children.Add(separator); }
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(i == 0 ? 0 : 16, 4, 16, 4); Grid.SetColumn(button, i); historyGrid.Children.Add(button);
            }
            AdaptHistory(historyGrid); readings.Children.Add(historyGrid);
            AddAccountHistoryFooter();
        }
        AddOverviewLinks();
        if (settings.Current.ShowLastUpdated || store.IsRefreshing)
        {
            readings.Children.Add(SettingsUi.Divider());
            var footer = Ui.Text(store.IsRefreshing ? "Refreshing…" : store.Status, 11, "#808080");
            footer.Margin = new Thickness(0, 10, 0, 0); readings.Children.Add(footer);
        }
    }

    private void OverviewMetric(Panel parent, string title, long value, string path)
    {
        var row = new DockPanel { Margin = new Thickness(0, parent.Children.Count == 0 ? 0 : 10, 0, 0) };
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(path), Width = 16, Height = 16, StrokeThickness = 1.25,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText"); DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        var reading = Ui.Text(TokenFormatter.Format(value, settings.Current.NumberStyle)); reading.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(reading, Dock.Right); row.Children.Add(reading);
        var label = Ui.Text(title, 13, "#A6A6AA"); label.Margin = new Thickness(0); row.Children.Add(label); parent.Children.Add(row);
    }

    private void AddOverviewLinks()
    {
        var links = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 16, 0, 16) };
        AutomationProperties.SetAutomationId(links, "usage.links");
        foreach (var (id, label, icon) in new[]
        {
            ("activity", "Usage", "M1,1 V15 H15 M3,11 L6,7 L10,9 L14,3"),
            ("projects", "Projects", "M1,4 V13 Q1,15 3,15 H13 Q15,15 15,13 V5 Q15,3 13,3 H7 L5,1 H3 Q1,1 1,3 Z"),
            ("sessions", "Sessions", "M1,1 H15 V11 H8 L4,15 V11 H1 Z M4,4 H12 M4,7 H10")
        })
        {
            if (!settings.Current.AnalyticsEnabled || id == "projects" && !settings.Current.ProjectsEnabled || id == "sessions" && !settings.Current.SessionsEnabled) continue;
            var link = Ui.Button(label, () => Forward(id, "7d"));
            AutomationProperties.SetAutomationId(link, "usage.destination." + id);
            AutomationProperties.SetName(link, "Open " + label);
            link.BorderThickness = new Thickness(0); link.SetResourceReference(Control.BackgroundProperty, "ControlBackground");
            link.Padding = new Thickness(12); link.Margin = new Thickness(0, 0, 12, 0); link.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var content = new DockPanel();
            var mark = new System.Windows.Shapes.Path { Data = Geometry.Parse(icon), StrokeThickness = 1.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            mark.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText"); DockPanel.SetDock(mark, Dock.Left); content.Children.Add(mark);
            var disclosure = Ui.Text("›", 13, "#98989D"); disclosure.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(disclosure, Dock.Right); content.Children.Add(disclosure);
            var title = Ui.Text(label, 13); title.Margin = new Thickness(0); title.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(title);
            link.Content = content; links.Children.Add(link);
        }
        if (links.Children.Count == 0) return;
        ((FrameworkElement)links.Children[links.Children.Count - 1]).Margin = new Thickness(0);
        links.Columns = links.Children.Count;
        links.SizeChanged += (_, _) => links.Columns = links.ActualWidth < 390 ? 1 : links.Children.Count;
        readings.Children.Add(SettingsUi.Divider()); readings.Children.Add(links);
    }
}
