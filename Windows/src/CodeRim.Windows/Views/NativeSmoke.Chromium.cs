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
    private static async Task ChromiumConnectionRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        const string token = "synthetic-chromium-token-123456789";
        const string other = "synthetic-other-account-123456789";
        const string quota = """{"model_remains":[{"model_name":"general","current_interval_remaining_percent":96}]}""";
        var enabled = settings.Current.EnabledProviders;
        var keys = new[] { "chromium:deepseek:default", "chromium:factory:default", "chromium:minimax:global", "chromium:minimax:cn",
            "setting:deepseek:DEEPSEEK_USAGE_SOURCE", "setting:deepseek:DEEPSEEK_DETAILED_USAGE", "setting:minimax:MINIMAX_USAGE_SOURCE", "setting:minimax:MINIMAX_REGION", "provider:deepseek", "provider:factory" };
        var profile = Path.GetFullPath(Path.Combine(directory, "synthetic-chromium-profile")); Directory.CreateDirectory(profile);
        ChromiumProviderCredential Credential(string id) => id switch
        {
            "deepseek" => new(id, "default", profile, "https://platform.deepseek.com", token),
            "factory" => new(id, "default", profile, "https://app.factory.ai", """{"refresh_token":"synthetic-chromium-refresh"}"""),
            _ => new(id, "global", profile, "https://platform.minimax.io", other, new BrowserCookieJar(new[] {
                new BrowserCookie("HERTZ-SESSION", token, ".minimax.io", "/", true, false, 0),
                new BrowserCookie("minimax_group_id_v2", "123", ".minimax.io", "/", true, false, 0) }, ["minimax.io"]).Serialize(), "123")
        };
        var rotatePosts = 0; var calls = 0; var refuse = false; Action? mutate = null;
        HttpResponseMessage Response(HttpRequestMessage request)
        {
            calls++; var change = mutate; mutate = null; change?.Invoke();
            if (request.RequestUri!.Host == "api.workos.com") rotatePosts++;
            if (refuse) return new(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            if (request.RequestUri.Host.Contains("factory", StringComparison.Ordinal) || request.RequestUri.Host == "api.workos.com") return FactoryFixtureResponse(request);
            if (request.RequestUri.Host == "platform.deepseek.com")
            {
                Require(request.Headers.Authorization?.Parameter == token, "Chromium DeepSeek borrowed another credential.");
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"code":0,"data":{"biz_code":0,"biz_data":{"normal_wallets":[{"currency":"USD","balance":"42.00"}],"bonus_wallets":[]}}}""") };
            }
            if (request.RequestUri.Host == "api.deepseek.com") return new(HttpStatusCode.OK) { Content = new StringContent("""{"is_available":true,"balance_infos":[{"currency":"USD","total_balance":"7.00"}]}""") };
            var uri = request.RequestUri;
            Require(request.Method == HttpMethod.Get && uri.Scheme == "https", "Chromium MiniMax changed its request contract.");
            Require(request.Headers.GetValues("Cookie").Single().Contains("HERTZ-SESSION=" + token, StringComparison.Ordinal), "Chromium MiniMax borrowed another session.");
            string body;
            if (uri.Host == "www.minimax.io" && uri.PathAndQuery == "/v1/api/openplatform/charge/combo/cycle_audio_resource_package?biz_line=2&cycle_type=3&resource_package_type=7")
            {
                Require(request.Headers.Authorization is null && request.Headers.GetValues("x-group-id").Single() == "123", "Chromium MiniMax metadata crossed its selected group.");
                body = """{"data":{"current_subscribe":{"title":"Synthetic Coding Plan"}}}""";
            }
            else
            {
                Require(uri.Host == "platform.minimax.io" && request.Headers.Authorization?.Parameter == other, "Chromium MiniMax left its region or local-storage bearer.");
                Require(uri.PathAndQuery is "/user-center/payment/coding-plan?cycle_type=3" or "/account/amount?page=1&limit=100&aggregate=false", "Chromium MiniMax requested an unexpected endpoint.");
                body = uri.AbsolutePath == "/account/amount" ? """{"charge_records":[],"total_cnt":0}""" : quota;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
        ProviderConnections Connections() => new(vault, new NativeProviders(new AmpFixtureHandler(Response)), new HttpProviders(new AmpFixtureHandler(Response)), nativeCredentialReader: _ => null);
        try
        {
            vault.Save("setting:deepseek:DEEPSEEK_USAGE_SOURCE", "web"); vault.Save("setting:deepseek:DEEPSEEK_DETAILED_USAGE", "false");
            vault.Save("setting:minimax:MINIMAX_USAGE_SOURCE", "web"); vault.Save("setting:minimax:MINIMAX_REGION", "global");
            vault.Save("provider:deepseek", "saved-api-do-not-replace"); vault.Save("provider:factory", "saved-manual-do-not-replace");
            foreach (var id in new[] { "deepseek", "factory", "minimax" })
            {
                settings.Save(settings.Current with { EnabledProviders = [id] });
                using var connections = Connections(); using var store = new DashboardStore(settings, vault, providerConnections: Connections());
                var window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate(id); await Idle();
                var value = Credential(id); var saved = false;
                var dialog = ChromiumConnections.CreateDialog(window, id, vault, () => { saved = true; store.InvalidateAccount(id); },
                    (_, _, _, _, _, ct) => { ct.ThrowIfCancellationRequested(); return value; }, connections.VerifyChromiumAsync);
                try
                {
                    dialog.Show(); await Idle(); Descendants<TextBox>(dialog).Single(x => AutomationProperties.GetName(x) == "Chromium profile path").Text = profile;
                    Descendants<Button>(dialog).Single(x => Equals(x.Content, id == "factory" ? "Import and renew sign-in" : "Import sign-in")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var attempt = 0; attempt < 200 && !saved; attempt++) { await Task.Delay(20); await Idle(); }
                    Require(saved, "Chromium " + id + " verification did not save its explicit session.");
                    var stored = ChromiumProviderCredential.Parse(vault.Load(ProviderConnections.ChromiumStorageKey(vault, id))!);
                    Require(stored.Provider == id && stored.ProfileDirectory == profile, "Chromium import crossed provider/profile slots.");
                    if (id == "factory") Require(FactoryWorkOsProfile.Parse(stored.Secret!).RefreshToken == "rotated-native-fixture", "Chromium Factory rotation was lost.");
                    await store.RefreshProviderAsync(id); await Idle();
                    Require(store.Readings[id].State is ReadingState.Ready or ReadingState.Partial, "Chromium session did not reach the actual store.");
                    Require(Descendants<Button>(window).Any(x => Equals(x.Content, "Import from Chromium profile…")), "Chromium import is disconnected from settings.");
                    Capture(dialog, Path.Combine(directory, "windows-chromium-" + id + "-import.png"));
                    Capture(window, Path.Combine(directory, "windows-chromium-" + id + "-usage.png"));
                }
                finally { dialog.Close(); window.Close(); }
            }
            Require(rotatePosts == 1, "Chromium Factory refreshed again after committing its rotation.");
            using (var connections = Connections())
            {
                var scope = connections.Scope("deepseek"); var key = "chromium:deepseek:default";
                mutate = () => vault.Save(key, (Credential("deepseek") with { Secret = other }).Serialize());
                Require((await connections.FetchAsync("deepseek", settings.Current, CancellationToken.None)).State == ReadingState.Unavailable, "A replaced Chromium account published old usage.");
                Require(scope != connections.Scope("deepseek"), "Imported account replacement did not change its cache scope.");
                vault.Save(key, Credential("deepseek").Serialize()); refuse = true; var before = calls;
                Require((await connections.FetchAsync("deepseek", settings.Current, CancellationToken.None)).State == ReadingState.NeedsAuth && calls == before + 1, "Imported auth failure fell back to saved API.");
                refuse = false; vault.Save("setting:deepseek:DEEPSEEK_USAGE_SOURCE", "api");
                Require((await connections.FetchAsync("deepseek", settings.Current, CancellationToken.None)).State == ReadingState.Ready && scope != connections.Scope("deepseek"), "API mode failed to bypass the imported Web slot.");
                vault.Save("setting:deepseek:DEEPSEEK_USAGE_SOURCE", "web");
            }
            // The actual dialog owns a lifetime token and an opening-time version, including create-if-absent.
            foreach (var scenario in new[] { "close", "replace", "delete", "create", "profile", "source", "reject" })
            {
                const string key = "chromium:deepseek:default"; var initial = Credential("deepseek");
                if (scenario == "create") vault.Delete(key); else vault.Save(key, initial.Serialize());
                var before = vault.Load(key); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var canceled = false; var saved = false; var current = initial; using var store = new DashboardStore(settings, vault, providerConnections: Connections());
                var owner = new DashboardWindow(store, settings, vault); owner.Show();
                var dialog = ChromiumConnections.CreateDialog(owner, "deepseek", vault, () => saved = true,
                    (_, _, _, _, _, ct) => { ct.ThrowIfCancellationRequested(); return current; }, async (_, _, ct) =>
                    { entered.TrySetResult(); try { await release.Task.WaitAsync(ct); return new("deepseek", scenario == "reject" ? ReadingState.NeedsAuth : ReadingState.Ready, []); } catch (OperationCanceledException) { canceled = true; throw; } finally { done.TrySetResult(); } });
                try
                {
                    dialog.Show(); await Idle(); Descendants<TextBox>(dialog).Single(x => AutomationProperties.GetName(x) == "Chromium profile path").Text = profile;
                    Descendants<Button>(dialog).Single(x => Equals(x.Content, "Import sign-in")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (scenario == "close") dialog.Close(); else if (scenario is "replace" or "create") vault.Save(key, (initial with { Secret = other }).Serialize());
                    else if (scenario == "delete") vault.Delete(key); else if (scenario == "profile") current = initial with { Secret = other };
                    else if (scenario == "source") vault.Save("setting:deepseek:DEEPSEEK_USAGE_SOURCE", "api");
                    var expected = vault.Load(key); release.TrySetResult(); await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    for (var attempt = 0; attempt < 20; attempt++) { await Task.Delay(10); await Idle(); }
                    Require(!saved && vault.Load(key) == expected && (scenario != "close" || canceled), "A late Chromium import overwrote a changed selection: " + scenario);
                    if (scenario == "close") Require(expected == before, "Closing the dialog changed the old encrypted slot.");
                }
                finally { dialog.Close(); owner.Close(); vault.Save("setting:deepseek:DEEPSEEK_USAGE_SOURCE", "web"); }
            }
            Require(vault.Load("provider:deepseek") == "saved-api-do-not-replace" && vault.Load("provider:factory") == "saved-manual-do-not-replace", "Chromium import replaced existing credentials.");
            File.WriteAllText(Path.Combine(directory, "windows-chromium-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, network = "in-memory only", profileReader = "Core synthetic LevelDB/SQLite tests; dialog reader injected",
                providers = 3, productionConnectorStoreWpf = "PASS", dpapiRotation = "PASS", selectionRaces = 7, sourceScope = "PASS", protectedCookieDecryption = "unsupported by design"
            }, JsonOptions));
        }
        finally { foreach (var key in keys) vault.Delete(key); settings.Save(settings.Current with { EnabledProviders = enabled }); }
    }
}
