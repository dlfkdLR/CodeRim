using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class DashboardWindow
{
    private void AddProviderLocalData(string id, string name)
    {
        FrameworkElement Value(string title, string key) => SettingsUi.Row(title, ProviderValue("local-data." + key, "—"));
        var size = Value("Database size", "size"); size.ToolTip = "Shared local database, including its write-ahead log. No credentials or conversation text are stored here.";
        body.Children.Add(SettingsUi.Section(name + " Local Data",
            Value("Status", "status"), Value("Source files", "sources"), size,
            Value("Oldest record", "oldest"), Value("Newest record", "newest"),
            SettingsUi.Action("Open Data Folder", () => OpenUrl(CompanionFile.DataDirectory))));
        var sources = new List<UIElement> { Value("Local " + name + " sessions", "status") };
        if (id == "codex")
        {
            sources.Add(Value("Account limits", "limits"));
            sources.Add(SettingsUi.Value("Pricing catalog", "Available · " + UsageAnalytics.CatalogVersion));
        }
        body.Children.Add(SettingsUi.Section("Sources", sources.ToArray()));
        if (id == "claude") body.Children.Add(SettingsUi.Note("Reads ~/.claude/projects, or CLAUDE_CONFIG_DIR when set in the app's environment."));
        var rebuild = Ui.AsyncButton("Rebuild Statistics", () => store.RebuildStatisticsAsync(id));
        rebuild.HorizontalAlignment = HorizontalAlignment.Left; rebuild.Margin = new Thickness(14, 9, 14, 9);
        AutomationProperties.SetAutomationId(rebuild, "local-data.rebuild");
        var clear = Ui.AsyncButton("Clear Local History", async () =>
        {
            if (MessageBox.Show(this, "This deletes only the local " + name + " statistics. Session files are not deleted, and records at or before this time will remain excluded.",
                    "Clear " + name + " local history?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await store.ClearLocalHistoryAsync(id);
        });
        clear.HorizontalAlignment = HorizontalAlignment.Left; clear.Margin = new Thickness(14, 9, 14, 9);
        AutomationProperties.SetAutomationId(clear, "local-data.clear");
        var operation = ProviderValue("local-data.operation", ""); operation.Margin = new Thickness(14, 9, 14, 9);
        body.Children.Add(SettingsUi.Section("Manage Data", operation, rebuild, clear));
        UpdateProviderLocalData(id);
    }
    private void UpdateProviderLocalData(string id)
    {
        if (id is not ("codex" or "claude")) return;
        var data = store.DataStatistics.GetValueOrDefault(id);
        var busy = store.LocalDataBusy(id);
        var sourceState = store.SourceCounts.TryGetValue(id, out var count) ? count > 0 ? "Connected" : "No session files found" : busy ? "Checking…" : "Not scanned";
        var reading = store.AccountDisplay(id).Reading;
        foreach (var label in VisualChildren<TextBlock>(body))
        {
            var value = AutomationProperties.GetAutomationId(label) switch
            {
                "local-data.status" => sourceState,
                "local-data.sources" => store.SourceCounts.ContainsKey(id) ? count.ToString("N0", CultureInfo.CurrentCulture) : "—",
                "local-data.size" => data is null ? "—" : FormatDatabaseBytes(data.DatabaseBytes),
                "local-data.oldest" => data is null ? "—" : data.OldestRecord?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "None",
                "local-data.newest" => data is null ? "—" : data.NewestRecord?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "None",
                "local-data.limits" => !settings.Current.AccountLimitsEnabled ? "Disabled" : store.RefreshingProviders.Contains(id) ? "Checking…" : reading?.State switch
                    { ReadingState.Ready => "Connected", ReadingState.Stale => "Last known data", _ => "Unavailable" },
                "local-data.operation" => store.DataOperationMessages.GetValueOrDefault(id) ?? (busy ? "Reading local sessions…" : ""),
                _ => null
            };
            if (value is null) continue; label.Text = value;
            if (AutomationProperties.GetAutomationId(label) == "local-data.operation") label.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        foreach (var button in VisualChildren<Button>(body).Where(x => AutomationProperties.GetAutomationId(x) is "local-data.rebuild" or "local-data.clear"))
            button.IsEnabled = !busy && !store.Synthetic;
    }
    private static string FormatDatabaseBytes(long bytes) => bytes < 1000 ? bytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes"
        : bytes < 1000000 ? (bytes / 1000d).ToString("N1", CultureInfo.CurrentCulture) + " KB"
        : bytes < 1000000000 ? (bytes / 1000000d).ToString("N1", CultureInfo.CurrentCulture) + " MB"
        : (bytes / 1000000000d).ToString("N1", CultureInfo.CurrentCulture) + " GB";
}
