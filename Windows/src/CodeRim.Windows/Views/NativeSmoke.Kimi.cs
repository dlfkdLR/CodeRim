using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static readonly string[] KimiEnvironmentKeys = ["KIMI_USAGE_SOURCE", "KIMI_CODE_API_KEY", "KIMI_MANUAL_COOKIE", "KIMI_AUTH_TOKEN", "kimi_auth_token",
        "KIMI_CODE_HOME", "KIMI_CODE_BASE_URL", "KIMI_CODE_OAUTH_HOST", "KIMI_OAUTH_HOST"];
    private static async Task KimiConnectionRegression(DashboardWindow owner, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var enabled = settings.Current.EnabledProviders;
        var environment = KimiEnvironmentKeys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var root = Path.Combine(directory, "kimi-fixture"); var file = Path.Combine(root, "credentials", "kimi-code.json");
        var calls = new List<(string Host, string? Credential)>(); var replaceDuringFetch = false;
        HttpResponseMessage Response(HttpRequestMessage request)
        {
            calls.Add((request.RequestUri!.Host, request.Headers.Authorization?.Parameter));
            if (replaceDuringFetch) vault.Save("cookie:kimi", "replaced-web");
            string body;
            if (request.RequestUri.Host == "api.kimi.com")
            {
                if (!request.Headers.Contains("X-Msh-Platform")) return new(HttpStatusCode.Unauthorized);
                Require(request.Headers.GetValues("X-Msh-Platform").Single() == "kimi_code_cli", "Borrowed CLI identity header is missing.");
                body = """{"usages":{"limit_5h":{"used_ratio":0.12},"limit_7d":{"used_ratio":0.33}}}""";
            }
            else
            {
                Require(request.RequestUri.Host == "www.kimi.com" && request.Method == HttpMethod.Post, "Web login left its fixed POST origin.");
                body = request.RequestUri.AbsolutePath.EndsWith("/GetUsages", StringComparison.Ordinal)
                    ? """{"usages":[{"scope":"FEATURE_CODING","detail":{"limit":100,"used":25},"limits":[{"window":{"duration":5,"timeUnit":"TIME_UNIT_HOUR"},"detail":{"limit":100,"used":65}}]}]}"""
                    : request.RequestUri.AbsolutePath.EndsWith("/GetSubscriptionStats", StringComparison.Ordinal)
                        ? """{"subscriptionBalance":{"amountUsedRatio":0.42},"ratelimitCode7d":{"enabled":false}}"""
                        : """{"subscription":{"active":true,"status":"SUBSCRIPTION_STATUS_ACTIVE","goods":{"title":"Allegro"}}}""";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
        ProviderConnections Connections() => new(vault, new NativeProviders(new AmpFixtureHandler(Response)), nativeCredentialReader: id => id == "kimi" ? NativeCredentials.Read(id) : null);
        try
        {
            foreach (var key in KimiEnvironmentKeys) Environment.SetEnvironmentVariable(key, null);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var contents = JsonSerializer.Serialize(new { access_token = "fixture-cli", refresh_token = "must-not-be-saved", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
            File.WriteAllText(file, contents); Environment.SetEnvironmentVariable("KIMI_CODE_HOME", root);
            using var verifier = Connections();
            Require((await verifier.FetchAsync("kimi", settings.Current, CancellationToken.None)).Headline?.UsedPercent == 33
                && calls[^1] == ("api.kimi.com", "fixture-cli"), "Auto failed to reuse CLI login or selected the wrong main quota.");
            Require(File.ReadAllText(file) == contents && !File.Exists(Path.Combine(root, "device_id")), "CLI login files were modified.");
            vault.Save("provider:kimi", "rejected-api");
            var beforeFallback = verifier.Scope("kimi");
            Require((await verifier.FetchAsync("kimi", settings.Current, CancellationToken.None)).Headline?.UsedPercent == 33
                && verifier.Scope("kimi") != beforeFallback, "Auto failed to move a rejected API connection to the selected CLI scope.");
            foreach (var corrupt in new[] { "{", "null", "{}" })
            {
                vault.Save("browser:kimi", corrupt);
                using var damagedBrowser = Connections();
                Require((await damagedBrowser.FetchAsync("kimi", settings.Current, CancellationToken.None)).Headline?.UsedPercent == 33,
                    "A damaged unselected browser session blocked the valid CLI fallback.");
            }
            vault.Delete("browser:kimi");
            settings.Save(settings.Current with { EnabledProviders = ["kimi"] });
            using var store = new DashboardStore(settings, vault, providerConnections: Connections());
            var window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate("kimi");
            try
            {
                await store.RefreshProviderAsync("kimi");
                await Until(() => store.Readings.TryGetValue("kimi", out var reading) && reading.Headline?.UsedPercent == 33, "Fallback did not stabilize under the CLI scope."); await Idle();
                Require(store.Readings["kimi"].Headline?.UsedPercent == 33, "CLI usage did not reach the actual store.");
                Capture(window, Path.Combine(directory, "windows-kimi-cli.png"));
                vault.Save("cookie:kimi", "saved-web");
                var picker = Descendants<ComboBox>(window).Single(combo => AutomationProperties.GetName(combo) == "Usage source");
                picker.SelectedItem = "Web"; await Idle(); await store.RefreshProviderAsync("kimi"); await Idle();
                Require(store.Readings["kimi"].Headline?.UsedPercent == 25 && store.Readings["kimi"].Plan == "Allegro"
                    && calls[^1] == ("www.kimi.com", "saved-web"), "Web source failed to publish its own account usage.");
                Require(Descendants<PasswordBox>(window).Count() == 1, "Web source exposed unrelated API credentials.");
                Capture(window, Path.Combine(directory, "windows-kimi-web.png"));
                replaceDuringFetch = true;
                Require((await verifier.FetchAsync("kimi", settings.Current, CancellationToken.None)).State == ReadingState.Unavailable,
                    "Replacing the Web connection published a late reading.");
                replaceDuringFetch = false;
                vault.Save("provider:kimi", "manual-api"); vault.Save("setting:kimi:KIMI_USAGE_SOURCE", "api");
                var jar = new BrowserCookieJar([new("kimi-auth", "browser-web", "www.kimi.com", "/apiv2", true, true, 0)], ["kimi.com"]);
                var saved = false;
                var dialog = BrowserConnections.CreateDialog(window, "kimi", vault, () => saved = true,
                    [new("Synthetic selected Firefox profile", "fixture-only")], (_, _) => Task.FromResult(jar),
                    (candidate, token) => verifier.VerifyBrowserAsync("kimi", settings.Current, candidate, token));
                dialog.Show(); await Idle();
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "Import sign-in")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => saved || Descendants<Button>(dialog).Single(button => Equals(button.Content, "Import sign-in")).IsEnabled, "Kimi browser verification did not finish.");
                Require(saved && vault.Load("provider:kimi") == "manual-api" && BrowserConnections.Load("kimi", vault)?.Count == 1
                    && calls[^1] == ("www.kimi.com", "browser-web"), "Firefox verification used another credential or erased the API key.");
                await KimiDesktopRegression(window, settings, vault, directory);
            }
            finally { window.Close(); }
            File.WriteAllText(Path.Combine(directory, "windows-kimi-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, network = "in-memory only", actualCliFileDiscovery = "PASS", cliFilesUnchanged = "PASS",
                sourceAndAccountBoundary = "PASS", corruptBrowserFallback = "PASS", nativeDpapi = "PASS", firefoxVerifyOverride = "PASS", actualConnectorStoreWpf = "PASS"
            }, JsonOptions));
        }
        finally
        {
            foreach (var key in KimiEnvironmentKeys) Environment.SetEnvironmentVariable(key, environment[key]);
            foreach (var key in new[] { "provider:kimi", "cookie:kimi", "browser:kimi", "desktop:kimi", "setting:kimi:KIMI_USAGE_SOURCE" }) vault.Delete(key);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            settings.Save(settings.Current with { EnabledProviders = enabled }); owner.Navigate("usage");
        }
    }
}
