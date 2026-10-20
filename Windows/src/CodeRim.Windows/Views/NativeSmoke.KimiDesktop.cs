using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;
using Microsoft.Data.Sqlite;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task KimiDesktopRegression(DashboardWindow owner, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var root = Path.Combine(directory, "kimi-desktop-fixture"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Cookies");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE cookies(host_key TEXT,name TEXT,value TEXT,last_access_utc INTEGER); INSERT INTO cookies VALUES('.kimi.com','kimi-auth','desktop-A',1)";
            command.ExecuteNonQuery();
        }
        void Replace(string value)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE cookies SET value=$value,last_access_utc=last_access_utc+1";
            command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery();
        }
        var calls = new List<string?>(); var denied = false; var changeDuringRequest = false;
        HttpResponseMessage Response(HttpRequestMessage request)
        {
            calls.Add(request.Headers.Authorization?.Parameter);
            Require(request.RequestUri!.Host == "www.kimi.com" && request.Method == HttpMethod.Post, "Desktop token left its Web endpoint.");
            if (changeDuringRequest) { changeDuringRequest = false; Replace("desktop-B"); }
            if (denied) return new(HttpStatusCode.Unauthorized);
            var body = request.RequestUri.AbsolutePath.EndsWith("/GetUsages", StringComparison.Ordinal)
                ? """{"usages":[{"scope":"FEATURE_CODING","detail":{"limit":100,"used":37}}]}"""
                : request.RequestUri.AbsolutePath.EndsWith("/GetSubscriptionStats", StringComparison.Ordinal) ? "{}"
                : """{"subscription":{"active":true,"goods":{"title":"Desktop fixture"}}}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
        ProviderConnections Connections() => new(vault, new NativeProviders(new AmpFixtureHandler(Response)), nativeCredentialReader: _ => null);
        Task<KimiCredential> Read(string selected, CancellationToken token) => Task.Run(() => KimiDesktopAuthentication.Read(selected, DateTimeOffset.UtcNow, token), token);
        using var native = new NativeProviders(new AmpFixtureHandler(Response));
        Task<ProviderReading> Verify(KimiCredential credential, CancellationToken token) => native.FetchKimiAsync(credential, token);
        var saved = false;
        Window Dialog() => KimiDesktopConnection.CreateDialog(owner, vault, () => saved = true, path, Read, Verify);
        async Task Submit(Window dialog)
        {
            dialog.Show(); await Idle();
            Descendants<Button>(dialog).Single(button => Equals(button.Content, "Connect Desktop")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => saved || Descendants<Button>(dialog).Single(button => Equals(button.Content, "Connect Desktop")).IsEnabled, "Desktop verification did not finish.");
        }
        try
        {
            vault.Delete(KimiDesktopConnection.StorageKey); vault.Save("setting:kimi:KIMI_USAGE_SOURCE", "web");
            vault.Save("cookie:kimi", "manual-preserved");
            var jar = new BrowserCookieJar([new("kimi-auth", "firefox-preserved", "www.kimi.com", "/", true, true, 0)], ["kimi.com"]);
            vault.Save("browser:kimi", jar.Serialize());
            var before = File.ReadAllBytes(path);
            var dialog = Dialog(); await Submit(dialog);
            Require(saved && vault.Load(KimiDesktopConnection.StorageKey) == path && calls.All(value => value == "desktop-A"),
                "Desktop verification did not activate the selected file.");
            Require(vault.Load("cookie:kimi") == "manual-preserved" && BrowserConnections.Load("kimi", vault)?.Count == 1
                && before.SequenceEqual(File.ReadAllBytes(path)), "Desktop import changed a previous login or source database.");
            using var connections = Connections();
            var scopeA = connections.Scope("kimi");
            Require((await connections.FetchAsync("kimi", settings.Current, CancellationToken.None)).Headline?.UsedPercent == 37
                && calls[^1] == "desktop-A", "Existing Firefox took precedence over selected Desktop.");
            using var store = new DashboardStore(settings, vault, providerConnections: Connections());
            var window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate("kimi");
            try
            {
                await store.RefreshProviderAsync("kimi"); await Idle();
                Require(store.Readings["kimi"].Headline?.UsedPercent == 37
                    && !Descendants<PasswordBox>(window).Any()
                    && !Descendants<Button>(window).Any(button => Equals(button.Content, "Import from Firefox…")),
                    "Desktop usage or authoritative selected-source UI is missing.");
                Capture(window, Path.Combine(directory, "windows-kimi-desktop.png"));
                changeDuringRequest = true;
                Require((await connections.FetchAsync("kimi", settings.Current, CancellationToken.None)).State == ReadingState.Unavailable
                    && connections.Scope("kimi") != scopeA, "A changed Desktop account published the old response.");
                Replace(""); calls.Clear();
                Require((await connections.FetchAsync("kimi", settings.Current, CancellationToken.None)).State == ReadingState.NeedsAuth
                    && connections.Scope("kimi") is null && calls.Count == 0, "Desktop logout revived another saved Web account.");
                Replace("desktop-B"); denied = true; saved = false; dialog = Dialog(); await Submit(dialog);
                Require(!saved && vault.Load(KimiDesktopConnection.StorageKey) == path, "Rejected Desktop verification replaced the saved connection.");
                dialog.Close(); denied = false;
                saved = false; dialog = Dialog(); vault.Save(KimiDesktopConnection.StorageKey, path + ".changed"); await Submit(dialog);
                Require(!saved && vault.Load(KimiDesktopConnection.StorageKey) == path + ".changed", "Desktop verification overwrote a concurrent selection.");
                dialog.Close(); vault.Save(KimiDesktopConnection.StorageKey, path);
                var readCancelled = false;
                var closed = KimiDesktopConnection.CreateDialog(window, vault, () => throw new InvalidOperationException("Closed dialog saved a connection."),
                    path, async (_, token) =>
                    {
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); }
                        catch (OperationCanceledException) { readCancelled = token.IsCancellationRequested; throw; }
                    }, Verify);
                closed.Show(); await Idle();
                Descendants<Button>(closed).Single(button => Equals(button.Content, "Connect Desktop")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                closed.Close(); await Until(() => readCancelled, "Closing Desktop verification did not cancel the selected file read.");
                File.WriteAllText(path + ".invalid", "not SQLite"); vault.Save(KimiDesktopConnection.StorageKey, path + ".invalid");
                Require(connections.Scope("kimi") is null
                    && (await connections.FetchAsync("kimi", settings.Current, CancellationToken.None)).State == ReadingState.Error,
                    "Malformed Desktop database escaped the source boundary.");
                vault.Save("setting:kimi:KIMI_USAGE_SOURCE", "api");
                var apiCalls = 0;
                using (var apiConnection = new ProviderConnections(vault, new NativeProviders(new AmpFixtureHandler(request =>
                {
                    Require(request.RequestUri!.Host == "api.kimi.com", "API selection borrowed a Desktop Web token.");
                    apiCalls++; return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                })), nativeCredentialReader: _ => null))
                {
                    Require((await apiConnection.FetchAsync("kimi", settings.Current, CancellationToken.None)).State == ReadingState.NeedsAuth
                        && apiCalls == 1, "An unselected malformed Desktop database blocked the API connection.");
                }
                vault.Save("setting:kimi:KIMI_USAGE_SOURCE", "web");
                vault.Save(KimiDesktopConnection.StorageKey, path);
                window.Navigate("kimi");
                Descendants<Button>(window).Single(button => Equals(button.Content, "Use saved Web session")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle(); calls.Clear();
                Require(vault.Load(KimiDesktopConnection.StorageKey) is null
                    && (await connections.FetchAsync("kimi", settings.Current, CancellationToken.None)).Headline?.UsedPercent == 37
                    && calls[^1] == "firefox-preserved", "Explicitly restoring the saved Web connection failed.");
            }
            finally { window.Close(); }
            File.WriteAllText(Path.Combine(directory, "windows-kimi-desktop-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, network = "in-memory only", selectedPlaintextDatabase = "PASS", activeSelectionAndNativeDpapi = "PASS",
                previousSignInsPreserved = "PASS", sourceDatabaseUnchanged = "PASS", lateAccountAndLogoutInvalidation = "PASS",
                rejectedAndConcurrentSave = "PASS", cancelledRead = "PASS", malformedDatabase = "PASS", apiDoesNotBorrowDesktop = "PASS", actualStoreAndWpf = "PASS",
                installedOfficialDesktop = "UNVERIFIED", protectedCookieDecryption = "NOT_IMPLEMENTED"
            }, JsonOptions));
        }
        finally
        {
            vault.Delete(KimiDesktopConnection.StorageKey);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
