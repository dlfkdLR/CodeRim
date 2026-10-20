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
    private CancellationTokenSource? signInCancellation;
    private CancellationTokenSource? accountRefreshCancellation;
    private Task? accountRefresh;
    private readonly WrapPanel actions = new();
    private bool busy;
    private int refreshRevision;
    private string? verifiedCurrentId;
    private Window? owner;
    private readonly System.Windows.Controls.Button cancel;
    private readonly System.Windows.Controls.Button refresh;
    private readonly string provider;
    private readonly SavedAccounts accounts;
    private readonly DashboardStore store;
    private readonly AppSettingsStore settings;
    private readonly StackPanel list = new();
    private readonly TextBlock feedback = Ui.Text("", 12, "#A6A6AA");
    internal AccountsPane(string provider, CredentialVault vault, DashboardStore store, AppSettingsStore settings, SavedAccounts? savedAccounts = null)
    {
        this.provider = provider; this.store = store; this.settings = settings; accounts = savedAccounts ?? new SavedAccounts(vault);
        Margin = new Thickness(24); LastChildFill = true;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); Children.Add(header);
        header.Children.Add(Ui.Text((provider == "codex" ? "Codex" : "Claude") + " Accounts", 17, weight: FontWeights.SemiBold));
        header.Children.Add(Ui.Text(provider == "codex" ? "Select an account for Codex and its CLI."
            : "Select an account for the Claude Code CLI. Claude Desktop keeps its own sign-in.", color: "#A6A6AA"));
        header.Margin = new Thickness(0, 0, 0, 14);
        var footer = new StackPanel();
        System.Windows.Automation.AutomationProperties.SetAutomationId(footer, "accounts.footer");
        DockPanel.SetDock(footer, Dock.Bottom); Children.Add(footer);
        var divider = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 12) };
        divider.SetResourceReference(Border.BackgroundProperty, "DividerBrush"); footer.Children.Add(divider);
        var save = Ui.AsyncButton("Save Current Account", async () =>
        {
            SetBusy(true);
            try { await WaitForCurrentProbeAsync(); await accounts.SaveCurrentAsync(provider, Executable(), waitForRefresh: () => store.WaitForProviderIdleAsync(provider)).ConfigureAwait(true); await RefreshAccountsAsync(); feedback.Text = "Current account saved."; }
            catch (Exception error) when (error is not OutOfMemoryException) { feedback.Text = error is AccountOperationBusyException blocked ? blocked.UserMessage : "The official CLI could not verify a file-backed subscription login. Finish sign-in through the CLI and retry."; }
            finally { SetBusy(false); }
        });
        System.Windows.Automation.AutomationProperties.SetAutomationId(save, "accounts.saveCurrent"); actions.Children.Add(save);
        var add = Ui.AsyncButton("Add Account…", SignInAsync);
        System.Windows.Automation.AutomationProperties.SetAutomationId(add, "accounts.add"); actions.Children.Add(add);
        refresh = Ui.AsyncButton("Refresh Accounts", RefreshAccountsAsync); actions.Children.Add(refresh);
        cancel = Ui.Button("Cancel sign-in", CancelSignIn); cancel.Visibility = Visibility.Collapsed; actions.Children.Add(cancel);
        footer.Children.Add(actions); footer.Children.Add(feedback);
        System.Windows.Automation.AutomationProperties.SetAutomationId(feedback, "accounts.status");
        var note = Ui.Text("Close running provider sessions before switching. Removing a saved account preserves the current CLI login and local history.", 11, "#A6A6AA");
        note.Margin = new Thickness(0, 10, 0, 0); footer.Children.Add(note);
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        System.Windows.Automation.AutomationProperties.SetAutomationId(scroll, "accounts.list");
        Children.Add(scroll);
        Loaded += (_, _) => { owner = Window.GetWindow(this); if (owner is not null) owner.Activated += Activated; _ = RefreshAccountsAsync(); };
        Unloaded += (_, _) => { refreshRevision++; accountRefreshCancellation?.Cancel(); signInCancellation?.Cancel(); if (owner is not null) owner.Activated -= Activated; owner = null; };
        Populate();
    }
    internal void ShowSignInProgress(bool active)
    {
        SetBusy(active);
        cancel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        refresh.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
    }
    private string Executable() => provider == "codex" ? settings.Current.CodexExecutable ?? ProviderConnections.ResolveCodex() ?? throw new FileNotFoundException()
        : ProviderConnections.ResolveExecutable("claude.exe") ?? throw new FileNotFoundException();
    private async Task SignInAsync()
    {
        if (busy || SavedAccounts.OperationInProgress) { feedback.Text = "Wait for the current account operation to finish."; return; }
        using var cancellation = new CancellationTokenSource(); signInCancellation = cancellation;
        string? busyMessage = null;
        ShowSignInProgress(true);
        try
        {
            await WaitForCurrentProbeAsync();
            feedback.Text = "Finish sign-in in your browser. Your current CLI account stays selected.";
            await accounts.AddAsync(provider, Executable(), cancellation.Token).ConfigureAwait(true);
            await RefreshAccountsAsync(); feedback.Text = "Account added. Select Switch when you want to use it.";
        }
        catch (OperationCanceledException) { feedback.Text = "Sign-in cancelled. Your current account is unchanged."; }
        catch (AccountOperationBusyException error) { busyMessage = error.UserMessage; feedback.Text = busyMessage; }
        catch (Exception e) when (e is not OutOfMemoryException) { feedback.Text = "The new account could not be verified. Your current CLI login and saved accounts are unchanged."; }
        finally { signInCancellation = null; ShowSignInProgress(false); Populate(); if (busyMessage is not null) feedback.Text = busyMessage; }
    }
    private void CancelSignIn() => signInCancellation?.Cancel();
    private void SetBusy(bool active)
    {
        busy = active; if (active) { refreshRevision++; accountRefreshCancellation?.Cancel(); } list.IsEnabled = !active;
        foreach (var button in actions.Children.OfType<System.Windows.Controls.Button>()) button.IsEnabled = !active || button == cancel;
    }
    private async void Activated(object? sender, EventArgs e)
    {
        if (!busy) await RefreshAccountsAsync();
    }
    private async Task WaitForCurrentProbeAsync()
    {
        if (accountRefresh is { } pending) await pending.ConfigureAwait(true);
    }
    private Task RefreshAccountsAsync()
    {
        if (accountRefresh is { IsCompleted: false }) return accountRefresh;
        return accountRefresh = VerifyAndPopulateAsync();
    }
    private async Task VerifyAndPopulateAsync()
    {
        using var cancellation = new CancellationTokenSource(); accountRefreshCancellation = cancellation;
        var revision = ++refreshRevision;
        string? verified = null;
        if (!store.Synthetic)
        {
            try { verified = await SavedAccounts.VerifyCurrentIdAsync(provider, Executable(), cancellation.Token).ConfigureAwait(true); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        accountRefreshCancellation = null;
        if (revision != refreshRevision || cancellation.IsCancellationRequested) return;
        verifiedCurrentId = verified; Populate();
    }
    private void Populate()
    {
        list.Children.Clear();
        try
        {
            var saved = accounts.Read(provider);
            var current = verifiedCurrentId;
            if (saved.Count == 0)
            {
                list.Children.Add(Ui.Text("No saved accounts", weight: FontWeights.SemiBold));
                list.Children.Add(Ui.Text("Save your current login, or sign in to add another account.", color: "#A6A6AA"));
            }
            foreach (var account in saved)
            {
                // One line per account, as on macOS: who it is on the left; Current or Switch, then remove, on the right.
                if (list.Children.Count > 0)
                {
                    var divider = new Border { Height = 1 }; divider.SetResourceReference(Border.BackgroundProperty, "DividerBrush"); list.Children.Add(divider);
                }
                var panel = new Grid { Margin = new Thickness(0, 14, 0, 14) };
                panel.ColumnDefinitions.Add(new ColumnDefinition()); panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                var email = Ui.Text(account.Identity.Email, 14, weight: FontWeights.Medium); email.TextWrapping = TextWrapping.Wrap; identity.Children.Add(email);
                var plan = Ui.Text((AccountPlanDisplay.Name(provider, account.Identity.Plan, account.Profile, account.Identity.Email, account.Identity.Organization)
                    ?? "Subscription") + " · " + account.Identity.Organization, 11, "#A6A6AA");
                plan.Margin = new Thickness(0, 3, 0, 0); identity.Children.Add(plan);
                panel.Children.Add(identity);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(actions, 1);
                var select = Ui.AsyncButton("Switch", async () =>
                {
                    if (MessageBox.Show(Window.GetWindow(this), "Switch the CLI to " + account.Identity.Email + "? Close its running sessions before continuing.", "Switch account", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    SetBusy(true); verifiedCurrentId = null; feedback.Text = "Verifying account…";
                    try
                    {
                        await WaitForCurrentProbeAsync();
                        if (store.Sessions.Any(x => x.Provider == provider && x.State is "busy" or "waiting")) throw new InvalidOperationException("Close the provider's active sessions first.");
                        store.InvalidateAccount(provider);
                        if (provider == "claude") await store.Claude.BeginAccountSwitchAsync();
                        var verifiedSwitch = false;
                        try
                        {
                            await accounts.SwitchAsync(account, Executable(), waitForRefresh: () => store.WaitForProviderIdleAsync(provider)).ConfigureAwait(true);
                            verifiedSwitch = true;
                        }
                        finally { if (provider == "claude") await store.Claude.FinishAccountSwitchAsync(verifiedSwitch ? account.Identity.Id : null); }
                        store.InvalidateAccount(provider); await store.RefreshProviderAsync(provider).ConfigureAwait(true);
                        await RefreshAccountsAsync(); feedback.Text = "The CLI verified the selected account.";
                        if (provider == "claude" && ClaudeDesktopAccount.Differs(ClaudeDesktopAccount.ProfileAccountId(account.Profile)))
                            feedback.Text += " Claude Desktop signs in separately and is still on another account — switch it in Claude Desktop.";
                    }
                    catch (AccountOperationBusyException error) { feedback.Text = error.UserMessage; }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        store.InvalidateAccount(provider); await store.RefreshProviderAsync(provider).ConfigureAwait(true);
                        feedback.Text = e is AccountSwitchCommittedException
                            ? "The Codex login changed, but verification did not finish. Check the current account in the official CLI before retrying."
                            : "The switch could not be verified. Refresh the account list, close running provider sessions, sign in through the official CLI, and retry.";
                    }
                    finally { SetBusy(false); }
                });
                System.Windows.Automation.AutomationProperties.SetName(select, "Switch to " + account.Identity.Email);
                select.Margin = new Thickness(0, 0, 4, 0); select.HorizontalAlignment = HorizontalAlignment.Right;
                if (current != account.Identity.Id) actions.Children.Add(select);
                else
                {
                    var marker = Ui.Text("\u2713 Current", 13, "#A6A6AA"); marker.VerticalAlignment = VerticalAlignment.Center; marker.Margin = new Thickness(0, 0, 8, 0);
                    System.Windows.Automation.AutomationProperties.SetAutomationId(marker, "accounts.current"); actions.Children.Add(marker);
                }
                var remove = Ui.Button("Remove", () => Run(() => { if (MessageBox.Show(Window.GetWindow(this), "Remove the saved login for " + account.Identity.Email + "? Its current CLI session and usage history are preserved.", "Remove saved account", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { accounts.Remove(provider, account.Identity.Id); Populate(); } }));
                System.Windows.Automation.AutomationProperties.SetName(remove, "Remove saved account " + account.Identity.Email);
                // A borderless minus-in-circle, as macOS draws it.
                remove.Content = new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M8,1 A7,7 0 1 1 7.99,1 Z M4.5,8 H11.5"),
                    StrokeThickness = 1.3, Width = 16, Height = 16, Stretch = System.Windows.Media.Stretch.None };
                ((System.Windows.Shapes.Path)remove.Content).SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryText");
                remove.Width = remove.Height = 32; remove.MinHeight = 32; remove.Padding = new Thickness(0); remove.Margin = new Thickness(0);
                remove.Background = System.Windows.Media.Brushes.Transparent; remove.BorderThickness = new Thickness(0);
                remove.ToolTip = "Remove saved account";
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
        { feedback.Text = e is AccountOperationBusyException blocked ? blocked.UserMessage : SavedAccounts.OperationInProgress ? "Wait for the current account operation to finish, then retry." : "A complete CLI subscription login is required. Finish sign-in through the official provider CLI, then retry."; }
    }
}
