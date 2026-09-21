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

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task AdditionalBrowserConnectionsRegression(DashboardWindow owner, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var enabled = settings.Current.EnabledProviders;
        var keys = new[] { "MIMO_API_URL", "ALIBABA_TOKEN_PLAN_REGION", "ALIBABA_TOKEN_PLAN_SEC_TOKEN", "QWEN_CLOUD_SEC_TOKEN" };
        var environment = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var evidence = new List<object>();
        try
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
            foreach (var id in new[] { "mimo", "abacus", "longcat", "alibabatokenplan", "qwencloud" })
            {
                var host = id switch {
                    "mimo" => "platform.xiaomimimo.com", "abacus" => "apps.abacus.ai", "longcat" => "longcat.chat",
                    "alibabatokenplan" => "modelstudio.console.alibabacloud.com", _ => "home.qwencloud.com"
                };
                var gateway = id == "alibabatokenplan" ? "bailian-singapore-cs.alibabacloud.com" : "cs-data.qwencloud.com";
                var plan = id is "alibabatokenplan" or "qwencloud";
                var cookies = id == "mimo"
                    ? new BrowserCookie[] { new("api-platform_serviceToken", "fixture", host, "/api", true, true, 0), new("userId", "fixture-user", host, "/api", true, true, 0) }
                    : [new("session", "fixture", host, "/", true, true, 0)];
                var jar = new BrowserCookieJar(plan
                    ? [..cookies, new("sec_token", "dashboard-sec", host, "/", true, true, 0),
                        new("session", "gateway-only", gateway, "/data", true, true, 0), new("csrf", "gateway-csrf", gateway, "/data", true, true, 0)]
                    : cookies, BrowserConnections.Domains(id));
                var requests = 0;
                HttpResponseMessage Response(HttpRequestMessage request)
                {
                    requests++;
                    Require(request.Headers.Authorization is null, "Imported browser login became an API bearer.");
                    var cookie = request.Headers.GetValues("Cookie").Single();
                    string body;
                    if (plan)
                    {
                        if (request.RequestUri!.AbsolutePath == "/data/api.json")
                        {
                            Require(request.RequestUri.Host == gateway && cookie == "session=gateway-only; csrf=gateway-csrf"
                                && request.Headers.GetValues("x-csrf-token").Single() == "gateway-csrf",
                                "Token Plan cookie/header crossed console origins.");
                            var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                            Require(form.Contains("sec_token=dashboard-sec", StringComparison.Ordinal), "Dashboard sec_token was not used by the paired gateway.");
                            body = """{"per5HourPercentage":0.33}""";
                        }
                        else body = request.RequestUri.AbsolutePath == "/tool/user/info.json" ? "{}" : "<html></html>";
                    }
                    else
                    {
                        Require(request.RequestUri!.Host == host, "Browser login left its fixed provider origin.");
                        body = id switch {
                            "mimo" => request.RequestUri.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                                ? """{"code":0,"data":{"monthUsage":{"items":[{"name":"Credits","limit":100,"used":33}]}}}"""
                                : """{"code":0,"data":{"balance":10,"currency":"USD","planCode":"Pro"}}""",
                            "abacus" => """{"success":true,"result":{"totalComputePoints":100,"computePointsLeft":67,"currentTier":"Pro"}}""",
                            _ => request.RequestUri.AbsolutePath switch {
                                "/api/v1/user-current" => """{"code":0,"data":{"name":"Fixture"}}""",
                                "/api/pay/quota/metering/token-packs/summary" => """{"code":0,"data":{"currentLot":{"status":"ACTIVE","totalToken":100,"consumedToken":33}}}""",
                                _ => """{"code":0,"data":{"totalQuota":100,"list":[{"availableToken":67}]}}"""
                            }
                        };
                    }
                    return new(HttpStatusCode.OK) { Content = new StringContent(body) };
                }
                ProviderConnections Connections() => new(vault, new NativeProviders(new AmpFixtureHandler(Response)), nativeCredentialReader: _ => null);
                vault.Save("provider:" + id, "manual-preserved");
                if (id == "alibabatokenplan") vault.Save("setting:" + id + ":ALIBABA_TOKEN_PLAN_REGION", "intl-personal");
                using var verifier = Connections();
                var saved = false;
                var dialog = BrowserConnections.CreateDialog(owner, id, vault, () => saved = true,
                    [new("Synthetic selected Firefox profile", "fixture-only")], (_, _) => Task.FromResult(jar),
                    (candidate, token) => verifier.VerifyBrowserAsync(id, settings.Current, candidate, token));
                dialog.Show(); await Idle();
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "Import sign-in"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => saved || Descendants<Button>(dialog).Single(button => Equals(button.Content, "Import sign-in")).IsEnabled,
                    "Browser verification did not complete.");
                Require(saved && BrowserConnections.Load(id, vault)?.Count == jar.Count
                    && vault.Load("provider:" + id) == "manual-preserved", "Verified import failed to persist privately or preserve manual fallback.");
                settings.Save(settings.Current with { EnabledProviders = [id] });
                using (var liveStore = new DashboardStore(settings, vault, providerConnections: Connections()))
                {
                    var liveWindow = new DashboardWindow(liveStore, settings, vault); liveWindow.Show(); liveWindow.Navigate(id);
                    try
                    {
                        await liveStore.RefreshProviderAsync(id); await Idle();
                        Require(liveStore.Readings[id].State == ReadingState.Ready
                            && Descendants<TextBlock>(liveWindow).Any(text => text.Text.Contains("33% used", StringComparison.Ordinal)),
                            "Imported browser reading did not reach the provider view.");
                        Require(Descendants<Button>(liveWindow).Any(button => Equals(button.Content, "Import from Firefox…")),
                            "Firefox connection action is missing.");
                        Capture(liveWindow, Path.Combine(directory, "windows-browser-" + id + ".png"));
                    }
                    finally { liveWindow.Close(); }
                }
                evidence.Add(new { provider = id, requests, verifiedDialog = "PASS", dpapiRoundTrip = "PASS",
                    manualFallbackPreserved = "PASS", connectorStoreWpf = "PASS" });
                vault.Delete("browser:" + id); vault.Delete("provider:" + id);
                vault.Delete("setting:" + id + ":ALIBABA_TOKEN_PLAN_REGION");
            }
            File.WriteAllText(Path.Combine(directory, "windows-browser-expansion-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, network = "in-memory only", firefoxProfileDiscovery = "not exercised by this fixture",
                providers = evidence
            }, JsonOptions));
        }
        finally
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, environment[key]);
            foreach (var id in new[] { "mimo", "abacus", "longcat", "alibabatokenplan", "qwencloud" })
            { vault.Delete("browser:" + id); vault.Delete("provider:" + id); vault.Delete("setting:" + id + ":ALIBABA_TOKEN_PLAN_REGION"); }
            settings.Save(settings.Current with { EnabledProviders = enabled }); owner.Navigate("usage");
        }
    }
}
