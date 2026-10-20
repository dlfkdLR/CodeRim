using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task FactoryRotationStoreRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var originalSettings = settings.Current;
        var keys = new[] { "provider:factory", "chromium:factory:default", "browser:factory", "cookie:factory" };
        var originals = keys.ToDictionary(key => key, vault.Load);
        var results = new List<object>();
        try
        {
            settings.Save(settings.Current with { EnabledProviders = ["factory"] });
            var modes = new[] { "manual", "chromium" };
            var scenarios = new[] { "success", "quota-failure", "refresh-failure", "replace-quota", "aba-quota", "failed-cas", "generation-quota" };
            foreach (var mode in modes)
            foreach (var scenario in scenarios)
            {
                foreach (var key in keys) vault.Delete(key);
                var slot = mode == "manual" ? "provider:factory" : "chromium:factory:default";
                var profile = Path.GetFullPath(Path.Combine(directory, "synthetic-factory-rotation"));
                string Encode(string json) => mode == "manual" ? json : new ChromiumProviderCredential("factory", "default", profile, "https://app.factory.ai", json).Serialize();
                var original = Encode("""{"refresh_token":"synthetic-old-refresh"}""");
                var replacement = Encode("""{"refresh_token":"synthetic-replacement-refresh"}""");
                vault.Save(slot, original);
                var posts = 0; var quota = 0; var changed = false; var removed = false; var seeded = false;
                var emitted = new List<double?>();
                DashboardStore? activeStore = null;
                using var connection = new ProviderConnections(vault, new NativeProviders(new FactoryFixtureHandler(request =>
                {
                    if (request.RequestUri!.Host == "api.workos.com")
                    {
                        posts++;
                        if (scenario == "failed-cas" && !changed) { changed = true; vault.Save(slot, replacement); }
                        if (scenario == "refresh-failure" || changed && scenario != "failed-cas")
                            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"synthetic-rotated-access","refresh_token":"synthetic-rotated-refresh"}""") });
                    }
                    if (request.RequestUri.AbsolutePath == "/api/app/auth/me")
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"userProfile":{"id":"synthetic-user"}}""") });
                    quota++; var percentage = changed ? 31 : 17;
                    if (scenario == "generation-quota" && !changed)
                    { changed = true; System.Windows.Application.Current.Dispatcher.Invoke(() => activeStore!.InvalidateAccount("factory")); }
                    if (scenario is "replace-quota" or "aba-quota" && !changed)
                    {
                        changed = true; var current = vault.Load(slot)!; vault.Save(slot, replacement);
                        if (scenario == "aba-quota") vault.Save(slot, current);
                    }
                    if (scenario == "quota-failure") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                        usesTokenRateLimitsBilling = true, limits = new { standard = new { fiveHour = new { usedPercent = percentage } } }
                    })) });
                })), nativeCredentialReader: _ => null);
                using var store = new DashboardStore(settings, vault, providerConnections: connection);
                activeStore = store;
                store.ReadingUpdated += reading => emitted.Add(reading.Headline?.UsedPercent);
                store.PropertyChanged += (_, _) => { if (seeded && !store.Readings.ContainsKey("factory")) removed = true; };
                var first = store.RefreshProviderAsync("factory");
                store.Readings["factory"] = new("factory", ReadingState.Ready, [new("session", "5 hours", 8)], DateTimeOffset.Now);
                seeded = true; await first;
                for (var pass = 0; pass < 8; pass++) { await store.WaitForProviderIdleAsync("factory"); await Idle(); }
                var currentReading = store.Readings.GetValueOrDefault("factory");
                var passed = scenario switch
                {
                    "success" => quota == 1 && posts == 1 && emitted.Count == 1 && emitted[0] == 17 && !removed,
                    "quota-failure" => posts == 1 && currentReading?.Headline?.UsedPercent == 8 && currentReading.State == ReadingState.Stale && !removed,
                    "refresh-failure" => quota == 0 && currentReading?.State == ReadingState.NeedsAuth,
                    "failed-cas" => !emitted.Contains(17) && removed,
                    _ => !emitted.Contains(17) && removed
                };
                results.Add(new { mode, scenario, passed, posts, quota, emitted, removed, state = currentReading?.State.ToString() });
                File.WriteAllText(Path.Combine(directory, "windows-factory-rotation-store.json"), JsonSerializer.Serialize(results, JsonOptions));
                Require(passed, "Factory normal-polling scope regression: " + mode + " / " + scenario);
                if (scenario == "success")
                {
                    Require(CompanionFile.Read().Providers.Single(x => x.Id == "factory").AccountScope == connection.Scope("factory"),
                        "The quota snapshot was persisted with its pre-rotation account scope.");
                    var receipt = vault.LoadVersioned(slot)!;
                    Require(!vault.SaveIfUnchanged(slot, "outdated-version", receipt.Value, out var failedVersion) && failedVersion is null,
                        "A failed DPAPI CAS returned a rotation receipt.");
                    Require(vault.SaveIfUnchanged(slot, receipt.Version, receipt.Value, out var version) && version == vault.Version(slot),
                        "The DPAPI CAS receipt did not describe its exact committed ciphertext.");
                }
            }
        }
        finally
        {
            foreach (var item in originals) { if (item.Value is null) vault.Delete(item.Key); else vault.Save(item.Key, item.Value); }
            settings.Save(originalSettings);
        }
    }
}
