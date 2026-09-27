using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Services;
using CodeRim.Core.Domain;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;

namespace CodeRim.Windows.Views;

internal sealed partial class DashboardWindow
{
    private CheckBox? claudeEnabled;
    private Button? claudeRefresh;
    private StackPanel? claudeAccount;
    private string? claudeLayout;
    private bool updatingClaude;
    private string ClaudeStatus => !store.ClaudeAvailable || store.Claude.Busy ? store.Claude.Message
        : store.Claude.HelperError is { } helperError
            ? store.Readings.GetValueOrDefault("claude") is { Windows.Count: > 0 } ? "Showing last known Claude limits" : helperError
        : store.Readings.GetValueOrDefault("claude")?.Evaluated(DateTimeOffset.Now) is { } reading && reading.Windows.Count > 0
            ? reading.State == ReadingState.Stale ? "Use Claude Code to update limits" : "Claude limits updated"
            : store.Claude.Message;

    private void AddClaudeHeader(DockPanel header)
    {
        claudeEnabled = Ui.Toggle("", store.Claude.Preferences.Enabled, async value =>
        {
            if (updatingClaude) return;
            await store.Claude.SetEnabledAsync(value); UpdateClaude();
        });
        claudeEnabled.Margin = new Thickness(10, 0, 0, 0); claudeEnabled.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(claudeEnabled, "Enable Claude Code"); AutomationProperties.SetAutomationId(claudeEnabled, "claude.enabled");
        DockPanel.SetDock(claudeEnabled, Dock.Right); header.Children.Add(claudeEnabled);
    }
    private void AddClaudeAccount()
    {
        claudeAccount = new StackPanel(); claudeLayout = null; body.Children.Add(claudeAccount); UpdateClaude();
    }
    private async Task AddClaudeAsync()
    {
        var replace = false;
        try
        {
            if (!store.Synthetic && await Task.Run(ClaudeHookInstaller.HasOtherStatusLine))
            {
                replace = MessageBox.Show(this, "Replace your current status line? CodeRim will keep it for restoration when disconnected.", "Connect Claude", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                if (!replace) return;
            }
            await store.Claude.AddCurrentAccountAsync(replace);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { MessageBox.Show(this, "Unable to read Claude settings safely. Check file access and retry.", "CodeRim"); }
        UpdateClaude();
    }
    private static Button ClaudeAction(string title, string id, Func<Task> action)
    {
        var button = Ui.AsyncButton(title, action); button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(14, 9, 14, 9);
        AutomationProperties.SetAutomationId(button, id); return button;
    }
    private void UpdateClaude()
    {
        if (page != "claude" || claudeEnabled is null || claudeAccount is null) return;
        updatingClaude = true;
        try { claudeEnabled.IsChecked = store.Claude.Preferences.Enabled; claudeEnabled.IsEnabled = !store.Claude.ChangingConnection; }
        finally { updatingClaude = false; }
        if (claudeRefresh is not null)
        { claudeRefresh.Visibility = store.ClaudeAvailable ? Visibility.Visible : Visibility.Collapsed; claudeRefresh.IsEnabled = !store.Claude.Busy; }
        // Rebuild only when the account layout changes, leaving unrelated settings
        // and their focused controls intact during a periodic status check.
        var layout = !store.Claude.Preferences.Enabled ? "off" : store.ClaudeAvailable ? "connected" : store.Claude.DetectedAccount is null ? "missing" : "detected";
        if (claudeLayout != layout)
        {
            claudeLayout = layout; claudeAccount.Children.Clear(); var rows = new List<UIElement>();
            if (layout == "connected")
            {
                rows.Add(SettingsUi.Row("Account", ProviderValue("claude.account", "")));
                rows.Add(SettingsUi.Row("Plan", ProviderValue("claude.plan", "")));
                rows.Add(SettingsUi.Row("Limits", ProviderValue("claude.message", "")));
                rows.Add(SettingsUi.Action("Manage Accounts…", () => Navigate("claude-accounts")));
                rows.Add(ClaudeAction("Disconnect", "claude.disconnect", async () => { await store.Claude.DisconnectAsync(); UpdateClaude(); }));
            }
            else if (layout != "off")
            {
                rows.Add(SettingsUi.Row("Status", ProviderValue("claude.message", "")));
                if (layout == "detected") rows.Add(SettingsUi.Row("Detected account", ProviderValue("claude.detected", "")));
                rows.Add(ClaudeAction("Add Account", "claude.add", AddClaudeAsync));
            }
            if (rows.Count > 0) claudeAccount.Children.Add(SettingsUi.Section("Account", rows.ToArray()));
            if (layout == "missing") claudeAccount.Children.Add(SettingsUi.Note("Sign in with the `claude` command in your terminal, then choose Add Account."));
        }
        var display = store.AccountDisplay("claude");
        foreach (var label in VisualChildren<TextBlock>(body))
        {
            switch (AutomationProperties.GetAutomationId(label))
            {
                case "provider.status": label.Text = ClaudeStatus; break;
                case "claude.account": label.Text = display.Label ?? store.Claude.Account?.Email ?? "Not connected"; break;
                case "claude.plan": label.Text = display.Plan ?? ""; if (label.Parent is FrameworkElement row) row.Visibility = display.Plan is null ? Visibility.Collapsed : Visibility.Visible; break;
                case "claude.message": label.Text = ClaudeStatus; break;
                case "claude.detected": label.Text = store.Claude.DetectedAccount?.Email ?? ""; break;
            }
        }
        foreach (var action in VisualChildren<Button>(claudeAccount)) action.IsEnabled = !store.Claude.Busy;
    }
}
