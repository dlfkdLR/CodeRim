using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal sealed class AccountsPane : DockPanel
{
    private Process? signInProcess;
    private readonly System.Windows.Threading.DispatcherTimer signInTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Windows.Controls.Button cancel;
    private readonly System.Windows.Controls.Button refresh;
    private readonly string provider;
    private readonly SavedAccounts accounts;
    private readonly DashboardStore store;
    private readonly AppSettingsStore settings;
    private readonly StackPanel list = new();
    private readonly TextBlock feedback = Ui.Text("", 12, "#A6A6AA");
    internal AccountsPane(string provider, CredentialVault vault, DashboardStore store, AppSettingsStore settings)
    {
        this.provider = provider; this.store = store; this.settings = settings; accounts = new SavedAccounts(vault);
        Margin = new Thickness(24); LastChildFill = true;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); Children.Add(header);
        header.Children.Add(Ui.Text((provider == "codex" ? "Codex" : "Claude") + " Accounts", 17, weight: FontWeights.SemiBold));
        header.Children.Add(Ui.Text("Save the CLI's current sign-in, then switch between saved accounts.", color: "#A6A6AA"));
        header.Margin = new Thickness(0, 0, 14, 14);
        var footer = new StackPanel();
        System.Windows.Automation.AutomationProperties.SetAutomationId(footer, "accounts.footer");
        DockPanel.SetDock(footer, Dock.Bottom); Children.Add(footer);
        var divider = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 12) };
        divider.SetResourceReference(Border.BackgroundProperty, "DividerBrush"); footer.Children.Add(divider);
        var actions = new WrapPanel();
        var save = Ui.AsyncButton("Save Current Account", async () =>
        {
            try { await accounts.SaveCurrentAsync(provider, Executable(), waitForRefresh: () => store.WaitForProviderIdleAsync(provider)).ConfigureAwait(true); Populate(); feedback.Text = "Verified CLI account saved using Windows user encryption."; }
            catch (Exception error) when (error is not OutOfMemoryException) { feedback.Text = "The official CLI could not verify a file-backed subscription login. Finish sign-in through the CLI and retry."; }
        });
        System.Windows.Automation.AutomationProperties.SetAutomationId(save, "accounts.saveCurrent"); actions.Children.Add(save);
        var add = Ui.Button("Add Account…", () => Run(SignIn));
        System.Windows.Automation.AutomationProperties.SetAutomationId(add, "accounts.add"); actions.Children.Add(add);
        refresh = Ui.Button("Refresh Accounts", Populate); actions.Children.Add(refresh);
        cancel = Ui.Button("Cancel sign-in", CancelSignIn); cancel.Visibility = Visibility.Collapsed; actions.Children.Add(cancel);
        footer.Children.Add(actions); footer.Children.Add(feedback);
        System.Windows.Automation.AutomationProperties.SetAutomationId(feedback, "accounts.status");
        var note = Ui.Text("Close running provider sessions before switching. Removing a saved account preserves the current CLI login and local history.", 11, "#A6A6AA");
        note.Margin = new Thickness(0, 10, 0, 0); footer.Children.Add(note);
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        System.Windows.Automation.AutomationProperties.SetAutomationId(scroll, "accounts.list");
        Children.Add(scroll);
        signInTimer.Tick += (_, _) =>
        {
            if (signInProcess is null || !signInProcess.HasExited) return;
            signInProcess.Dispose(); signInProcess = null; signInTimer.Stop(); ShowSignInProgress(false);
            feedback.Text = "Sign-in process finished. Save Current Account to verify and keep this login."; Populate();
        };
        Unloaded += (_, _) => { signInTimer.Stop(); signInProcess?.Dispose(); signInProcess = null; };
        Populate();
    }
    internal void ShowSignInProgress(bool active)
    {
        cancel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        refresh.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
    }
    private string Executable() => provider == "codex" ? settings.Current.CodexExecutable ?? ProviderConnections.ResolveCodex() ?? throw new FileNotFoundException()
        : ProviderConnections.ResolveExecutable("claude.exe") ?? throw new FileNotFoundException();
    private void SignIn()
    {
        if (SavedAccounts.OperationInProgress) throw new InvalidOperationException("Wait for the current account operation to finish.");
        var executable = Executable();
        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        foreach (var argument in provider == "codex" ? new[] { "login" } : new[] { "auth", "login" }) start.ArgumentList.Add(argument);
        if (signInProcess is { HasExited: false }) { feedback.Text = "Sign-in is already in progress."; return; }
        signInProcess?.Dispose(); signInProcess = Process.Start(start);
        ShowSignInProgress(signInProcess is not null); signInTimer.Start();
        feedback.Text = "Finish sign-in in the official CLI, then choose Save Current Account.";
    }
    private void CancelSignIn()
    {
        try { if (signInProcess is { HasExited: false }) signInProcess.Kill(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        signInProcess?.Dispose(); signInProcess = null; signInTimer.Stop(); ShowSignInProgress(false);
        feedback.Text = "Sign-in cancelled. Previously saved accounts are preserved.";
    }
    private void Populate()
    {
        list.Children.Clear();
        try
        {
            var saved = accounts.Read(provider);
            string? current = null;
            try { if (!store.Synthetic) current = SavedAccounts.Current(provider).Identity.Id; } catch (Exception e) when (e is IOException or System.Text.Json.JsonException or FormatException) { }
            if (saved.Count == 0)
            {
                list.Children.Add(Ui.Text("No saved accounts", weight: FontWeights.SemiBold));
                list.Children.Add(Ui.Text("Save your current login, or sign in to add another account.", color: "#A6A6AA"));
            }
            foreach (var account in saved)
            {
                var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
                panel.Children.Add(Ui.Text(account.Identity.Email + (current == account.Identity.Id ? " · CLI login file" : ""), 15, weight: FontWeights.SemiBold));
                panel.Children.Add(Ui.Text((account.Identity.Plan ?? "Subscription") + " · " + account.Identity.Organization, 11, "#A6A6AA"));
                var actions = new WrapPanel();
                var select = Ui.AsyncButton("Use account", async () =>
                {
                    if (MessageBox.Show(Window.GetWindow(this), "Switch the CLI to " + account.Identity.Email + "? Close its running sessions before continuing.", "Switch account", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    feedback.Text = "Verifying account…";
                    try
                    {
                        if (store.Sessions.Any(x => x.Provider == provider && x.State is "busy" or "waiting")) throw new InvalidOperationException("Close the provider's active sessions first.");
                        store.InvalidateAccount(provider);
                        await accounts.SwitchAsync(account, Executable(), waitForRefresh: () => store.WaitForProviderIdleAsync(provider)).ConfigureAwait(true);
                        store.InvalidateAccount(provider); await store.RefreshProviderAsync(provider).ConfigureAwait(true);
                        feedback.Text = "The CLI verified the selected account."; Populate();
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        store.InvalidateAccount(provider); await store.RefreshProviderAsync(provider).ConfigureAwait(true);
                        feedback.Text = "The switch could not be verified. Close running provider sessions, sign in through the official CLI, and retry.";
                    }
                });
                System.Windows.Automation.AutomationProperties.SetName(select, "Switch to " + account.Identity.Email);
                actions.Children.Add(select);
                var remove = Ui.Button("Remove", () => Run(() => { if (MessageBox.Show(Window.GetWindow(this), "Remove the saved login for " + account.Identity.Email + "? Its current CLI session and usage history are preserved.", "Remove saved account", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { accounts.Remove(provider, account.Identity.Id); Populate(); } }));
                System.Windows.Automation.AutomationProperties.SetName(remove, "Remove saved account " + account.Identity.Email);
                actions.Children.Add(remove);
                panel.Children.Add(actions); list.Children.Add(panel);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { feedback.Text = "Saved accounts could not be read. Check access to the Windows user credential store."; }
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        { feedback.Text = SavedAccounts.OperationInProgress ? "Wait for the current account operation to finish, then retry." : "A complete CLI subscription login is required. Finish sign-in through the official provider CLI, then retry."; }
    }
}
