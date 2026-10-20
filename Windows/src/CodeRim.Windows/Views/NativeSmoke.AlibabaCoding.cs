using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
    private static async Task AlibabaCodingSourceRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        const string quota = """{"codingPlanInstanceInfos":[{"status":"ACTIVE","planName":"Fixture Pro","codingPlanQuotaInfo":{"per5HourUsedQuota":25,"per5HourTotalQuota":100,"perWeekUsedQuota":100,"perWeekTotalQuota":1000,"perBillMonthUsedQuota":200,"perBillMonthTotalQuota":2000}}]}""";
        const string intlCookie = "login_aliyunid_ticket=intl-ticket; login_aliyunid_pk=intl-account; sec_token=intl-sec";
        const string cnCookie = "login_aliyunid_ticket=cn-ticket; login_aliyunid_pk=cn-account; sec_token=cn-sec";
        var keys = new[] { "ALIBABA_CODING_PLAN_SOURCE", "ALIBABA_CODING_PLAN_REGION", "ALIBABA_CODING_PLAN_API_KEY", "ALIBABA_CODING_PLAN_COOKIE", "DASHSCOPE_API_KEY", "ALIBABA_API_KEY" };
        var environment = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var enabled = settings.Current.EnabledProviders;
        var calls = new List<(string Host, string? Cookie, string? Api)>(); var mutate = 0; var refuse = false;
        HttpResponseMessage Response(HttpRequestMessage request)
        {
            var cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : null;
            var api = request.Headers.Authorization?.Parameter;
            calls.Add((request.RequestUri!.Host, cookie, api));
            if (mutate != 0) { var change = mutate; mutate = 0; vault.Save(change == 1 ? "cookie:alibaba:intl" : "setting:alibaba:ALIBABA_CODING_PLAN_REGION", change == 1 ? intlCookie.Replace("intl-ticket", "replacement", StringComparison.Ordinal) : "cn"); }
            if (cookie is not null)
            {
                Require(api is null && !request.Headers.Contains("x-api-key"), "Coding Plan Web leaked an API key.");
                Require(cookie == intlCookie || cookie == cnCookie || cookie.Contains("browser-ticket", StringComparison.Ordinal), "Coding Plan sent a different session.");
                if (refuse) return new(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
                if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("<script>const config={SEC_TOKEN:'fixture-sec'}</script>") };
                Require(request.RequestUri.Host is "bailian-singapore-cs.alibabacloud.com" or "bailian-cs.console.aliyun.com", "Coding Plan Web used an unpinned gateway.");
                Require(request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded", "Coding Plan Web did not use the console form contract.");
            }
            else Require(api == "saved-coding-api" && request.RequestUri.Host == "modelstudio.console.alibabacloud.com", "Coding Plan API changed source or region.");
            return new(HttpStatusCode.OK) { Content = new StringContent(quota) };
        }
        ProviderConnections Connections() => new(vault, native: new NativeProviders(new AmpFixtureHandler(Response)), nativeCredentialReader: _ => null);
        try
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
            vault.Save("provider:alibaba", "saved-coding-api");
            vault.Save("cookie:alibaba:intl", intlCookie); vault.Save("cookie:alibaba:cn", cnCookie);
            vault.Save("setting:alibaba:ALIBABA_CODING_PLAN_REGION", "intl");
            using var connection = Connections();
            Require((await connection.FetchAsync("alibaba", settings.Current, CancellationToken.None)).State == ReadingState.Ready
                && calls[^1].Api == "saved-coding-api", "The Coding Plan default did not preserve API mode.");
            var apiScope = connection.Scope("alibaba");
            vault.Save("setting:alibaba:ALIBABA_CODING_PLAN_SOURCE", "web");
            var webScope = connection.Scope("alibaba");
            Require(webScope is not null && webScope != apiScope, "API and Web share an account scope.");
            var reading = await connection.FetchAsync("alibaba", settings.Current, CancellationToken.None);
            Require(reading.State == ReadingState.Ready && reading.Windows.Count == 3 && calls[^1].Cookie == intlCookie, "The explicit Web credential did not reach the console.");
            refuse = true; var before = calls.Count;
            Require((await connection.FetchAsync("alibaba", settings.Current, CancellationToken.None)).State == ReadingState.NeedsAuth
                && calls.Count == before + 1 && calls[^1].Api is null, "Explicit Web authentication failure fell back to API or another region.");
            refuse = false; mutate = 1;
            Require((await connection.FetchAsync("alibaba", settings.Current, CancellationToken.None)).State == ReadingState.Unavailable,
                "A replaced Coding Plan session published its old response.");
            vault.Save("cookie:alibaba:intl", intlCookie); mutate = 2;
            Require((await connection.FetchAsync("alibaba", settings.Current, CancellationToken.None)).State == ReadingState.Unavailable,
                "A changed Coding Plan region published its old response.");
            Require((await connection.FetchAsync("alibaba", settings.Current, CancellationToken.None)).State == ReadingState.Ready
                && calls[^1].Cookie == cnCookie && calls[^1].Host == "bailian-cs.console.aliyun.com"
                && connection.Scope("alibaba") != webScope, "China did not select its own encrypted Web slot.");
            vault.Save("setting:alibaba:ALIBABA_CODING_PLAN_REGION", "intl");
            var jar = new BrowserCookieJar(new[] {
                new BrowserCookie("login_aliyunid_ticket", "browser-ticket", "bailian-singapore-cs.alibabacloud.com", "/data", true, true, 0),
                new BrowserCookie("login_aliyunid_pk", "browser-account", "bailian-singapore-cs.alibabacloud.com", "/data", true, true, 0),
                new BrowserCookie("sec_token", "browser-sec", "bailian-singapore-cs.alibabacloud.com", "/data", true, true, 0)
            }, AlibabaCodingPlanAuthentication.Domains("intl"));
            vault.Save("setting:alibaba:ALIBABA_CODING_PLAN_SOURCE", "api"); before = calls.Count;
            Require((await connection.VerifyBrowserAsync("alibaba", settings.Current, jar)).State == ReadingState.Ready
                && calls.Count == before + 1 && calls[^1].Cookie?.Contains("browser-ticket", StringComparison.Ordinal) == true,
                "Browser verification used an API credential or sent a scoped cookie to the dashboard.");
            vault.Save("setting:alibaba:ALIBABA_CODING_PLAN_SOURCE", "web");
            vault.Save("browser:alibaba:intl", jar.Serialize());
            Require((await connection.FetchAsync("alibaba", settings.Current, CancellationToken.None)).State == ReadingState.Ready
                && calls[^1].Cookie?.Contains("browser-ticket", StringComparison.Ordinal) == true, "Selected browser session did not take precedence over manual cookies.");
            vault.Delete("browser:alibaba:intl"); settings.Save(settings.Current with { EnabledProviders = ["alibaba"] });
            using var store = new DashboardStore(settings, vault, providerConnections: Connections());
            var window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate("alibaba");
            try
            {
                await store.RefreshProviderAsync("alibaba"); await Idle();
                Require(store.Readings["alibaba"].State == ReadingState.Ready && store.Readings["alibaba"].Windows[2].DurationMinutes == 43200
                    && Descendants<TextBlock>(window).Any(text => text.Text.Contains("25 / 100 requests", StringComparison.Ordinal)), "Actual Coding Plan counts did not reach WPF.");
                Require(Descendants<PasswordBox>(window).Count() == 1, "Web shows unrelated API credential controls.");
                Capture(window, Path.Combine(directory, "windows-alibaba-coding-web.png"));
                var regionPicker = Descendants<ComboBox>(window).Single(combo => AutomationProperties.GetName(combo) == "Region");
                regionPicker.SelectedItem = "China"; await Idle(); await store.RefreshProviderAsync("alibaba"); await Idle();
                Require(vault.Load("setting:alibaba:ALIBABA_CODING_PLAN_REGION") == "cn" && calls[^1].Cookie == cnCookie,
                    "The region picker did not select its own Web slot.");
                Capture(window, Path.Combine(directory, "windows-alibaba-coding-china.png"));
                Descendants<ComboBox>(window).Single(combo => AutomationProperties.GetName(combo) == "Region").SelectedItem = "International";
                await Idle(); await store.RefreshProviderAsync("alibaba"); await Idle();
                var picker = Descendants<ComboBox>(window).Single(combo => AutomationProperties.GetName(combo) == "Usage source");
                picker.SelectedItem = "API"; await Idle(); await store.RefreshProviderAsync("alibaba"); await Idle();
                Require(vault.Load("setting:alibaba:ALIBABA_CODING_PLAN_SOURCE") == "api" && calls[^1].Api == "saved-coding-api"
                    && vault.Load("cookie:alibaba:intl") == intlCookie && vault.Load("cookie:alibaba:cn") == cnCookie,
                    "Source switching lost saved Web sessions or did not reach the API connector.");
                Capture(window, Path.Combine(directory, "windows-alibaba-coding-api.png"));
            }
            finally { window.Close(); }
            File.WriteAllText(Path.Combine(directory, "windows-alibaba-coding-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, network = "in-memory only", defaultApiPreserved = "PASS", webNoFallback = "PASS", encryptedRegionalSlots = "PASS",
                browserActualPathVerification = "PASS", sessionReplacement = "PASS", regionReplacement = "PASS", actualConnectorStoreWpf = "PASS", calls = calls.Count
            }, JsonOptions));
        }
        finally
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, environment[key]);
            foreach (var key in new[] { "provider:alibaba", "cookie:alibaba:intl", "cookie:alibaba:cn", "browser:alibaba:intl", "browser:alibaba:cn", "setting:alibaba:ALIBABA_CODING_PLAN_SOURCE", "setting:alibaba:ALIBABA_CODING_PLAN_REGION" }) vault.Delete(key);
            settings.Save(settings.Current with { EnabledProviders = enabled });
        }
    }
}
