using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class DashboardWindow
{
    private Button? providerAlert;
    private System.Windows.Shapes.Path? providerBell;
    private CheckBox? providerNotify;
    private TextBlock? providerAlertNote;
    private bool updatingProviderAlert;
    private void ResetProviderAlerts() { providerAlert = null; providerBell = null; providerNotify = null; providerAlertNote = null; }
    private void SetProviderMuted(string id, bool muted)
    {
        if (updatingProviderAlert || settings.Current.MutedAlertProviders.Contains(id, StringComparer.Ordinal) == muted) return;
        Save(settings.Current with { MutedAlertProviders = muted
            ? settings.Current.MutedAlertProviders.Append(id).Distinct(StringComparer.Ordinal).ToArray()
            : settings.Current.MutedAlertProviders.Where(x => x != id).ToArray() });
        // Save reports failures through the existing dialog; always restore the
        // controls from committed preferences, including the failure path.
        UpdateProviderAlerts(id, store.AccountDisplay(id).Reading);
    }
    private void AddProviderAlertButton(DockPanel header, string id, string name)
    {
        providerAlert = Ui.Button("", () => SetProviderMuted(id, !settings.Current.MutedAlertProviders.Contains(id, StringComparer.Ordinal)));
        providerAlert.Width = providerAlert.Height = providerAlert.MinHeight = 26; providerAlert.Padding = new Thickness(4);
        providerAlert.Background = Brushes.Transparent; providerAlert.BorderThickness = new Thickness(0); providerAlert.Margin = new Thickness(0, 0, 8, 0);
        providerBell = new System.Windows.Shapes.Path { Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.25,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        providerBell.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText"); providerAlert.Content = providerBell;
        AutomationProperties.SetAutomationId(providerAlert, "provider.alerts"); AutomationProperties.SetName(providerAlert, "Mute " + name + " alerts");
        DockPanel.SetDock(providerAlert, Dock.Right); header.Children.Add(providerAlert);
    }
    private void AddProviderAlerts(string id)
    {
        providerNotify = (CheckBox)SettingsUi.Toggle("Notify at 80% and 100%", !settings.Current.MutedAlertProviders.Contains(id, StringComparer.Ordinal), value => SetProviderMuted(id, !value));
        AutomationProperties.SetAutomationId(providerNotify, "provider.notify");
        body.Children.Add(SettingsUi.Section("Alerts", providerNotify));
        providerAlertNote = SettingsUi.Note("Limit alerts are off for every provider — turn them on in the Notch pane."); body.Children.Add(providerAlertNote);
        UpdateProviderAlerts(id, store.AccountDisplay(id).Reading);
    }
    private void UpdateProviderAlerts(string id, ProviderReading? reading)
    {
        if (providerAlert is null) return;
        var name = ProviderCatalog.Find(id)?.Name ?? id; var muted = settings.Current.MutedAlertProviders.Contains(id, StringComparer.Ordinal);
        providerAlert.Visibility = reading is { State: ReadingState.Ready } or { Windows.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        providerAlert.IsEnabled = settings.Current.AlertsEnabled;
        AutomationProperties.SetName(providerAlert, (muted ? "Unmute " : "Mute ") + name + " alerts");
        providerAlert.ToolTip = muted ? "Alerts muted for " + name + ". Click to unmute." : "Alert when " + name + " crosses 80% and 100% of a limit.";
        providerBell!.Data = Geometry.Parse("M3,11 Q5,9 5,6 A3,3 0 0 1 11,6 Q11,9 13,11 Z M6,13 Q8,16 10,13" + (muted ? " M1,1 L15,15" : ""));
        providerBell.Opacity = muted ? 0.55 : 1;
        if (providerNotify is not null)
        {
            updatingProviderAlert = true;
            try { providerNotify.IsChecked = !muted; providerNotify.IsEnabled = settings.Current.AlertsEnabled; }
            finally { updatingProviderAlert = false; }
        }
        if (providerAlertNote is not null) providerAlertNote.Visibility = settings.Current.AlertsEnabled ? Visibility.Collapsed : Visibility.Visible;
    }
    private static string ProviderStatus(string id, ProviderReading? reading)
    {
        if (id == "codex") return "Available";
        return reading?.State switch
        {
            ReadingState.Ready => reading.Windows.Count > 0 ? "Connected" : "Connected — no reading yet",
            ReadingState.Loading => "Checking…",
            ReadingState.Stale => reading.UpdatedAt is { } date && date != DateTimeOffset.MinValue ? "Last read " + ElapsedCopy.Ago(date, DateTimeOffset.Now) : "Checking…",
            ReadingState.NeedsAuth => "Not connected",
            ReadingState.Disabled => "Disabled",
            _ => reading?.Message ?? "Not connected"
        };
    }
    private void RenderProviderLimits(ProviderReading? reading)
    {
        providerReading.Children.Clear(); providerReading.Margin = new Thickness(0);
        var now = DateTimeOffset.Now; var windows = reading?.Windows ?? [];
        if (windows.Count == 0)
        {
            if (reading?.Message is { Length: > 0 } message) providerReading.Children.Add(SettingsUi.Note(message));
            return;
        }
        var rows = new List<UIElement>(); string? group = null;
        foreach (var window in windows)
        {
            if (window.Group is { Length: > 0 } nextGroup && nextGroup != group)
            {
                var groupLabel = Ui.Text(nextGroup, 12, "#A6A6AA", FontWeights.SemiBold); groupLabel.Margin = new Thickness(20, 9, 20, 9); rows.Add(groupLabel);
            }
            group = window.Group;
            var row = new StackPanel { Margin = new Thickness(20, 9, 20, 9) };
            var summary = LimitFormatting.Summary(window);
            var label = Ui.Row(window.Name, summary); label.Margin = new Thickness(0); row.Children.Add(label);
            AutomationProperties.SetAutomationId(row, "provider.limit." + window.Id); AutomationProperties.SetName(row, window.Name + ", " + summary);
            if (window.UsedPercent is { } used && double.IsFinite(used))
            {
                var bar = new ProgressBar { Value = Math.Clamp(used, 0, 100), Margin = new Thickness(0, 6, 0, 0),
                    Style = (Style)Application.Current.FindResource("ProviderUsageProgress") };
                bar.SetResourceReference(Control.ForegroundProperty, used >= 70 ? "UsageCritical" : used >= 50 ? "UsageWatch" : "UsageAmple");
                AutomationProperties.SetName(bar, window.Name + " usage"); AutomationProperties.SetAutomationId(bar, "provider.limit.progress." + window.Id);
                row.Children.Add(bar);
            }
            if (window.ResetsAt is { } reset && reset > now)
            {
                var resetText = Ui.Text(ResetCopy.Text(reset, "Absolute", now), 11, "#A6A6AA"); resetText.Margin = new Thickness(0, 6, 0, 0); row.Children.Add(resetText);
            }
            rows.Add(row);
        }
        providerReading.Children.Add(SettingsUi.Section("Usage", rows.ToArray()));
        var nearest = windows.Select(x => x.ResetsAt).Where(x => x > now).Min();
        if (nearest is { } date)
        {
            var reset = ResetCopy.Text(date, "Absolute", now);
            providerReading.Children.Add(SettingsUi.Note("Nearest window resets " + (reset.StartsWith("Resets ", StringComparison.Ordinal) ? reset[7..] : reset) + "."));
        }
        if (reading?.Message is { Length: > 0 } note) providerReading.Children.Add(SettingsUi.Note(note));
    }
}
