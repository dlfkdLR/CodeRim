using System.IO;
using System.Windows;
using System.Windows.Controls;
using CodeRim.Core.Providers;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Views;

namespace CodeRim.Windows.Services;

internal static class BrowserConnections
{
    internal static string[] Domains(string id) => id == "perplexity" ? ["perplexity.ai"] : ScriptProviders.Catalog.TryGetValue(id, out var script)
        ? script.CookieDomains : id switch {
            "cursor" => ["cursor.com"],
            "groq" => ["groq.com"],
            "factory" => ["factory.ai"],
            "notion" => ["notion.so", "notion.com"],
            "mistral" => ["mistral.ai"],
            "augment" => ["augmentcode.com"],
            "opencode-zen" => ["opencode.ai"],
            _ => []
        };
    internal static BrowserCookieJar? Load(string id, CredentialVault vault) =>
        vault.Load("browser:" + id) is { } json ? BrowserCookieJar.Parse(json, Domains(id)) : null;

    private static BrowserProfile[] Profiles()
    {
        var roots = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox") };
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
            roots.AddRange(Directory.EnumerateDirectories(packages, "Mozilla.Firefox_*")
                .Take(8).Select(p => Path.Combine(p, "LocalCache", "Roaming", "Mozilla", "Firefox")));
        return roots.SelectMany(FirefoxCookieImport.Profiles).DistinctBy(p => p.Directory).Take(32).ToArray();
    }

    internal static void Import(Window owner, string id, CredentialVault vault, AppSettings settings, Action saved)
    {
        try
        {
            var profiles = Profiles();
            if (profiles.Length == 0)
            {
                MessageBox.Show(owner, "No supported Firefox profile was found. Sign in to this provider in Firefox, then try again. Chrome and Edge protected profiles are not decrypted by CodeRim.", "Browser connection");
                return;
            }
            CreateDialog(owner, id, vault, saved, profiles,
                (profile, token) => Task.Run(() => FirefoxCookieImport.Read(profile, Domains(id), DateTimeOffset.UtcNow, token), token),
                async (jar, token) => { using var connections = new ProviderConnections(vault); return await connections.VerifyBrowserAsync(id, settings, jar, token); }).ShowDialog();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(owner, "Firefox profiles could not be read. You can still enter a provider credential manually.", "Browser connection"); }
    }
    internal static Window CreateDialog(Window owner, string id, CredentialVault vault, Action saved,
        BrowserProfile[] profiles, Func<BrowserProfile, CancellationToken, Task<BrowserCookieJar>> read, Func<BrowserCookieJar, CancellationToken, Task<ProviderReading>> verify)
    {
            var window = new Window { Owner = owner, Title = "Connect " + id, Width = 460, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            var closed = false; var busy = false; var lifetime = new CancellationTokenSource();
            window.Closed += (_, _) => { closed = true; lifetime.Cancel(); if (!busy) lifetime.Dispose(); };
            var body = new StackPanel { Margin = new Thickness(24) };
            body.Children.Add(Ui.Text("Choose your signed-in Firefox profile."));
            body.Children.Add(Ui.Text("Only cookies for " + string.Join(", ", Domains(id)) + " are imported. Container and partitioned sessions remain separate. Imported cookies are encrypted for your Windows user.", 12, "#A6A6AA"));
            var select = new System.Windows.Controls.ComboBox { ItemsSource = profiles, DisplayMemberPath = "Name", SelectedIndex = 0, MinHeight = 28 };
            System.Windows.Automation.AutomationProperties.SetName(select, "Firefox profile");
            body.Children.Add(select);
            var status = Ui.Text("", 12, "#A6A6AA");
            System.Windows.Automation.AutomationProperties.SetAutomationId(status, "browser-import.status");
            body.Children.Add(Ui.AsyncButton("Import sign-in", async () =>
            {
                if (closed || select.SelectedItem is not BrowserProfile profile) return;
                busy = true;
                try
                {
                    var jar = await read(profile, lifetime.Token);
                    if (closed) return;
                    if (jar.Count == 0) { status.Text = "No current sign-in was found for this provider in this profile."; return; }
                    status.Text = "Checking the provider connection…";
                    var reading = await verify(jar, lifetime.Token);
                    if (closed) return;
                    if (reading.State is not (ReadingState.Ready or ReadingState.Partial))
                    {
                        status.Text = "The provider did not confirm this sign-in. Existing connections were kept. Check the browser account and retry.";
                        return;
                    }
                    vault.Save("browser:" + id, jar.Serialize());
                    // Imported credentials take precedence. Preserve manual credentials so removal can restore them.
                    saved(); window.Close();
                }
                catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException
                    or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
                { if (!closed) status.Text = "Could not import this profile safely. Check its sign-in and retry; no browser data was changed."; }
                finally { busy = false; if (closed) lifetime.Dispose(); }
            }));
            body.Children.Add(status); body.Children.Add(Ui.Button("Cancel", window.Close)); window.Content = body; return window;
    }
}
