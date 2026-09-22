using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;
namespace CodeRim.Windows.Views;
internal static partial class NativeSmoke
{
    private sealed class AmpFixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(action(request)); }
    }
    private static async Task AmpSourceRegression(DashboardWindow dashboard, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var originalProviders = settings.Current.EnabledProviders;
        var keys = new[] { "AMP_API_KEY", "AMP_COOKIE", "AMP_COOKIE_HEADER", "AMP_USAGE_SOURCE", "AMP_EXECUTABLE", "WINDSURF_USAGE_SOURCE", "WINDSURF_CACHE_PATH" };
        var environment = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
            var requests = new List<(string Path, string? Authorization, string? Cookie, string Method)>();
            using var connections = new ProviderConnections(vault, new NativeProviders(new AmpFixtureHandler(request =>
            {
                var cookie = request.Headers.TryGetValues("Cookie", out var values) ? values.Single() : null;
                requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), cookie, request.Method.Method));
                Require(request.RequestUri.Host == "ampcode.com", "Amp escaped its fixed provider origin");
                return new(HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Post
                    ? """{"ok":true,"result":{"displayText":"Amp Free: 75% remaining today"}}"""
                    : "<script>__sveltekit_x.data={freeTierUsage:{quota:1000,used:338.5,hourlyReplenishment:42}};</script>") };
            })));
            var jar = new BrowserCookieJar([new("session", "native-web-fixture", "ampcode.com", "/settings", true, true, 0)], ["ampcode.com"]);
            vault.Save("browser:amp", jar.Serialize()); vault.Save("provider:amp", "native-api-fixture");
            vault.Save("setting:amp:AMP_USAGE_SOURCE", "api");
            var api = await connections.FetchAsync("amp", settings.Current, CancellationToken.None);
            Require(api.State == ReadingState.Ready && requests.Count == 1 && requests[^1].Method == "POST"
                && requests[^1].Authorization == "Bearer native-api-fixture" && requests[^1].Cookie is null, "Saved browser session replaced the selected Amp API source");
            var apiScope = connections.Scope("amp");
            vault.Save("setting:amp:AMP_USAGE_SOURCE", "web");
            var web = await connections.FetchAsync("amp", settings.Current, CancellationToken.None);
            Require(web.State == ReadingState.Ready && web.Headline?.UsedPercent == 33.85 && requests[^1].Path == "/settings"
                && requests[^1].Cookie == "session=native-web-fixture" && requests[^1].Authorization is null, "Amp Web source did not use only its scoped session");
            Require(connections.CanCache("amp") && apiScope != connections.Scope("amp"), "Amp Web and API cache scope was not separated");
            vault.Delete("browser:amp"); var before = requests.Count;
            Environment.SetEnvironmentVariable("AMP_API_KEY", "native-environment-api");
            var missing = await connections.FetchAsync("amp", settings.Current, CancellationToken.None);
            Require(missing.State == ReadingState.NeedsAuth && requests.Count == before, "Missing Amp Web cookie fell back to an API key");
            vault.Save("cookie:amp", "session=native-manual");
            Require((await connections.FetchAsync("amp", settings.Current, CancellationToken.None)).State == ReadingState.Ready
                && requests[^1].Cookie == "session=native-manual", "Amp manual cookie slot was not connected");
            vault.Delete("cookie:amp"); Environment.SetEnvironmentVariable("AMP_COOKIE", "session=native-environment-web");
            var previousScope = connections.Scope("amp");
            Require((await connections.FetchAsync("amp", settings.Current, CancellationToken.None)).State == ReadingState.Ready
                && requests[^1].Cookie == "session=native-environment-web", "Amp Web environment cookie was not connected");
            Environment.SetEnvironmentVariable("AMP_COOKIE", "session=native-replaced");
            Require(previousScope != connections.Scope("amp"), "Amp cookie environment change did not invalidate scope");
            vault.Save("setting:amp:AMP_USAGE_SOURCE", "cli");
            vault.Save("setting:amp:AMP_EXECUTABLE", Path.Combine(AppContext.BaseDirectory, "missing-native-amp.exe"));
            before = requests.Count;
            Require((await connections.FetchAsync("amp", settings.Current, CancellationToken.None)).State == ReadingState.Error && requests.Count == before,
                "Failed Amp CLI switched to another source");
            var verified = await connections.VerifyBrowserAsync("amp", settings.Current, jar);
            Require(verified.State == ReadingState.Ready && requests.Count == before + 1 && requests[^1].Cookie == "session=native-web-fixture",
                "Browser verification used the selected CLI instead of the candidate browser session");
            var unrelatedPath = new BrowserCookieJar([new("session", "not-for-settings", "ampcode.com", "/other", true, true, 0)], ["ampcode.com"]);
            before = requests.Count;
            Require((await connections.VerifyBrowserAsync("amp", settings.Current, unrelatedPath)).State == ReadingState.NeedsAuth
                && requests.Count == before, "Unscoped Amp profile verification fell back to saved or environment credentials");
            vault.Save("setting:amp:AMP_USAGE_SOURCE", "");
            Environment.SetEnvironmentVariable("AMP_USAGE_SOURCE", " cli ");
            vault.Save("setting:amp:AMP_EXECUTABLE", " ");
            Environment.SetEnvironmentVariable("AMP_EXECUTABLE", Path.Combine(AppContext.BaseDirectory, "missing-first.exe"));
            Require(!connections.CanCache("amp") && ProviderConnections.EffectiveSetting(vault, "amp", "AMP_USAGE_SOURCE") == "cli",
                "Blank saved source disagrees with effective environment source");
            var firstPathScope = connections.Scope("amp");
            Environment.SetEnvironmentVariable("AMP_EXECUTABLE", Path.Combine(AppContext.BaseDirectory, "missing-second.exe"));
            Require(firstPathScope != connections.Scope("amp"), "Changing effective environment CLI path did not invalidate the blank saved path scope");
            vault.Save("setting:windsurf:WINDSURF_USAGE_SOURCE", "");
            Environment.SetEnvironmentVariable("WINDSURF_USAGE_SOURCE", " local ");
            vault.Save("setting:windsurf:WINDSURF_CACHE_PATH", " ");
            Environment.SetEnvironmentVariable("WINDSURF_CACHE_PATH", "C:\\synthetic-first.vscdb");
            var firstWindsurfScope = connections.Scope("windsurf");
            Require(!connections.CanCache("windsurf"), "Windsurf blank source ignored the effective local setting");
            Environment.SetEnvironmentVariable("WINDSURF_CACHE_PATH", "C:\\synthetic-second.vscdb");
            Require(firstWindsurfScope != connections.Scope("windsurf"), "Windsurf effective local path did not invalidate scope");
            settings.Save(settings.Current with { EnabledProviders = [..originalProviders, "amp"] });
            dashboard.Navigate("amp"); await Idle();
            Require(Equals(Descendants<ComboBox>(dashboard).Single(x => x.Items.Contains("API") && x.Items.Contains("CLI")).SelectedItem, "CLI"),
                "Amp UI does not show the effective CLI environment source");
            vault.Save("setting:amp:AMP_USAGE_SOURCE", "api"); dashboard.Navigate("amp"); await Idle();
            Require(!Descendants<Button>(dashboard).Any(x => Equals(x.Content, "Import from Firefox…")), "Amp API page displays an inactive browser importer");
            var source = Descendants<ComboBox>(dashboard).Single(x => x.Items.Contains("API") && x.Items.Contains("CLI") && x.Items.Contains("Web"));
            source.SelectedItem = "Web"; await Idle();
            Require(vault.Load("setting:amp:AMP_USAGE_SOURCE") == "web"
                && Descendants<Button>(dashboard).Any(x => Equals(x.Content, "Import from Firefox…"))
                && Descendants<TextBlock>(dashboard).Any(x => x.Text == "Amp Web session cookie"), "Amp Web source picker did not render its actual connection controls");
            Capture(dashboard, Path.Combine(directory, "windows-amp-web-connection.png"));
            File.WriteAllText(Path.Combine(directory, "windows-amp-source-evidence.json"),
                System.Text.Json.JsonSerializer.Serialize(new { checksPassed = true, requestCount = requests.Count, realAccount = false,
                    sourceIsolation = true, scopedBrowserVerification = true, sourcePicker = true, separateCredentialSlots = true }));
        }
        finally
        {
            foreach (var pair in environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            foreach (var key in new[] { "browser:amp", "provider:amp", "cookie:amp", "setting:amp:AMP_USAGE_SOURCE", "setting:amp:AMP_EXECUTABLE", "setting:windsurf:WINDSURF_USAGE_SOURCE", "setting:windsurf:WINDSURF_CACHE_PATH" }) vault.Delete(key);
            settings.Save(settings.Current with { EnabledProviders = originalProviders }); dashboard.Navigate("usage"); await Idle();
        }
    }
}
