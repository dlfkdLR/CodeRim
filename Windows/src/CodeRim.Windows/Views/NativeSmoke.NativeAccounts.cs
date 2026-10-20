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
    private static async Task NativeAccountRegression(DashboardWindow dashboard, DashboardStore store, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        Require(Path.GetFileName(CompanionFile.DataDirectory).StartsWith("CodeRim-Smoke-", StringComparison.Ordinal), "Native account regression requires isolated storage.");
        (string Id, string Source, string Plan)[] cases = [("cursor", "Cursor", "Pro"), ("grok", "Grok", ""),
            ("commandcode", "Command Code", "Goat"), ("ollama", "Ollama", ""), ("opencode", "OpenCode", "Go")];
        var before = settings.Current;
        var oldReadings = cases.ToDictionary(x => x.Id, x => store.Readings.GetValueOrDefault(x.Id));
        var keys = cases.SelectMany(x => NativeProviders.CredentialKeys(x.Id) ?? ProviderCatalog.Find(x.Id)!.EnvironmentKeys).Distinct(StringComparer.Ordinal).ToArray();
        var environment = keys.ToDictionary(x => x, Environment.GetEnvironmentVariable);
        var slots = cases.SelectMany(x => new[] { "provider:" + x.Id, "browser:" + x.Id, "chromium:" + x.Id }).ToDictionary(x => x, vault.Load);
        var local = new Dictionary<string, NativeProviderLogin>(StringComparer.Ordinal)
        {
            ["cursor"] = new("WorkosCursorSessionToken=fixture-owner::cursor-fixture", new("cursor@example.invalid", "Cursor"), "pro"),
            ["grok"] = new("grok-fixture", new("grok@example.invalid", "Grok")),
            ["commandcode"] = new("commandcode-fixture", new("commandcode@example.invalid", "Command Code"))
        };
        const string payload = """{"usage":{"rolling":{"percent":25}},"limits":{"monthly":{"usage":0.25}},"config":{"creditUsagePercent":25},"individualUsage":{"plan":{"autoPercentUsed":25}},"membershipType":"pro","credits":{"monthlyCredits":75},"totalCost":25,"data":{"planId":"goat_monthly"},"org":{"id":"fixture-org"}}""";
        var reads = 0; var sent = new List<string?>(); var rotate = false; var responseStatus = HttpStatusCode.OK;
        var checks = new List<string>(); Exception? failure = null; var cleanup = new List<Exception>();
        using var connections = new ProviderConnections(vault, new NativeProviders(new AmpFixtureHandler(request =>
        {
            Require(request.RequestUri!.Host is "cursor.com" or "cli-chat-proxy.grok.com" or "api.commandcode.ai" or "ollama.com" or "opencode.ai", "Native account request escaped its provider origin.");
            sent.Add(request.Headers.TryGetValues("Cookie", out var cookie) ? cookie.Single() : request.Headers.Authorization?.Parameter);
            if (rotate) { rotate = false; local["cursor"] = local["cursor"] with { Credential = "WorkosCursorSessionToken=replaced::replacement", Account = new("replacement@example.invalid", "Cursor") }; }
            return new(responseStatus) { Content = new StringContent(payload) };
        })), nativeCredentialReader: id => id is "ollama" or "opencode" ? id + "-fixture" : null,
            nativeAccountReader: id => { reads++; return local.GetValueOrDefault(id); });
        try
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
            foreach (var slot in slots.Keys) vault.Delete(slot);
            settings.Save(before with { EnabledProviders = [..before.EnabledProviders.Concat(cases.Select(x => x.Id)).Distinct(StringComparer.Ordinal)] });
            foreach (var item in cases)
            {
                var reading = await connections.FetchAsync(item.Id, settings.Current, CancellationToken.None);
                var label = local.GetValueOrDefault(item.Id)?.Account.Label;
                Require(reading.State == ReadingState.Ready && reading.Headline?.UsedPercent == 25 && reading.Account == new ProviderAccountMetadata(label, item.Source), item.Id + " lost credential-bound account metadata.");
                Require(sent[^1] == (local.GetValueOrDefault(item.Id)?.Credential ?? item.Id + "-fixture"), item.Id + " used a different credential.");
                store.Readings[item.Id] = reading; dashboard.Navigate(item.Id); await Idle(); dashboard.UpdateLayout();
                var identity = Descendants<StackPanel>(dashboard).Single(x => AutomationProperties.GetAutomationId(x) == "provider.identity");
                TextBlock Value(string field) => Descendants<TextBlock>(identity).Single(x => AutomationProperties.GetAutomationId(x) == "provider.identity." + field);
                Require(identity.IsVisible && Value("label").Text == (label ?? "") && Value("plan").Text == item.Plan && Value("source").Text == item.Source, item.Id + " account section differs from the reference values.");
                var link = Descendants<TextBlock>(identity).SelectMany(x => x.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Single();
                Require(link.NavigateUri == ProviderAccountLinks.UsagePage(item.Id) && AutomationProperties.GetName(link) == "Open usage page", item.Id + " account destination is not the fixed provider page.");
                Capture(dashboard, Path.Combine(directory, "windows-native-account-" + item.Id + ".png"));
                checks.Add(item.Id + " credential, quota, account rows and fixed management link");
            }
            var originalCursor = local["cursor"]; var originalReads = reads;
            vault.Save("provider:cursor", "WorkosCursorSessionToken=saved::saved-fixture");
            var saved = await connections.FetchAsync("cursor", settings.Current, CancellationToken.None);
            Require(saved.State == ReadingState.Ready && saved.Account is { Label: null, Source: "Cursor" } && reads == originalReads
                && sent[^1] == "WorkosCursorSessionToken=saved::saved-fixture", "Saved Cursor credential borrowed local identity.");
            vault.Delete("provider:cursor");
            // The reference loader reads this exact key. The catalogue also
            // contains non-credential settings, so its first entry is not a key.
            const string environmentKey = "COMMAND_CODE_API_KEY";
            Environment.SetEnvironmentVariable(environmentKey, "environment-fixture"); originalReads = reads;
            var ambient = await connections.FetchAsync("commandcode", settings.Current, CancellationToken.None);
            Require(ambient.State == ReadingState.Ready && ambient.Account is { Label: null, Source: "Command Code" }
                && reads == originalReads && sent[^1] == "environment-fixture", "Environment credential borrowed local identity.");
            Environment.SetEnvironmentVariable(environmentKey, null); checks.Add("Saved and environment keys never borrow a local account label");
            var jar = new BrowserCookieJar([new("WorkosCursorSessionToken", "browser::fixture", "cursor.com", "/", true, true, 0)], ["cursor.com"]);
            originalReads = reads;
            var browser = await connections.VerifyBrowserAsync("cursor", settings.Current, jar);
            Require(browser.State == ReadingState.Ready && browser.Account is { Label: null, Source: "Cursor" } && reads == originalReads
                && sent[^1] == "WorkosCursorSessionToken=browser::fixture", "Browser credential borrowed local identity.");
            checks.Add("Scoped browser credential never borrows a local account label");
            rotate = true;
            var rotated = await connections.FetchAsync("cursor", settings.Current, CancellationToken.None);
            Require(rotated.State == ReadingState.Unavailable && rotated.Account is null && rotated.Windows.Count == 0, "Account rotation published the prior identity and quota.");
            local["cursor"] = originalCursor; checks.Add("Local credential rotation discards stale identity and quota");
            responseStatus = HttpStatusCode.Unauthorized;
            var denied = await connections.FetchAsync("cursor", settings.Current, CancellationToken.None);
            Require(denied.State == ReadingState.NeedsAuth && denied.Account is null && denied.Windows.Count == 0, "Failed authentication published account metadata.");
            checks.Add("Authentication failure publishes no identity or quota");
            responseStatus = HttpStatusCode.ServiceUnavailable;
            var unavailable = await connections.FetchAsync("cursor", settings.Current, CancellationToken.None);
            Require(unavailable.State == ReadingState.Error && unavailable.Windows.Count == 0, "Failed usage fetch invented a reading.");
            store.Readings["cursor"] = ReadingRetention.Merge(unavailable, store.Readings["cursor"]);
            Require(store.Readings["cursor"].Windows.Count == 0 && store.Readings["cursor"].UpdatedAt is null,
                "Cursor failure restored its previous quota or timestamp.");
            dashboard.Navigate("cursor"); await Idle();
            Require(!Descendants<ProgressBar>(dashboard).Any() && settings.Current.EnabledProviders.Contains("cursor", StringComparer.Ordinal),
                "Failed Cursor retained a quota bar or disabled the selected provider.");
            Capture(dashboard, Path.Combine(directory, "windows-native-account-cursor-failed.png"));
            checks.Add("Borrowed-provider server failure clears quota without disabling the provider");
            File.WriteAllText(Path.Combine(directory, "windows-native-accounts.json"), JsonSerializer.Serialize(new { completed = true,
                fixture = true, network = "in-memory only", realAccount = false, checks }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            foreach (var pair in environment) Restore(() => Environment.SetEnvironmentVariable(pair.Key, pair.Value));
            foreach (var pair in slots) Restore(() => { if (pair.Value is null) vault.Delete(pair.Key); else vault.Save(pair.Key, pair.Value); });
            foreach (var pair in oldReadings) Restore(() => { if (pair.Value is null) store.Readings.Remove(pair.Key); else store.Readings[pair.Key] = pair.Value; });
            Restore(() => settings.Save(before)); Restore(() => dashboard.Navigate("usage"));
        }
        if (cleanup.Count > 0) throw new AggregateException("Native account fixture cleanup failed.", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
