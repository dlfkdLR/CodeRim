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
    private static readonly string[] StepFunEnvironmentKeys = ["STEPFUN_AUTH_MODE", "STEPFUN_TOKEN", "STEPFUN_USERNAME", "STEPFUN_PASSWORD"];
    private static async Task StepFunConnectionRegression(DashboardWindow owner, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var enabled = settings.Current.EnabledProviders;
        var environment = StepFunEnvironmentKeys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var loginCount = 0; var refreshCount = 0; var removeOnRefresh = false;
        HttpResponseMessage Response(HttpRequestMessage request)
        {
            Require(request.RequestUri!.Host == "platform.stepfun.com", "StepFun credential left the selected fixed origin.");
            var path = request.RequestUri.AbsolutePath;
            HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
            if (path == "/") { var response = Ok(""); response.Headers.Add("Set-Cookie", "INGRESSCOOKIE=fixture; Path=/; Secure"); return response; }
            if (path.EndsWith("RegisterDevice", StringComparison.Ordinal)) return Ok("""{"accessToken":{"raw":"anonymous"}}""");
            if (path.EndsWith("SignInByPassword", StringComparison.Ordinal)) { loginCount++; return Ok("""{"accessToken":{"raw":"login-token"},"refreshToken":{"raw":"refresh-token"}}"""); }
            if (path.EndsWith("RefreshToken", StringComparison.Ordinal))
            {
                refreshCount++; if (removeOnRefresh) vault.Delete("provider:stepfun");
                return Ok("""{"accessToken":{"raw":"rotated-token"}}""");
            }
            if (request.Headers.GetValues("Cookie").Single().Contains("Oasis-Token=expired", StringComparison.Ordinal)) return new(HttpStatusCode.Unauthorized);
            return Ok(path.EndsWith("GetStepPlanStatus", StringComparison.Ordinal) ? """{"status":1,"subscription":{"name":"Step Plan"}}"""
                : """{"status":1,"five_hour_usage_left_rate":0.67,"weekly_usage_left_rate":0.5,"five_hour_usage_reset_time":1801000000,"weekly_usage_reset_time":1801000000}""");
        }
        ProviderConnections Connections() => new(vault, new NativeProviders(new AmpFixtureHandler(Response)), nativeCredentialReader: _ => null);
        try
        {
            foreach (var key in StepFunEnvironmentKeys) Environment.SetEnvironmentVariable(key, null);
            vault.Save("setting:stepfun:STEPFUN_USERNAME", "fixture-user"); vault.Save("setting:stepfun:STEPFUN_PASSWORD", "fixture-password");
            using var verifier = Connections(); var scope = verifier.Scope("stepfun");
            Require((await verifier.FetchAsync("stepfun", settings.Current, CancellationToken.None)).State == ReadingState.Ready
                && verifier.Scope("stepfun") == scope && loginCount == 1, "Password login changed its account scope or did not fetch quota.");
            await verifier.FetchAsync("stepfun", settings.Current, CancellationToken.None);
            Require(loginCount == 1, "A valid cached StepFun sign-in logged in again.");
            settings.Save(settings.Current with { EnabledProviders = ["stepfun"] });
            using var store = new DashboardStore(settings, vault, providerConnections: Connections());
            var window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate("stepfun");
            try
            {
                await store.RefreshProviderAsync("stepfun"); await Idle();
                Require(store.Readings["stepfun"].Headline?.UsedPercent is { } usage && Math.Abs(usage - 33) < 0.00001 && store.Readings["stepfun"].Plan == "Step Plan",
                    "Password connection did not reach the actual store.");
                Require(Descendants<PasswordBox>(window).Count() == 2, "StepFun password must remain a secure field.");
                Capture(window, Path.Combine(directory, "windows-stepfun-auto.png"));
                var password = Descendants<PasswordBox>(window).Single(field => AutomationProperties.GetName(field) == "Save password");
                password.Password = " fixture-password ";
                Descendants<Button>(window).Single(button => Equals(button.Content, "Save password")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(vault.Load("setting:stepfun:STEPFUN_PASSWORD") == " fixture-password ", "Password UI changed significant surrounding spaces.");
                await Idle();
                vault.Save("provider:stepfun", "expired");
                var picker = Descendants<ComboBox>(window).Single(combo => AutomationProperties.GetName(combo) == "Authentication");
                picker.SelectedItem = "Manual"; await Idle(); await store.RefreshProviderAsync("stepfun"); await Idle();
                var recovered = StepFunAuthentication.Saved(vault.Load("provider:stepfun"));
                Require(recovered?.Token == "rotated-token" && recovered.Owner == StepFunAuthentication.Manual("expired")!.Owner
                    && store.Readings["stepfun"].State == ReadingState.Ready, "Manual refresh did not preserve its owner and publish quota.");
                Require(Descendants<PasswordBox>(window).Count() == 1, "Manual mode exposed an unrelated password.");
                Capture(window, Path.Combine(directory, "windows-stepfun-manual.png"));
                vault.Save("provider:stepfun", "expired"); removeOnRefresh = true;
                Require((await verifier.FetchAsync("stepfun", settings.Current, CancellationToken.None)).State == ReadingState.Unavailable
                    && vault.Load("provider:stepfun") is null, "Token refresh resurrected a removed StepFun account.");
                removeOnRefresh = false;
                var jar = new BrowserCookieJar([new("Oasis-Token", "browser-token", "platform.stepfun.com", "/", true, true, 0)], ["stepfun.com"]);
                Require((await verifier.VerifyBrowserAsync("stepfun", settings.Current, jar)).State == ReadingState.Ready,
                    "Browser verification used Manual mode instead of its selected jar.");
                vault.Save("browser:stepfun", jar.Serialize());
                Require(BrowserConnections.Load("stepfun", vault)?.Count == 1, "StepFun Firefox session was not stored in DPAPI.");
            }
            finally { window.Close(); }
            File.WriteAllText(Path.Combine(directory, "windows-stepfun-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, network = "in-memory only", passwordLogin = "PASS", memorySession = "PASS", manualRefreshCas = "PASS",
                removedAccountNotResurrected = "PASS", browserOverride = "PASS", nativeDpapi = "PASS", actualConnectorStoreWpf = "PASS", loginCount, refreshCount
            }, JsonOptions));
        }
        finally
        {
            foreach (var key in StepFunEnvironmentKeys) Environment.SetEnvironmentVariable(key, environment[key]);
            foreach (var key in new[] { "provider:stepfun", "browser:stepfun", "setting:stepfun:STEPFUN_AUTH_MODE", "setting:stepfun:STEPFUN_USERNAME", "setting:stepfun:STEPFUN_PASSWORD" }) vault.Delete(key);
            settings.Save(settings.Current with { EnabledProviders = enabled }); owner.Navigate("usage");
        }
    }
}
