using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task AccountBusyViewRegression(SavedAccounts accounts, CredentialVault vault, string root, Func<int> operationEntries, List<object> checks)
    {
        Require(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
            && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted", "Account UI fixtures require an isolated hosted runner.");
        var settings = new AppSettingsStore(); var previousSettings = settings.Current;
        var saved = vault.Load("accounts:codex");
        var missingExecutable = Path.Combine(root, "ui-missing-codex.exe");
        const string privateDiagnostic = "PRIVATE-FIXTURE-DIAGNOSTIC: synthetic-token-and-internal-path";
        const string otherSessionBusy = "Another CodeRim session is performing an account operation. Wait for it to finish.";
        const string saveFailure = "The official CLI could not verify a file-backed subscription login. Finish sign-in through the CLI and retry.";
        const string addFailure = "The new account could not be verified. Your current CLI login and saved accounts are unchanged.";
        Exception? failure = null; var cleanup = new List<Exception>();
        void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
        async Task View(string action, SavedAccounts service, Func<int> entries, string expected, string scenario, int expectedEntries = 1, bool pendingOperation = false)
        {
            DashboardStore? store = null; Window? window = null;
            try
            {
                store = new DashboardStore(settings, vault, synthetic: true);
                var pane = new AccountsPane("codex", vault, store, settings, service);
                window = new Window { Content = pane, Width = 720, Height = 500, ShowInTaskbar = false, Title = "Synthetic account busy view" };
                window.Show(); await Idle();
                var button = Descendants<Button>(pane).Single(control => AutomationProperties.GetAutomationId(control) == "accounts." + action);
                var status = Descendants<TextBlock>(pane).Single(text => AutomationProperties.GetAutomationId(text) == "accounts.status");
                var before = vault.Version("accounts:codex"); var path = SavedAccounts.Paths("codex").Credential; var credential = GuardedFile.Read(path); var entered = entries();
                var signInDirectories = Directory.EnumerateDirectories(Path.Combine(CompanionFile.DataDirectory, "vault"), "sign-in-*").Order(StringComparer.Ordinal).ToArray();
                Require(button.IsEnabled, "The account action was disabled before its routed event.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                for (var attempt = 0; attempt < 500 && !button.IsEnabled; attempt++) await Task.Delay(20);
                Require(button.IsEnabled && Descendants<Button>(pane).All(control => control.IsEnabled)
                    && SavedAccounts.OperationInProgress == pendingOperation, "Account action controls or local busy did not recover.");
                Require(Descendants<ScrollViewer>(pane).Single(control => AutomationProperties.GetAutomationId(control) == "accounts.list").Content is UIElement { IsEnabled: true }
                    && Descendants<Button>(pane).Single(control => Equals(control.Content, "Cancel sign-in")).Visibility == Visibility.Collapsed
                    && Descendants<Button>(pane).Single(control => Equals(control.Content, "Refresh Accounts")).Visibility == Visibility.Visible,
                    "Account action left the list disabled or retained sign-in progress controls.");
                Require(status.Text == expected && !status.Text.Contains(privateDiagnostic, StringComparison.Ordinal), "Account UI replaced the safe operation status or leaked an internal diagnostic.");
                Require(entries() == entered + expectedEntries, "Account UI entered the wrong number of actual lease acquisitions.");
                Require(before == vault.Version("accounts:codex") && credential == GuardedFile.Read(path)
                    && !File.Exists(missingExecutable)
                    && Directory.EnumerateDirectories(Path.Combine(CompanionFile.DataDirectory, "vault"), "sign-in-*").Order(StringComparer.Ordinal).SequenceEqual(signInDirectories),
                    "A denied account UI action changed credentials or created sign-in state.");
                checks.Add(new { scenario = "operation-ui-" + scenario, passed = true, interaction = "WPF routed event",
                    provider = "codex", syntheticStore = true, physicalInput = false, realAccount = false });
            }
            finally
            {
                Restore(() => window?.Close());
                try { store?.Dispose(); }
                catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            }
        }
        try
        {
            Require(saved is not null && !File.Exists(missingExecutable), "The account UI fixture preconditions are invalid.");
            settings.Save(previousSettings with { CodexExecutable = missingExecutable });
            await View("saveCurrent", accounts, operationEntries, otherSessionBusy, "save-busy");
            await View("add", accounts, operationEntries, otherSessionBusy, "add-busy");
            try
            {
                vault.Save("accounts:codex", "{malformed-synthetic-list");
                await View("add", accounts, operationEntries, otherSessionBusy, "add-busy-list-read-failure");
            }
            finally { Restore(() => vault.Save("accounts:codex", saved!)); }
            var diagnosticEntries = 0;
            var denied = new SavedAccounts(vault, () => { diagnosticEntries++; throw new IOException(privateDiagnostic); });
            await View("saveCurrent", denied, () => diagnosticEntries, saveFailure, "save-diagnostic-sanitized");
            await View("add", denied, () => diagnosticEntries, addFailure, "add-diagnostic-sanitized");

            using var cancellation = new CancellationTokenSource();
            var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var local = new SavedAccounts(vault, () => AccountOperationLease.Acquire(AccountOperationLease.Name + ".Test." + Guid.NewGuid().ToString("N")));
            var pending = local.SaveCurrentAsync("codex", missingExecutable, () => pause.Task.WaitAsync(cancellation.Token), cancellation.Token);
            try
            {
                Require(!pending.IsCompleted && SavedAccounts.OperationInProgress, "The independent local operation did not suspend.");
                await View("saveCurrent", accounts, operationEntries, "An account operation is already in progress.", "save-local-reentry-busy", expectedEntries: 0, pendingOperation: true);
            }
            finally
            {
                cancellation.Cancel(); pause.TrySetResult();
                try { await pending; }
                catch (OperationCanceledException) { }
                catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            Restore(() => { if (saved is null) vault.Delete("accounts:codex"); else vault.Save("accounts:codex", saved); });
            Restore(() => settings.Save(previousSettings));
        }
        if (cleanup.Count > 0) throw new AggregateException("Account UI fixture cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
