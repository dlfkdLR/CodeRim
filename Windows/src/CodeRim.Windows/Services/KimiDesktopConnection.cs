using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Views;

namespace CodeRim.Windows.Services;

internal static class KimiDesktopConnection
{
    internal const string StorageKey = "desktop:kimi";
    private static readonly string[] SelectionKeys = ["provider:kimi", "cookie:kimi", "browser:kimi", "setting:kimi:KIMI_USAGE_SOURCE"];
    internal static void Import(Window owner, CredentialVault vault, Action saved)
    {
        var picker = new Microsoft.Win32.OpenFileDialog {
            Title = "Choose Kimi Desktop's Cookies database", Filter = "Cookies database|Cookies|All files|*.*", CheckFileExists = true
        };
        if (picker.ShowDialog(owner) != true) return;
        try
        {
            using var native = new NativeProviders();
            CreateDialog(owner, vault, saved, picker.FileName,
                (path, token) => Task.Run(() => KimiDesktopAuthentication.Read(path, DateTimeOffset.UtcNow, token), token),
                (credential, token) => native.FetchKimiAsync(credential, token)).ShowDialog();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        { MessageBox.Show(owner, "The Desktop connection could not be opened. Your current connection was kept.", "Kimi Desktop"); }
    }
    internal static Window CreateDialog(Window owner, CredentialVault vault, Action saved, string path,
        Func<string, CancellationToken, Task<KimiCredential>> read,
        Func<KimiCredential, CancellationToken, Task<ProviderReading>> verify)
    {
        var expected = vault.Version(StorageKey);
        var selection = SelectionKeys.ToDictionary(key => key, vault.Version);
        var window = new Window { Owner = owner, Title = "Connect Kimi Desktop", Width = 460, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
        var lifetime = new CancellationTokenSource(); var closed = false; var busy = false;
        window.Closed += (_, _) => { closed = true; lifetime.Cancel(); if (!busy) lifetime.Dispose(); };
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Ui.Text("Use this Kimi Desktop sign-in", 18));
        body.Children.Add(Ui.Text("CodeRim reads the selected app's current plaintext session. It does not modify the app's login. Protected sessions can be connected through the existing Web sign-in options.", 12, "#A6A6AA"));
        var file = Ui.Text(Path.GetFileName(path), 12);
        System.Windows.Automation.AutomationProperties.SetName(file, "Selected Desktop database"); body.Children.Add(file);
        var status = Ui.Text("", 12, "#A6A6AA");
        System.Windows.Automation.AutomationProperties.SetAutomationId(status, "kimi-desktop.status");
        body.Children.Add(Ui.AsyncButton("Connect Desktop", async () =>
        {
            if (closed) return; busy = true;
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                budget.CancelAfter(TimeSpan.FromSeconds(30));
                var credential = await read(path, budget.Token);
                if (closed) return;
                if (credential is not { Source: "web", Token: not null, ApiBaseUrl: null, BrowserState: null }
                    || credential.FilePath != Path.GetFullPath(path))
                { status.Text = "No supported current Desktop sign-in was found. Your existing connection was kept."; return; }
                status.Text = "Checking the Kimi connection…";
                var reading = await verify(credential, budget.Token);
                if (closed) return;
                if (reading.State is not (ReadingState.Ready or ReadingState.Partial))
                { status.Text = "Kimi did not confirm this sign-in. Your existing connection was kept."; return; }
                if (credential != await read(path, budget.Token))
                { status.Text = "The Desktop sign-in changed. Retry the connection."; return; }
                if (closed) return;
                if (selection.Any(pair => vault.Version(pair.Key) != pair.Value)
                    || !vault.SaveIfUnchanged(StorageKey, expected, Path.GetFullPath(path)))
                { status.Text = "The selected Kimi connection changed. Reopen this connection."; return; }
                saved(); window.Close();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException
                or Microsoft.Data.Sqlite.SqliteException or CryptographicException or ArgumentException or System.Text.Json.JsonException)
            { if (!closed) status.Text = "The Desktop session could not be read or verified. Your existing connection was kept."; }
            finally { busy = false; if (closed) lifetime.Dispose(); }
        }));
        body.Children.Add(status); body.Children.Add(Ui.Button("Cancel", window.Close)); window.Content = body;
        return window;
    }
}
