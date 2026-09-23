using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

/// <summary>A persistent Mac-style provider row; refresh updates its text/actions in place.</summary>
internal sealed class ProviderAccountRow : DockPanel
{
    private readonly string id;
    private readonly string providerName;
    private readonly DashboardStore store;
    private readonly AppSettingsStore settings;
    private readonly TextBlock name;
    private readonly TextBlock secondary;
    private readonly ProviderMark mark;
    private readonly Button alert;
    private readonly Button primary;
    private readonly System.Windows.Shapes.Path bell;

    internal ProviderAccountRow(string id, DashboardStore store, AppSettingsStore settings, Action details, Action remove, Action<bool> mute)
    {
        this.id = id; this.store = store; this.settings = settings; providerName = ProviderCatalog.Find(id)!.Name;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(actions, Dock.Right); Children.Add(actions);
        alert = Ui.Button("", () => { mute(!settings.Current.MutedAlertProviders.Contains(id, StringComparer.Ordinal)); Refresh(); });
        alert.Width = 26; alert.MinHeight = alert.Height = 24; alert.Padding = new Thickness(4); alert.BorderThickness = new Thickness(0);
        alert.Background = Brushes.Transparent; alert.Margin = new Thickness(0, 0, 8, 0);
        bell = new System.Windows.Shapes.Path { Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.25,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        bell.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText"); alert.Content = bell;
        AutomationProperties.SetAutomationId(alert, "settings.providers.alerts." + id); actions.Children.Add(alert);
        primary = Ui.Button("Details", details); primary.FontSize = 11; primary.MinHeight = primary.Height = 24;
        primary.Padding = new Thickness(8, 2, 8, 2); primary.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetAutomationId(primary, "settings.providers.primary." + id); actions.Children.Add(primary);
        var removeButton = Ui.Button("", remove); removeButton.Width = removeButton.Height = removeButton.MinHeight = 20;
        removeButton.Padding = new Thickness(2); removeButton.BorderThickness = new Thickness(0); removeButton.Background = Brushes.Transparent; removeButton.Margin = new Thickness(0);
        var minus = new System.Windows.Shapes.Path { Data = Geometry.Parse("M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M4,8 H12"), Width = 16, Height = 16, StrokeThickness = 1.25 };
        minus.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText"); removeButton.Content = minus;
        removeButton.ToolTip = "Remove " + providerName + " from CodeRim";
        AutomationProperties.SetName(removeButton, "Remove " + providerName); AutomationProperties.SetAutomationId(removeButton, "settings.providers.remove." + id); actions.Children.Add(removeButton);
        mark = new ProviderMark { ProviderId = id, Margin = new Thickness(5.5) };
        var logo = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(6), Child = mark, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        logo.SetResourceReference(Border.BackgroundProperty, "ControlBackground"); DockPanel.SetDock(logo, Dock.Left); Children.Add(logo);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name = Ui.Text(providerName, 13); name.Margin = new Thickness(0); labels.Children.Add(name);
        secondary = Ui.Text("", 11, "#A6A6AA"); secondary.Margin = new Thickness(0, 1, 0, 0); secondary.MaxHeight = 32;
        secondary.TextWrapping = TextWrapping.Wrap; secondary.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(secondary, "provider-list." + id); labels.Children.Add(secondary); Children.Add(labels);
        Refresh();
    }
    internal void Refresh()
    {
        var display = store.AccountDisplay(id);
        var reading = display.Reading?.Evaluated(DateTimeOffset.Now);
        var identity = display.Label;
        var connected = id is "codex" or "claude" && identity is not null || reading?.Windows.Count > 0;
        string? headline = null;
        if (reading is { Windows.Count: > 0 })
        {
            var window = reading.Headline;
            if (window?.UsedPercent is { } used && double.IsFinite(used)) headline = LimitFormatting.Percent(used) + "% of " + window.Name;
            else if (window?.DisplayValue is { Length: > 0 } value) headline = value + " of " + window.Name;
        }
        var state = headline ?? reading?.State switch
        {
            ReadingState.Loading => "Checking…",
            ReadingState.Ready => "Connected — no reading yet",
            ReadingState.Stale => reading.UpdatedAt is { } date && date != DateTimeOffset.MinValue ? "Last read " + ElapsedCopy.Ago(date, DateTimeOffset.Now) : "Checking…",
            ReadingState.Disabled => "Disabled",
            ReadingState.Error or ReadingState.Unavailable or ReadingState.Unsupported => reading.Message ?? "Unavailable",
            _ => identity is not null ? "Signed in" : reading?.Message ?? "Not connected"
        };
        var account = string.Join(" · ", new[] { identity, display.Plan }.Where(x => !string.IsNullOrWhiteSpace(x)));
        secondary.Text = account.Length == 0 ? state : state is "Connected" or "Signed in" or "Not connected" ? account : account + " · " + state;
        secondary.ToolTip = secondary.Text;
        name.SetResourceReference(TextBlock.ForegroundProperty, connected ? "PrimaryText" : "SecondaryText");
        mark.SetResourceReference(ProviderMark.ForegroundProperty, connected ? "PrimaryText" : "SecondaryText");
        var muted = settings.Current.MutedAlertProviders.Contains(id, StringComparer.Ordinal);
        alert.Visibility = connected ? Visibility.Visible : Visibility.Collapsed; alert.IsEnabled = settings.Current.AlertsEnabled;
        bell.Data = Geometry.Parse("M3,11 Q5,9 5,6 A3,3 0 0 1 11,6 Q11,9 13,11 Z M6,13 Q8,16 10,13" + (muted ? " M1,1 L15,15" : ""));
        bell.Opacity = muted ? 0.55 : 1;
        alert.ToolTip = muted ? "Alerts for " + providerName + " are muted" : "Alert at 80% and 100% of a limit";
        AutomationProperties.SetName(alert, (muted ? "Unmute " : "Mute ") + providerName + " alerts");
        primary.Content = id is "codex" or "claude" || connected ? "Details" : "Set Up…";
        AutomationProperties.SetName(primary, primary.Content + " " + providerName);
        AutomationProperties.SetName(this, providerName + ", " + state);
    }
}
