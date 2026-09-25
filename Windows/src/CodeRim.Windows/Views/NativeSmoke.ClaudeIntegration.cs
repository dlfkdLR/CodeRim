using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CodeRim.Core.Services;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private sealed class ClaudeFixtureOperations(Func<LoginIdentity?> read) : IClaudeIntegrationOperations
    {
        public int Probes, Installs, Uninstalls;
        public bool FailInstall, FailUninstall;
        public string? CurrentAccountId => read()?.Id;
        public Task<LoginIdentity?> ProbeAsync(CancellationToken token) { Probes++; return Task.FromResult(read()); }
        public Task InstallAsync(bool replaceExisting, CancellationToken token)
        { Installs++; return FailInstall ? Task.FromException(new IOException("synthetic helper failure")) : Task.CompletedTask; }
        public Task UninstallAsync(CancellationToken token)
        { Uninstalls++; return FailUninstall ? Task.FromException(new IOException("synthetic uninstall failure")) : Task.CompletedTask; }
    }
    private static async Task ClaudeIntegrationRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var before = settings.Current; DashboardStore? store = null; DashboardWindow? window = null;
        var checks = new List<string>(); Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            settings.Save(before with { ClaudeIntegration = new(false, null), EnabledProviders = [], UsageProvider = "codex",
                AutomaticRefresh = false, RefreshIntervalSeconds = 0, ProfileSyncEnabled = false, AccountLimitsEnabled = false, CompletionSound = false });
            LoginIdentity? current = new("fixture-claude-a", "a@example.invalid", "org-a", "max");
            var operations = new ClaudeFixtureOperations(() => current) { FailInstall = true };
            var integration = new ClaudeIntegration(settings.Current.ClaudeIntegration!, operations, (next, codex) =>
                settings.Save(settings.Current with { ClaudeIntegration = next, UsageProvider = codex ? "codex" : settings.Current.UsageProvider }));
            store = new DashboardStore(settings, vault, synthetic: true, claudeIntegration: integration);
            await store.RefreshAsync();
            window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate("usage"); await Idle();
            var pane = Descendants<UsagePane>(window).Single();
            Require(store.AvailableUsageProviders.SequenceEqual(["codex"]) && operations.Probes == 0, "A fresh disabled integration probed Claude or hid Codex.");
            pane.HandleShortcut(Key.D2, ModifierKeys.Control | ModifierKeys.Shift); await Idle();
            Require(settings.Current.UsageProvider == "codex", "Keyboard selection bypassed Claude availability.");
            window.Navigate("claude"); await Idle();
            CheckBox Toggle() => Descendants<CheckBox>(window).Single(x => AutomationProperties.GetAutomationId(x) == "claude.enabled");
            Button Action(string id) => Descendants<Button>(window).Single(x => AutomationProperties.GetAutomationId(x) == id);
            Require(Toggle().IsChecked == false && !Descendants<Button>(window).Any(x => AutomationProperties.GetAutomationId(x) == "claude.add"),
                "Disabled Claude settings expose connected-account controls.");
            Capture(window, Path.Combine(directory, "windows-claude-disabled.png"));
            Toggle().IsChecked = true; await WaitClaude();
            Require(integration.DetectedAccount?.Id == current.Id && !integration.Available && integration.Preferences.LinkedAccountId is null && operations.Installs == 0,
                "Enabling Claude implicitly approved the detected account.");
            Capture(window, Path.Combine(directory, "windows-claude-detected.png"));
            Action("claude.add").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitClaude();
            Require(integration.Available && integration.HelperError is not null && new AppSettingsStore().Current.ClaudeIntegration?.LinkedAccountId == current.Id,
                "Helper failure removed local Usage availability or link persistence failed.");
            await store.WaitForProviderIdleAsync("claude"); await store.WaitForLocalIdleAsync(); window.Navigate("claude"); await Idle();
            Require(store.AccountDisplay("claude").Reading?.State == ReadingState.Stale && Descendants<TextBlock>(window).Any(x => x.Text == "Showing last known Claude limits"),
                "A ready cached quota concealed the helper failure.");
            store.Readings.Remove("claude"); window.Navigate("claude"); await Idle();
            Require(Descendants<TextBlock>(window).Any(x => x.Text == integration.HelperError), "Helper failure is missing from connected Claude settings.");
            Capture(window, Path.Combine(directory, "windows-claude-connected-helper-failure.png"));
            checks.Add("Disabled, detected, explicit Add, persisted link and helper-failure local Usage");
            window.Navigate("usage"); await Idle(); pane = Descendants<UsagePane>(window).Single(); pane.SelectProvider("claude"); await Idle();
            Require(settings.Current.UsageProvider == "claude" && store.AvailableUsageProviders.SequenceEqual(["codex", "claude"]),
                "Notch membership controls available Usage integrations.");
            Descendants<Button>(pane).Single(x => x.Content as string == "Switch").Focus();
            current = new("fixture-claude-b", "b@example.invalid", "org-b", "pro");
            await integration.RefreshAsync(); await Idle();
            Require(settings.Current.UsageProvider == "claude" && !integration.Available
                && !store.Readings.ContainsKey("claude"), "External owner change retained old quotas or forced a different selection.");
            var selector = Descendants<UsageProviderPicker>(pane).Single(); selector.IsDropDownOpen = true; await Idle();
            var host = (Border)selector.Template.FindName("PART_ProviderContent", selector);
            Require(!Descendants<Button>(host).Any(x => AutomationProperties.GetAutomationId(x) == "menu.provider.claude"), "Unavailable selected Claude remained selectable.");
            selector.IsDropDownOpen = false;
            Require(Descendants<Button>(pane).Any(x => x.Content as string == "Open Claude Settings"), "Focused Usage retained stale content after losing its owner.");
            pane.SelectProvider("codex"); await Idle();
            Require(selector.Items.Count == 1 && !selector.IsTextSearchEnabled, "Leaving an unavailable selection retained a base-ComboBox selection bypass.");
            foreach (var key in new[] { Key.End, Key.Right })
            {
                var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(selector)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                selector.RaiseEvent(input);
                if (!input.Handled) { input.RoutedEvent = Keyboard.KeyDownEvent; selector.RaiseEvent(input); }
                await Idle();
                Require(Equals(selector.SelectedValue, "codex") && settings.Current.UsageProvider == "codex", "Closed-picker keyboard selection diverged from displayed Usage.");
            }
            checks.Add("Hidden notch retains Usage; external owner invalidates focused content, preserves unavailable selection and blocks closed-picker bypasses");
            window.Navigate("claude"); await Idle(); Action("claude.add").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitClaude();
            operations.FailUninstall = true; Toggle().IsChecked = false; await WaitClaude();
            Require(integration.Preferences.Enabled && Toggle().IsChecked == true && integration.Available,
                "Failed uninstall falsely turned off Claude.");
            operations.FailUninstall = false; Toggle().IsChecked = false; await WaitClaude();
            Require(!integration.Preferences.Enabled && integration.Preferences.LinkedAccountId == current.Id && settings.Current.UsageProvider == "codex",
                "Disable removed the approved owner or failed to return Usage to Codex.");
            Toggle().IsChecked = true; await WaitClaude(); Require(integration.Available, "Re-enabling did not recover the same approved owner.");
            Action("claude.disconnect").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitClaude();
            Require(integration.Preferences.Enabled && integration.Preferences.LinkedAccountId is null && !integration.Available,
                "Disconnect did not preserve enabled state and remove only the link.");
            checks.Add("Uninstall failure, disable/re-enable ownership retention and explicit Disconnect");
            using (var hostTimer = new ClaudeIntegrationHost(settings, synthetic: true, supplied: new ClaudeIntegration(new(true, null), operations, (_, _) => { })))
            {
                hostTimer.Start(); await hostTimer.Integration.RefreshAsync(); var probes = operations.Probes;
                hostTimer.Start(); Require(hostTimer.Polling && hostTimer.PollInterval == TimeSpan.FromSeconds(120), "Claude poll interval or single-start contract changed.");
                await hostTimer.PollAsync(); Require(operations.Probes == probes + 1, "General refresh OFF or notch hidden suppressed independent Claude polling.");
                hostTimer.Dispose(); Require(!hostTimer.Polling, "Claude timer survived disposal.");
            }
            checks.Add("Single 120-second poll, independent of General refresh and notch visibility, stops on dispose");
            File.WriteAllText(Path.Combine(directory, "windows-claude-integration.json"), JsonSerializer.Serialize(new { completed = true, checks }, JsonOptions));
            async Task WaitClaude()
            {
                for (var attempt = 0; attempt < 100; attempt++) { await Idle(); if (!integration.Busy) return; await Task.Delay(10); }
                throw new InvalidOperationException("Claude fixture did not finish its operation.");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { window?.Close(); } catch (Exception error) { cleanup.Add(error); }
            try { var idle = store?.WaitForLocalIdleAsync() ?? Task.CompletedTask; var remote = store?.WaitForProviderIdleAsync("claude") ?? Task.CompletedTask; store?.Dispose(); await Task.WhenAll(idle, remote); } catch (Exception error) { cleanup.Add(error); }
            try { settings.Save(before); } catch (Exception error) { cleanup.Add(error); }
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Claude integration fixture and cleanup failed", cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Claude integration cleanup failed", cleanup);
    }
    private static async Task ClaudeMigrationRegression(CredentialVault vault, string directory)
    {
        var variables = new[] { "CODERIM_DATA_DIR", "CLAUDE_CONFIG_DIR", "CODEX_HOME" };
        var original = variables.ToDictionary(key => key, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        var root = Path.Combine(Path.GetTempPath(), "coderim-claude-migration-" + Guid.NewGuid().ToString("N"));
        DashboardStore? store = null; Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            Directory.CreateDirectory(root); CredentialVault.RestrictDirectory(root);
            var data = Path.Combine(root, "data"); var config = Path.Combine(root, "claude"); var codexHome = Path.Combine(root, "codex");
            Directory.CreateDirectory(data); Directory.CreateDirectory(config); Directory.CreateDirectory(codexHome);
            Environment.SetEnvironmentVariable("CODERIM_DATA_DIR", data); Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", config); Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            var settings = new AppSettingsStore();
            Require(settings.Current.ClaudeIntegration is { Enabled: false, LinkedAccountId: null }, "Fresh install did not start disabled.");
            GuardedFile.WritePrivate(Path.Combine(config, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"fixture-access","refreshToken":"fixture-refresh","expiresAt":4102444800000,"scopes":["user:inference"]}}""");
            GuardedFile.WritePrivate(Path.Combine(config, ".claude.json"), """{"oauthAccount":{"emailAddress":"fixture@example.invalid","organizationUuid":"fixture-org","accountUuid":"fixture-user"}}""");
            var identity = SavedAccounts.Current("claude").Identity;
            ClaudeHookInstaller.Install();
            var cache = Path.Combine(data, "claude-limits.json");
            File.WriteAllText(cache, JsonSerializer.Serialize(new { accountScope = identity.Id }));
            AppSettings Legacy() => settings.Current with { ClaudeIntegration = null, EnabledProviders = ["claude"], AccountLimitsEnabled = false, ProfileSyncEnabled = false };
            settings.Save(Legacy()); var migrated = ClaudeIntegrationHost.ReadPreferences(settings);
            Require(migrated.Preferences == new ClaudeIntegrationPreferences(true, identity.Id) && migrated.Error is null,
                "Exact owned helper and matching account cache did not restore the legacy link.");
            settings.Save(settings.Current with { ClaudeIntegration = new(false, null) });
            Require(!ClaudeIntegrationHost.ReadPreferences(settings).Preferences.Enabled, "Migration overrode explicit disabled settings.");
            foreach (var value in new[] { "[]", "null", "123", "{}", "{\"accountScope\":\"different-owner\"}", new string(' ', 262145) })
            {
                settings.Save(Legacy()); File.WriteAllText(cache, value);
                var result = ClaudeIntegrationHost.ReadPreferences(settings);
                Require(result.Preferences.Enabled && result.Preferences.LinkedAccountId is null, "Malformed/foreign cache linked an account or crashed migration.");
            }
            File.WriteAllText(cache, JsonSerializer.Serialize(new { accountScope = identity.Id }));
            var journal = ClaudeHookInstaller.SettingsPath + ".coderim-state.json";
            var journalText = File.ReadAllText(journal); File.Delete(journal); settings.Save(Legacy());
            Require(ClaudeIntegrationHost.ReadPreferences(settings).Preferences.LinkedAccountId is null, "Cache alone authorized a legacy account.");
            File.WriteAllText(journal, journalText); settings.Save(Legacy());
            Directory.CreateDirectory(Path.Combine(data, "settings.json.new"));
            var blocked = ClaudeIntegrationHost.ReadPreferences(settings);
            Require(!blocked.Preferences.Enabled && blocked.Error is not null && settings.Current.ClaudeIntegration is null,
                "Failed migration save claimed a successful activation.");
            Directory.Delete(Path.Combine(data, "settings.json.new"));
            settings.Save(settings.Current with { ClaudeIntegration = new(true, identity.Id), EnabledProviders = ["claude"],
                AccountLimitsEnabled = false, ProfileSyncEnabled = false, CompletionSound = false });
            var operations = new ClaudeFixtureOperations(() => identity);
            var integration = new ClaudeIntegration(settings.Current.ClaudeIntegration!, operations,
                (next, codex) => settings.Save(settings.Current with { ClaudeIntegration = next, UsageProvider = codex ? "codex" : settings.Current.UsageProvider }));
            store = new DashboardStore(settings, vault, providerConnections: new ProviderConnections(vault), claudeIntegration: integration);
            await integration.RefreshAsync(); await store.RefreshAsync(); await store.WaitForProviderIdleAsync("claude");
            for (var attempt = 0; store.IsRefreshing && attempt < 100; attempt++) { await Idle(); await Task.Delay(10); }
            Require(!store.IsRefreshing, "Claude fixture scans did not finish before the write-failure check.");
            if (File.Exists(CompanionFile.SnapshotPath)) File.Delete(CompanionFile.SnapshotPath);
            Directory.CreateDirectory(CompanionFile.SnapshotPath);
            Require(await integration.SetEnabledAsync(false) && !integration.Busy && !integration.Available,
                "Companion write failure stranded a production store in Busy or blocked disable.");
            Directory.Delete(CompanionFile.SnapshotPath);
            File.WriteAllText(Path.Combine(directory, "windows-claude-migration.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Fresh off and explicit off", "Legacy exact owned journal plus matching owner", "Non-object, oversized and foreign cache remain unlinked",
                    "Cache alone cannot approve", "Settings write failure reports incomplete activation", "Production companion write failure cannot strand disable" } }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { var idle = store?.WaitForLocalIdleAsync() ?? Task.CompletedTask; var remote = store?.WaitForProviderIdleAsync("claude") ?? Task.CompletedTask; store?.Dispose(); await Task.WhenAll(idle, remote); } catch (Exception error) { cleanup.Add(error); }
            foreach (var (key, value) in original) try { Environment.SetEnvironmentVariable(key, value); } catch (Exception error) { cleanup.Add(error); }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception error) { cleanup.Add(error); }
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Claude migration fixture and cleanup failed", cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Claude migration cleanup failed", cleanup);
    }

}
