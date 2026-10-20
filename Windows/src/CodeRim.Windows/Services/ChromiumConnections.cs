using System.IO;
using System.Windows;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Views;

namespace CodeRim.Windows.Services;

internal static class ChromiumConnections
{
    internal static void Import(Window owner, string id, CredentialVault vault, Action saved)
    {
        try
        {
            using var connection = new ProviderConnections(vault);
            CreateDialog(owner, id, vault, saved, ChromiumProviderAuthentication.Read, connection.VerifyChromiumAsync).ShowDialog();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
        { MessageBox.Show(owner, "The selected browser connection could not be opened safely. Existing sign-ins were kept.", "CodeRim"); }
    }
    internal static Window CreateDialog(Window owner, string id, CredentialVault vault, Action saved,
        Func<string, string, string, string, DateTimeOffset, CancellationToken, ChromiumProviderCredential> read,
        Func<ChromiumProviderCredential, Func<ChromiumProviderCredential, bool>, CancellationToken, Task<ProviderReading>> verify)
    {
        var region = ProviderConnections.ChromiumRegion(vault, id); var key = ProviderConnections.ChromiumStorageKey(vault, id);
        var settings = ProviderConnections.ChromiumSelection(vault, id); var expected = vault.Version(key);
        var window = new Window { Owner = owner, Title = "Import " + id + " browser sign-in", Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
        var body = new StackPanel { Margin = new Thickness(24) }; window.Content = body;
        body.Children.Add(Ui.Text("Choose one Chromium profile folder (for example, Default or Profile 1). Close that browser before importing.", 13));
        body.Children.Add(Ui.Text("Only this provider's current sign-in is checked and encrypted for your Windows user. This imports a snapshot; it does not monitor browser account changes.", 12, "#A6A6AA"));
        if (id == "minimax") body.Children.Add(Ui.Text("MiniMax also requires matching plaintext cookies in this same profile. Protected cookies and uncheckpointed databases are unsupported. Use Firefox or a manual Web session if needed.", 12, "#F2C66D"));
        if (id == "factory") body.Children.Add(Ui.Text("Factory may renew the selected session. A renewed sign-in is saved before checking quota so it is kept if the usage service is unavailable.", 12, "#A6A6AA"));
        var path = new TextBox { MaxLength = 4096, MinHeight = 28, Margin = new Thickness(0, 8, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(path, "Chromium profile path"); body.Children.Add(path);
        body.Children.Add(Ui.Button("Choose profile folder…", () => { var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose Chromium profile folder" }; if (picker.ShowDialog(window) == true) path.Text = picker.FolderName; }));
        var origin = new ComboBox { ItemsSource = ChromiumProviderAuthentication.Origins(id, region), SelectedIndex = 0, MinHeight = 28, Margin = new Thickness(0, 8, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(origin, "Provider origin"); body.Children.Add(origin);
        var status = Ui.Text("", 12, "#A6A6AA"); System.Windows.Automation.AutomationProperties.SetAutomationId(status, "chromium-import.status");
        var lifetime = new CancellationTokenSource(); var closed = false; var busy = false; var completed = false;
        window.Closed += (_, _) => { closed = true; lifetime.Cancel(); if (!busy) lifetime.Dispose(); };
        body.Children.Add(Ui.AsyncButton(id == "factory" ? "Import and renew sign-in" : "Import sign-in", async () =>
        {
            if (closed || busy || completed || origin.SelectedItem is not string selectedOrigin) return;
            var selectedPath = path.Text.Trim(); var committed = false; busy = true; path.IsEnabled = false; origin.IsEnabled = false;
            bool Current() => !closed && !lifetime.IsCancellationRequested && path.Text.Trim() == selectedPath && (string?)origin.SelectedItem == selectedOrigin
                && ProviderConnections.ChromiumSelection(vault, id) == settings && ProviderConnections.ChromiumEnabled(vault, id);
            bool Commit(ChromiumProviderCredential value)
            {
                // Invoked on the owning dispatcher: Closed and this final check+CAS cannot interleave.
                if (!Current() || !vault.SaveIfUnchanged(key, expected, value.Serialize())) return false;
                committed = true; completed = true; return true;
            }
            try
            {
                if (!Current()) { status.Text = "The selected source or region changed. Reopen this connection."; return; }
                status.Text = "Reading the selected profile…";
                var credential = await Task.Run(() => read(id, region, selectedPath, selectedOrigin, DateTimeOffset.UtcNow, lifetime.Token), lifetime.Token);
                if (!Current()) return;
                status.Text = "Checking this sign-in…";
                var reading = await verify(credential, updated =>
                {
                    // Refresh rotation can run on a worker thread. Recheck the original browser
                    // snapshot before dispatching the final settings/version comparison and CAS.
                    var latest = read(id, region, selectedPath, selectedOrigin, DateTimeOffset.UtcNow, lifetime.Token);
                    return latest == credential && window.Dispatcher.Invoke(() => Commit(updated));
                }, lifetime.Token);
                if (closed) return;
                if (!committed && reading.State is ReadingState.Ready or ReadingState.Partial)
                {
                    // Refuse a profile account change during network verification; do not combine snapshots.
                    var latest = await Task.Run(() => read(id, region, selectedPath, selectedOrigin, DateTimeOffset.UtcNow, lifetime.Token), lifetime.Token);
                    if (latest != credential || !Commit(credential)) { status.Text = "The profile or saved connection changed. Existing sign-ins were kept."; return; }
                }
                if (!committed) { status.Text = "This sign-in was not confirmed. Existing connections were kept."; return; }
                saved(); status.Text = reading.State is ReadingState.Ready or ReadingState.Partial ? "Sign-in saved." : "The renewed sign-in was saved. Usage is temporarily unavailable.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OperationCanceledException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
            { if (!closed) { if (committed) { saved(); status.Text = "The renewed sign-in was saved. Usage is temporarily unavailable."; } else status.Text = error is ChromiumStorageException ? error.Message : "Could not import the selected profile safely. Existing sign-ins were kept."; } }
            finally { busy = false; if (closed) lifetime.Dispose(); else { path.IsEnabled = true; origin.IsEnabled = true; } }
        }));
        body.Children.Add(status); body.Children.Add(Ui.Button("Close", window.Close)); return window;
    }
}
