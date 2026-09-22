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
    private static async Task DeepSeekSourceRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var enabled = settings.Current.EnabledProviders;
        var keys = new[] { "DEEPSEEK_DETAILED_USAGE", "DEEPSEEK_USAGE_SOURCE", "DEEPSEEK_API_KEY", "DEEPSEEK_KEY", "DEEPSEEK_PLATFORM_TOKEN", "DEEPSEEK_USER_TOKEN" };
        var environment = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var calls = new List<(string Host, string? Credential)>(); var replaceDuringFetch = false;
        var optionalFailure = false; var changeDuringDetails = false; var detailCalls = 0;
        HttpResponseMessage Response(HttpRequestMessage request)
        {
            calls.Add((request.RequestUri!.Host, request.Headers.Authorization!.Parameter));
            if (replaceDuringFetch) vault.Save("provider:deepseek:web", "replacement-web");
            if (request.RequestUri.AbsolutePath.Contains("/usage/", StringComparison.Ordinal))
            {
                detailCalls++;
                Require(request.RequestUri.Host == "platform.deepseek.com" && request.Headers.Authorization.Parameter == "saved-web",
                    "Detailed usage mixed a different account or source.");
                if (changeDuringDetails) { changeDuringDetails = false; vault.Save("setting:deepseek:DEEPSEEK_DETAILED_USAGE", "false"); }
                if (optionalFailure) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
                var time = DateTimeOffset.Now.ToUnixTimeSeconds();
                var details = request.RequestUri.AbsolutePath.EndsWith("/amount", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new { code = 0, data = new { biz_code = 0, biz_data = new {
                        series = new[] { new { api_key = new { tracking_id = "synthetic-key" }, model = "deepseek-fixture",
                            buckets = new[] { new { time, usage = new { PROMPT_CACHE_HIT_TOKEN = 100, PROMPT_CACHE_MISS_TOKEN = 200, RESPONSE_TOKEN = 300, REQUEST = 7 } } } } } } } })
                    : JsonSerializer.Serialize(new { code = 0, data = new { biz_code = 0, biz_data = new {
                        data = new[] { new { currency = "USD", series = new[] { new { api_key = new { tracking_id = "synthetic-key" },
                            buckets = new[] { new { time, cost = "0.6" } } } } } } } } });
                return new(HttpStatusCode.OK) { Content = new StringContent(details) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.Host == "api.deepseek.com"
                ? """{"is_available":true,"balance_infos":[{"currency":"USD","total_balance":"42.00"}]}"""
                : """{"code":0,"data":{"biz_code":0,"biz_data":{"normal_wallets":[{"currency":"USD","balance":"12.50"}],"bonus_wallets":[]}}}""") };
        }
        ProviderConnections Connections() => new(vault, http: new HttpProviders(new AmpFixtureHandler(Response)), nativeCredentialReader: _ => null);
        try
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable("DEEPSEEK_PLATFORM_TOKEN", "environment-web");
            using var connection = Connections();
            Require((await connection.FetchAsync("deepseek", settings.Current, CancellationToken.None)).State == ReadingState.Ready
                && calls[^1] == ("platform.deepseek.com", "environment-web"), "Platform token was misrouted to the API-key endpoint.");
            vault.Save("provider:deepseek", "saved-api"); vault.Save("provider:deepseek:web", "saved-web");
            Require((await connection.FetchAsync("deepseek", settings.Current, CancellationToken.None)).State == ReadingState.Ready
                && calls[^1] == ("api.deepseek.com", "saved-api"), "Auto did not preserve the configured API key.");
            vault.Save("setting:deepseek:DEEPSEEK_USAGE_SOURCE", "web");
            var scope = connection.Scope("deepseek");
            Require((await connection.FetchAsync("deepseek", settings.Current, CancellationToken.None)).State == ReadingState.Ready
                && calls[^1] == ("platform.deepseek.com", "saved-web"), "Web did not select its independent saved session.");
            replaceDuringFetch = true;
            var changed = await connection.FetchAsync("deepseek", settings.Current, CancellationToken.None);
            Require(changed.State == ReadingState.Unavailable && changed.Windows.Count == 0 && connection.Scope("deepseek") != scope,
                "A replaced platform account published the old balance.");
            replaceDuringFetch = false; vault.Save("provider:deepseek:web", "saved-web");
            settings.Save(settings.Current with { EnabledProviders = ["deepseek"] });
            using var store = new DashboardStore(settings, vault, providerConnections: Connections());
            var window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate("deepseek");
            try
            {
                await store.RefreshProviderAsync("deepseek"); await Idle();
                Require(Descendants<TextBlock>(window).Any(text => text.Text.Contains("12.50 USD", StringComparison.Ordinal))
                    && Descendants<TextBlock>(window).Any(text => text.Text.Contains("Paid: 12.50 USD / Granted: 0.00 USD", StringComparison.Ordinal))
                    && !store.Readings["deepseek"].Windows.Any(limit => limit.UsedPercent.HasValue), "Platform money lost balance components or became synthetic quota.");
                Require(Descendants<PasswordBox>(window).Count() == 1, "Web exposes an unrelated API key field.");
                Capture(window, Path.Combine(directory, "windows-deepseek-web.png"));
                var priorScope = connection.Scope("deepseek");
                var detailsToggle = Descendants<CheckBox>(window).Single(box => AutomationProperties.GetName(box) == "Detailed Web usage");
                detailsToggle.IsChecked = true; await Idle(); await store.RefreshProviderAsync("deepseek"); await Idle();
                Require(vault.Load("setting:deepseek:DEEPSEEK_DETAILED_USAGE") == "true" && connection.Scope("deepseek") != priorScope,
                    "Detailed usage preference did not reach encrypted settings and scope.");
                Require(store.Readings["deepseek"].Windows.Count == 6 && store.Readings["deepseek"].State == ReadingState.Ready
                    && store.Readings["deepseek"].Windows.Skip(1).All(row => row.Group == "Usage")
                    && Descendants<TextBlock>(window).Any(text => text.Text == "$0.6000 · 600 tokens")
                    && Descendants<TextBlock>(window).Any(text => text.Text == "deepseek-fixture"), "Detailed Web rows did not reach actual WPF.");
                Capture(window, Path.Combine(directory, "windows-deepseek-details.png"));
                optionalFailure = true; await store.RefreshProviderAsync("deepseek"); await Idle();
                Require(store.Readings["deepseek"].State == ReadingState.Partial && store.Readings["deepseek"].Windows.Count == 1
                    && Descendants<TextBlock>(window).Any(text => text.Text.Contains("12.50 USD", StringComparison.Ordinal)),
                    "Optional failure erased the balance or retained old details.");
                Capture(window, Path.Combine(directory, "windows-deepseek-details-partial.png"));
                optionalFailure = false; changeDuringDetails = true;
                var late = await connection.FetchAsync("deepseek", settings.Current, CancellationToken.None);
                Require(late.State == ReadingState.Unavailable && late.Windows.Count == 0, "Late detailed usage survived a preference change.");
                vault.Save("setting:deepseek:DEEPSEEK_DETAILED_USAGE", "true");
                var detailsBeforeApi = detailCalls;
                var picker = Descendants<ComboBox>(window).Single(combo => AutomationProperties.GetName(combo) == "Usage source");
                picker.SelectedItem = "API"; await Idle();
                await store.RefreshProviderAsync("deepseek"); await Idle();
                Require(vault.Load("setting:deepseek:DEEPSEEK_USAGE_SOURCE") == "api"
                    && vault.Load("provider:deepseek:web") == "saved-web"
                    && calls[^1] == ("api.deepseek.com", "saved-api")
                    && Descendants<TextBlock>(window).Any(text => text.Text.Contains("42.00 USD", StringComparison.Ordinal)),
                    "Switching source mixed credentials or failed to update the balance.");
                Require(detailCalls == detailsBeforeApi && !Descendants<CheckBox>(window).Any(box => AutomationProperties.GetName(box) == "Detailed Web usage"),
                    "API selection fetched Web details or exposed an inactive toggle.");
                Capture(window, Path.Combine(directory, "windows-deepseek-api.png"));
            }
            finally { window.Close(); }
            File.WriteAllText(Path.Combine(directory, "windows-deepseek-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, network = "in-memory only", typedSourceAndEndpoint = "PASS", accountReplacement = "PASS",
                encryptedSlots = "PASS", actualConnectorStoreWpf = "PASS", detailsToggleAndRows = "PASS", optionalFailureKeepsBalance = "PASS", detailSelectionChange = "PASS", detailCalls
            }, JsonOptions));
        }
        finally
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, environment[key]);
            foreach (var key in new[] { "provider:deepseek", "provider:deepseek:web", "setting:deepseek:DEEPSEEK_USAGE_SOURCE", "setting:deepseek:DEEPSEEK_DETAILED_USAGE" }) vault.Delete(key);
            settings.Save(settings.Current with { EnabledProviders = enabled });
        }
    }
}
