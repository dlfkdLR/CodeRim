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
    private static readonly string[] MiniMaxEnvironmentKeys = ["MINIMAX_USAGE_SOURCE", "MINIMAX_REGION", "MINIMAX_COOKIE", "MINIMAX_COOKIE_HEADER", "MINIMAX_API_KEY", "MINIMAX_CODING_API_KEY"];
    private static async Task MiniMaxConnectionRegression(DashboardWindow owner, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var enabled = settings.Current.EnabledProviders; var requests = 0; var changeOnResponse = false;
        var environment = MiniMaxEnvironmentKeys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var keys = new[] { "provider:minimax", "provider:minimax:global", "provider:minimax:cn", "cookie:minimax:global", "cookie:minimax:cn",
            "browser:minimax:global", "browser:minimax:cn", "setting:minimax:LEGACY_REGION", "setting:minimax:MINIMAX_USAGE_SOURCE", "setting:minimax:MINIMAX_REGION" };
        var previous = keys.ToDictionary(key => key, vault.Load);
        HttpResponseMessage Response(HttpRequestMessage request)
        {
            requests++;
            var uri = request.RequestUri!;
            Require(uri.Host is "api.minimax.io" or "api.minimaxi.com" or "platform.minimax.io" or "platform.minimaxi.com" or "www.minimax.io" or "www.minimaxi.com",
                "MiniMax credential left its fixed regional origin.");
            var web = !uri.Host.StartsWith("api.", StringComparison.Ordinal);
            var cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : "";
            Require(web ? cookie.Length > 0 : cookie.Length == 0 && request.Headers.Authorization?.Parameter == "fixture-api",
                "MiniMax API and Web credentials were mixed.");
            var china = uri.Host.EndsWith(".minimaxi.com", StringComparison.Ordinal);
            if (web) Require(china ? cookie.Contains("=cn", StringComparison.Ordinal) : cookie.Contains("=global", StringComparison.Ordinal), "MiniMax session crossed regions.");
            if (changeOnResponse) { changeOnResponse = false; vault.Save("setting:minimax:MINIMAX_REGION", china ? "global" : "cn"); }
            var percent = web ? china ? 8 : 4 : 80;
            var body = uri.AbsolutePath == "/account/amount" ? """{"charge_records":[],"total_cnt":0}"""
                : uri.AbsolutePath.Contains("cycle_audio", StringComparison.Ordinal) ? """{"data":{"current_subscribe":{"title":"MiniMax Plus"}}}"""
                : JsonSerializer.Serialize(new { model_remains = new[] {
                    new { model_name = "video", current_interval_remaining_percent = 30 },
                    new { model_name = "general", current_interval_remaining_percent = 100 - percent } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
        ProviderConnections Connections() => new(vault, new NativeProviders(new AmpFixtureHandler(Response)), nativeCredentialReader: _ => null);
        try
        {
            foreach (var key in MiniMaxEnvironmentKeys) Environment.SetEnvironmentVariable(key, null);
            foreach (var key in keys) vault.Delete(key);
            vault.Save("provider:minimax", "fixture-api");
            using var verifier = Connections();
            var initial = await verifier.FetchAsync("minimax", settings.Current, CancellationToken.None);
            Require(initial.State == ReadingState.Ready && initial.Headline?.UsedPercent == 80
                && vault.Load("setting:minimax:LEGACY_REGION") == "global", "Legacy MiniMax API did not bind to its configured region.");
            settings.Save(settings.Current with { EnabledProviders = ["minimax"] });
            using var store = new DashboardStore(settings, vault, providerConnections: Connections());
            var window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate("minimax");
            try
            {
                await store.RefreshProviderAsync("minimax"); await Idle();
                Require(store.Readings["minimax"].Headline?.UsedPercent == 80, "MiniMax API did not reach the actual Store.");
                Capture(window, Path.Combine(directory, "windows-minimax-api.png"));
                var webInput = Descendants<PasswordBox>(window).Single(field => AutomationProperties.GetName(field) == "Save Web session");
                webInput.Password = "HERTZ-SESSION=global; minimax_group_id_v2=123";
                Descendants<Button>(window).Single(button => Equals(button.Content, "Save Web session")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle(); await store.RefreshProviderAsync("minimax"); await Idle();
                Require(vault.Load("cookie:minimax:global") == "HERTZ-SESSION=global; minimax_group_id_v2=123"
                    && store.Readings["minimax"].Headline?.UsedPercent == 4, "Saved MiniMax Web session did not become the selected Auto connection.");
                var mode = Descendants<ComboBox>(window).Single(combo => AutomationProperties.GetName(combo) == "Usage source");
                mode.SelectedItem = "Web"; await Idle(); await store.RefreshProviderAsync("minimax"); await Idle();
                Require(Descendants<PasswordBox>(window).Count() == 1, "Web mode exposed an API field.");
                Capture(window, Path.Combine(directory, "windows-minimax-web-global.png"));
                var region = Descendants<ComboBox>(window).Single(combo => AutomationProperties.GetName(combo) == "Region");
                var before = requests; region.SelectedItem = "China"; await Idle(); await store.RefreshProviderAsync("minimax"); await Idle();
                Require(store.Readings["minimax"].State == ReadingState.NeedsAuth && requests == before,
                    "A Global credential was reused after switching MiniMax to China.");
                webInput = Descendants<PasswordBox>(window).Single(field => AutomationProperties.GetName(field) == "Save Web session");
                webInput.Password = "HERTZ-SESSION=cn";
                Descendants<Button>(window).Single(button => Equals(button.Content, "Save Web session")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle(); await store.RefreshProviderAsync("minimax"); await Idle();
                Require(store.Readings["minimax"].Headline?.UsedPercent == 8, "China Web session did not reach its account.");
                Capture(window, Path.Combine(directory, "windows-minimax-web-cn.png"));
                var jar = new BrowserCookieJar([new("HERTZ-SESSION", "cn-browser", "platform.minimaxi.com", "/", true, true, 0)], ["minimaxi.com"]);
                var imported = false;
                var dialog = BrowserConnections.CreateDialog(window, "minimax", vault, () => imported = true,
                    [new("Synthetic Firefox China", "fixture-only")], (_, _) => Task.FromResult(jar),
                    (candidate, token) => verifier.VerifyBrowserAsync("minimax", settings.Current, candidate, token));
                dialog.Show(); await Idle();
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "Import sign-in")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => imported || Descendants<Button>(dialog).Single(button => Equals(button.Content, "Import sign-in")).IsEnabled, "MiniMax import did not finish.");
                Require(imported && BrowserConnections.Load("minimax", vault)?.Count == 1
                    && (await verifier.FetchAsync("minimax", settings.Current, CancellationToken.None)).State == ReadingState.Ready,
                    "Verified MiniMax browser session was not saved regionally and used for refresh.");
                var scope = verifier.Scope("minimax"); changeOnResponse = true;
                Require((await verifier.FetchAsync("minimax", settings.Current, CancellationToken.None)).State == ReadingState.Unavailable
                    && verifier.Scope("minimax") != scope, "A late MiniMax response survived a region change.");
            }
            finally { window.Close(); }
            File.WriteAllText(Path.Combine(directory, "windows-minimax-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, network = "in-memory only", apiAndWeb = "PASS", legacyRegionBinding = "PASS", regionIsolation = "PASS",
                selectedBrowserDpapi = "PASS", actualConnectorStoreWpf = "PASS", lateRegionChangeDiscard = "PASS", requests
            }, JsonOptions));
        }
        finally
        {
            foreach (var key in MiniMaxEnvironmentKeys) Environment.SetEnvironmentVariable(key, environment[key]);
            foreach (var key in keys) { if (previous[key] is { } value) vault.Save(key, value); else vault.Delete(key); }
            settings.Save(settings.Current with { EnabledProviders = enabled }); owner.Navigate("usage");
        }
    }
}
