using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task MacReferenceRegression(DashboardWindow dashboard, DashboardStore store, AppSettingsStore settings, string directory)
    {
        var checks = new List<string>();
        var before = settings.Current;
        var claudeBefore = store.Usage.GetValueOrDefault("claude");
        try
        {
            dashboard.Navigate("general"); await Idle();
            Require(dashboard.Title == "General", "Settings title does not track the selected section.");
            var sidebar = Descendants<ListBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Settings sections");
            var toggle = Descendants<System.Windows.Controls.Button>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "settings.sidebar.toggle");
            var beforeWidth = sidebar.ActualWidth;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(!sidebar.IsVisible && AutomationProperties.GetName(toggle) == "Show Sidebar", "Sidebar did not collapse or announce its state.");
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(sidebar.IsVisible && Math.Abs(sidebar.ActualWidth - beforeWidth) < 1 && AutomationProperties.GetName(toggle) == "Hide Sidebar", "Sidebar failed to restore its width.");
            checks.Add("Window section title and sidebar hide/show preserve width");

            var refresh = Descendants<System.Windows.Controls.ComboBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Mode");
            Require(refresh.Items.Count == 8, "Refresh mode choices differ from the macOS reference.");
            refresh.SelectedItem = 60; await Idle();
            Require(settings.Current.RefreshIntervalSeconds == 60 && !settings.Current.AutomaticRefresh, "One-minute polling is still conflated with Automatic.");
            refresh.SelectedItem = -1; await Idle();
            Require(settings.Current.RefreshIntervalSeconds == 60 && settings.Current.AutomaticRefresh, "Automatic refresh lost its one-minute fallback.");
            refresh.SelectedItem = 1800; await Idle();
            Require(settings.Current.RefreshIntervalSeconds == 1800 && !settings.Current.AutomaticRefresh, "Thirty-minute mode was not persisted.");
            checks.Add("All eight refresh choices distinguish file events from timed polling");

            settings.Save(before with { CostEstimatesEnabled = true, AnalyticsEnabled = true, ProjectsEnabled = true, SessionsEnabled = true });
            dashboard.Navigate("usage"); await Idle();
            var pane = Descendants<UsagePane>(dashboard).Single(); pane.SelectProvider("codex");
            pane.HandleShortcut(System.Windows.Input.Key.D1, System.Windows.Input.ModifierKeys.Control); await Idle();
            Capture(dashboard, Path.Combine(directory, "windows-reference-overview.png"));
            File.WriteAllText(Path.Combine(directory, "windows-reference-overview-state.json"), JsonSerializer.Serialize(new {
                settings.Current.CostEstimatesEnabled, settings.Current.AnalyticsEnabled, snapshot = store.Usage.GetValueOrDefault("codex"),
                visibleText = Descendants<TextBlock>(pane).Select(x => x.Text).ToArray() }));
            Require(Descendants<TextBlock>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.today.cost"), "Today is missing the existing local cost estimate.");
            var picker = Descendants<System.Windows.Controls.ComboBox>(pane).Single(x => AutomationProperties.GetName(x) == "Usage provider");
            Require(Math.Abs(picker.ActualWidth - 142) < 1 && Math.Abs(picker.ActualHeight - 34) < 1, "Provider selector dimensions differ from the reference.");
            Descendants<System.Windows.Controls.Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.destination.projects").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(!picker.IsVisible && Descendants<TextBlock>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.detail.title" && x.Text == "Projects"), "Detail view retained the overview header.");
            Require(!Descendants<System.Windows.Controls.Button>(pane).Any(x => x.IsVisible && x.Content as string == "Switch"), "Account switcher leaked into a detail view.");
            Descendants<System.Windows.Controls.Button>(pane).Single(x => AutomationProperties.GetAutomationId(x) == "usage.navigation.back").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Require(picker.IsVisible && picker.SelectedValue as string == "codex", "Back failed to restore provider selection.");
            checks.Add("Reference provider size, Today cost and overview/detail/Back hierarchy");

            store.Usage["claude"] = UsageSnapshot.Empty; pane.SelectProvider("claude"); await Idle();
            Require(Descendants<StackPanel>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.empty"), "Unavailable local usage was rendered as a numeric zero.");
            Require(!Descendants<Grid>(pane).Any(x => AutomationProperties.GetAutomationId(x) == "usage.history"), "An empty Claude source showed fabricated history.");
            Capture(dashboard, Path.Combine(directory, "windows-reference-empty.png"));
            checks.Add("Empty Claude state has no fabricated total or history");
        }
        finally
        {
            if (claudeBefore is null) store.Usage.Remove("claude"); else store.Usage["claude"] = claudeBefore;
            settings.Save(before); dashboard.Navigate("usage");
            Descendants<UsagePane>(dashboard).Single().SelectProvider("codex"); await Idle();
        }
        File.WriteAllText(Path.Combine(directory, "windows-mac-reference.json"), JsonSerializer.Serialize(new { completed = true, checks }));
    }
}
