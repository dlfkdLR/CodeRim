using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal sealed class AccountsPane : StackPanel
{
    private readonly string provider;
    private readonly SavedAccounts accounts;
    private readonly DashboardStore store;
    private readonly AppSettingsStore settings;
    private readonly StackPanel list = new();
    private readonly TextBlock feedback = Ui.Text("", 12, "#A6A6AA");
    internal AccountsPane(string provider, CredentialVault vault, DashboardStore store, AppSettingsStore settings)
    {
        this.provider = provider; this.store = store; this.settings = settings; accounts = new SavedAccounts(vault);
        Children.Add(Ui.Text((provider == "codex" ? "Codex" : "Claude") + " accounts", 24, weight: FontWeights.SemiBold));
        Children.Add(Ui.Text("Save the CLI's current sign-in, then switch between saved accounts.", color: "#A6A6AA"));
        var actions = new WrapPanel();
        actions.Children.Add(Ui.AsyncButton("Save current account", async () =>
        {
            try { await accounts.SaveCurrentAsync(provider, Executable(), waitForRefresh: () => store.WaitForProviderIdleAsync(provider)).ConfigureAwait(true); Populate(); feedback.Text = "Verified CLI account saved using Windows user encryption."; }
            catch (Exception error) when (error is not OutOfMemoryException) { feedback.Text = "The official CLI could not verify a file-backed subscription login. Finish sign-in through the CLI and retry."; }
        }));
        actions.Children.Add(Ui.Button("Sign in…", () => Run(SignIn)));
        actions.Children.Add(Ui.Button("Refresh accounts", Populate));
        Children.Add(actions); Children.Add(feedback); Children.Add(list); Populate();
        Children.Add(Ui.Text("Switching verifies the account through the official CLI. Close its running sessions before switching. Removing a saved account does not sign out or erase local usage.", 11, "#A6A6AA"));
    }
    private string Executable() => provider == "codex" ? settings.Current.CodexExecutable ?? ProviderConnections.ResolveCodex() ?? throw new FileNotFoundException()
        : ProviderConnections.ResolveExecutable("claude.exe") ?? throw new FileNotFoundException();
    private void SignIn()
    {
        if (SavedAccounts.OperationInProgress) throw new InvalidOperationException("Wait for the current account operation to finish.");
        var executable = Executable();
        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        foreach (var argument in provider == "codex" ? new[] { "login" } : new[] { "auth", "login" }) start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
        feedback.Text = "Finish sign-in in the official CLI, then choose Save current account.";
    }
    private void Populate()
    {
        list.Children.Clear();
        try
        {
            var saved = accounts.Read(provider);
            string? current = null;
            try { current = SavedAccounts.Current(provider).Identity.Id; } catch (Exception e) when (e is IOException or System.Text.Json.JsonException or FormatException) { }
            if (saved.Count == 0) list.Children.Add(Ui.Text("No saved accounts yet.", color: "#A6A6AA"));
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
                actions.Children.Add(select);
                actions.Children.Add(Ui.Button("Remove saved account", () => Run(() => { if (MessageBox.Show(Window.GetWindow(this), "Remove the saved login for " + account.Identity.Email + "? Its current CLI session and usage history are preserved.", "Remove saved account", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { accounts.Remove(provider, account.Identity.Id); Populate(); } })));
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
