using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Win32;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task SettingsReferenceRegression(DashboardWindow dashboard, AppSettingsStore settings, string directory)
    {
        var before = settings.Current; var registration = StartupService.ReadRegistration();
        using var approval = Registry.CurrentUser.CreateSubKey(StartupService.ApprovalKey, writable: true);
        var approvalBefore = approval.GetValue(StartupService.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var approvalKind = approvalBefore is null ? RegistryValueKind.Binary : approval.GetValueKind(StartupService.ValueName);
        var fixture = new Window { Owner = dashboard, Width = 180, Height = 100, ShowInTaskbar = false };
        var temporary = Path.Combine(CompanionFile.DataDirectory, "settings.json.new"); var createdTemporary = false;
        Exception? failure = null; var cleanupFailures = new List<Exception>();
        try
        {
            approval.DeleteValue(StartupService.ValueName, false);
            settings.Save(before with { LaunchAtLogin = false }, synchronizeStartup: true);
            dashboard.Navigate("general"); await Idle();
            CheckBox Toggle() => Descendants<CheckBox>(dashboard).Single(x => AutomationProperties.GetName(x) == "Launch at Login");
            string Status() => Descendants<TextBlock>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "startup.status").Text;
            var activationStates = new List<object>();
            async Task Reactivate()
            {
                activationStates.Add(new { phase = "before", fixtureActive = fixture.IsActive, dashboardActive = dashboard.IsActive,
                    check = Toggle().IsChecked, text = Status(), preference = settings.Current.LaunchAtLogin, actual = StartupService.ReadStatus(),
                    approval = approval.GetValue(StartupService.ValueName) is byte[] initialBytes ? Convert.ToHexString(initialBytes) : "not-binary-or-absent" });
                File.WriteAllText(Path.Combine(directory, "windows-startup-activation.json"), JsonSerializer.Serialize(activationStates));
                fixture.Show(); var fixtureAccepted = fixture.Activate();
                await MotionUntil(() => fixture.IsActive, "Startup fixture could not acquire native activation.");
                var dashboardAccepted = dashboard.Activate();
                await MotionUntil(() => dashboard.IsActive, "Settings window could not regain native activation.");
                await Idle();
                activationStates.Add(new { fixtureAccepted, dashboardAccepted, fixtureActive = fixture.IsActive, dashboardActive = dashboard.IsActive,
                    check = Toggle().IsChecked, toggleEnabled = Toggle().IsEnabled, text = Status(), preference = settings.Current.LaunchAtLogin,
                    actual = StartupService.ReadStatus(), approval = approval.GetValue(StartupService.ValueName) is byte[] bytes ? Convert.ToHexString(bytes) : "not-binary-or-absent" });
                File.WriteAllText(Path.Combine(directory, "windows-startup-activation.json"), JsonSerializer.Serialize(activationStates));
            }
            Require(Toggle().IsChecked == false && Status() == "Disabled", "Missing startup registration displayed as enabled.");
            StartupService.SetEnabled(true); await Reactivate();
            Require(Toggle().IsChecked == true && Status() == "Enabled" && !settings.Current.LaunchAtLogin, "Activation failed to read external registration, or wrote preferences while refreshing.");
            settings.Save(settings.Current with { LaunchAtLogin = true });
            StartupService.SetEnabled(false); await Reactivate();
            Require(Toggle().IsChecked == false && settings.Current.LaunchAtLogin, "Removed OS registration was hidden by a stale saved preference.");
            Require(new AppSettingsStore().Current.LaunchAtLogin && !StartupService.ReadStatus().Enabled, "Reloading a saved preference recreated removed startup registration.");
            Toggle().IsChecked = true; await Idle();
            Require(StartupService.ReadStatus().Enabled, "Explicit enable did not repair registration when the saved preference was already true.");
            byte[] blocked = [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
            approval.SetValue(StartupService.ValueName, blocked, RegistryValueKind.Binary); await Reactivate();
            Require(!Toggle().IsEnabled && Toggle().IsChecked == false && Status() == "Disabled in Windows Settings", "Windows-disabled startup was displayed as enabled.");
            Require(Descendants<Button>(dashboard).Single(x => AutomationProperties.GetName(x) == "Open Startup Apps Settings").IsVisible,
                "OS-disabled startup has no system settings recovery action.");
            settings.Save(settings.Current with { ShowLastUpdated = !settings.Current.ShowLastUpdated });
            Require(((byte[])approval.GetValue(StartupService.ValueName)!).SequenceEqual(blocked), "Reading or saving unrelated options overwrote Windows startup approval.");
            approval.SetValue(StartupService.ValueName, new byte[] { 255 }, RegistryValueKind.Binary); await Reactivate();
            Require(Status() == "Check Windows startup settings" && Toggle().IsChecked == false, "Unknown approval format was assumed enabled.");
            Require(!File.Exists(temporary) && !Directory.Exists(temporary), "Startup rollback fixture path already exists.");
            Directory.CreateDirectory(temporary); createdTemporary = true;
            var commandBeforeFailure = StartupService.ReadRegistration(); var rejected = false;
            try { settings.Save(settings.Current with { LaunchAtLogin = false }, synchronizeStartup: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
            Require(rejected && Equals(StartupService.ReadRegistration(), commandBeforeFailure), "Failed settings persistence changed the original startup command.");
            Directory.Delete(temporary); createdTemporary = false;

            fixture.Hide(); dashboard.Navigate("about"); await Idle();
            var links = Descendants<TextBlock>(dashboard).SelectMany(x => x.Inlines.OfType<Hyperlink>()).ToArray();
            Require(links.Length == 4 && links.Any(x => AutomationProperties.GetName(x) == "Codenotch - MIT License"), "Information links differ from the Mac project section.");
            Require(links.All(x => x.NavigateUri.IsFile || x.NavigateUri.Scheme == "https" && x.NavigateUri.Host == "github.com"), "Information link has an unexpected destination.");
            Require(Descendants<TextBlock>(dashboard).Any(x => x.Text == "Build")
                && Descendants<TextBlock>(dashboard).Any(x => x.Text.Contains("not affiliated with or endorsed by", StringComparison.Ordinal)), "Information is missing build identity or the independent-project notice.");
            if (InstallerUpdateCoordinator.IsManaged)
                Require(links.Count(x => x.NavigateUri.IsFile && File.Exists(x.NavigateUri.LocalPath)) == 2, "Installed application is missing bundled license notices.");
            Capture(dashboard, Path.Combine(directory, "windows-information-reference.png"));
            File.WriteAllText(Path.Combine(directory, "windows-settings-reference.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Actual OS startup registration refreshes on activation", "Stale saved preference cannot block explicit registration repair",
                    "Windows approval is read-only; unknown status stays explicit", "Settings write failure restores the exact registration", "Information build, four project links, notices and MSI bundled licenses" } }));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action)
            {
                try { action(); }
                catch (Exception error) when (error is not OutOfMemoryException) { cleanupFailures.Add(error); }
            }
            Restore(fixture.Close);
            Restore(() => { if (createdTemporary) Directory.Delete(temporary); });
            Restore(() => settings.Save(before));
            Restore(() => StartupService.Restore(registration));
            Restore(() =>
            {
                if (approvalBefore is null) approval.DeleteValue(StartupService.ValueName, false);
                else approval.SetValue(StartupService.ValueName, approvalBefore, approvalKind);
            });
            Restore(() => dashboard.Navigate("usage"));
        }
        if (cleanupFailures.Count > 0) throw new AggregateException("Native settings fixture cleanup failed.", failure is null ? cleanupFailures : cleanupFailures.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
