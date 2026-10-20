using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class DashboardWindow
{
    private CheckBox? launchAtLoginToggle;
    private TextBlock? startupStatus;
    private FrameworkElement? startupSettings;
    private bool synchronizingStartup;

    private void AddStartupSection()
    {
        var status = StartupService.ReadStatus();
        launchAtLoginToggle = (CheckBox)SettingsUi.Toggle("Launch at Login", status.Enabled, enabled =>
        {
            if (synchronizingStartup) return;
            try { settings.Save(settings.Current with { LaunchAtLogin = enabled }, synchronizeStartup: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
            { MessageBox.Show(this, "Startup registration could not be changed. Check Windows startup settings and try again.", "CodeRim", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { RefreshStartupStatus(); }
        });
        startupStatus = Ui.Text(status.Text, 13, "#A6A6AA"); AutomationProperties.SetAutomationId(startupStatus, "startup.status");
        startupSettings = SettingsUi.Action("Open Startup Apps Settings", () => OpenUrl("ms-settings:startupapps"));
        body.Children.Add(SettingsUi.Section("Startup", launchAtLoginToggle, SettingsUi.Row("Status", startupStatus), startupSettings));
        RefreshStartupStatus();
    }

    internal void RefreshStartupStatus()
    {
        if (page != "general" || launchAtLoginToggle is null || startupStatus is null || startupSettings is null) return;
        var status = StartupService.ReadStatus(); synchronizingStartup = true;
        try
        {
            launchAtLoginToggle.IsChecked = status.Enabled; launchAtLoginToggle.IsEnabled = status.CanChange;
            startupStatus.Text = status.Text;
            startupSettings.Visibility = status.ShowSystemSettings ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { synchronizingStartup = false; }
    }
}
