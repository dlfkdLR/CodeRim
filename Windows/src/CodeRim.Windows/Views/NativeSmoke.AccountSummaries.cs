using System.Collections.Concurrent;
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
    private static async Task IndependentAccountSummaryRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        Require(Path.GetFileName(CompanionFile.DataDirectory).StartsWith("CodeRim-Smoke-", StringComparison.Ordinal), "Account summary regression requires isolated storage.");
        string[] ids = ["cursor", "grok", "commandcode", "opencode", "ollama"];
        var before = settings.Current;
        var snapshot = File.Exists(CompanionFile.SnapshotPath) ? File.ReadAllBytes(CompanionFile.SnapshotPath) : null;
        var keys = ids.SelectMany(id => NativeProviders.CredentialKeys(id) ?? []).Distinct(StringComparer.Ordinal).ToDictionary(x => x, Environment.GetEnvironmentVariable);
        var slots = ids.SelectMany(id => new[] { "provider:" + id, "browser:" + id }).ToDictionary(x => x, vault.Load);
        var logins = new ConcurrentDictionary<string, NativeProviderLogin>(StringComparer.Ordinal);
        var summaries = new ConcurrentDictionary<string, NativeAccountSummary>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var source = id switch { "cursor" => "Cursor", "grok" => "Grok", "commandcode" => "Command Code", "opencode" => "OpenCode", _ => "Ollama" };
            logins[id] = new(id == "cursor" ? "WorkosCursorSessionToken=fixture::fixture-key" : id + "-fixture-key", new(id + "@example.invalid", source), id == "cursor" ? "pro" : id == "opencode" ? "Go" : null);
            summaries[id] = NativeAccountSummary.FromLogin(logins[id])!;
        }
        const string payload = """{"usage":{"rolling":{"percent":25}},"limits":{"monthly":{"usage":0.25}},"config":{"creditUsagePercent":25},"individualUsage":{"plan":{"autoPercentUsed":25}},"membershipType":"pro","credits":{"monthlyCredits":75},"totalCost":25,"data":{"planId":"goat_monthly"},"org":{"id":"fixture-org"}}""";
        var responseStatus = HttpStatusCode.ServiceUnavailable; var timeout = false; var requests = 0; var pause = false;
        var staleCommandReplies = 0;
        TaskCompletionSource? entered = null; TaskCompletionSource? release = null;
        using var summaryRelease = new ManualResetEventSlim(true);
        TaskCompletionSource? summaryEntered = null; var blockSummary = false;
        DashboardStore? store = null; DashboardWindow? window = null;
        Exception? failure = null; var cleanup = new List<Exception>(); var checks = new List<string>();
        try
        {
            foreach (var key in keys.Keys) Environment.SetEnvironmentVariable(key, null);
            foreach (var key in slots.Keys) vault.Delete(key);
            settings.Save(before with { EnabledProviders = ids });
            var connections = new ProviderConnections(vault, new NativeProviders(new FactoryFixtureHandler(async request =>
            {
                Require(request.RequestUri!.Host is "cursor.com" or "cli-chat-proxy.grok.com" or "api.commandcode.ai" or "ollama.com" or "opencode.ai", "Summary fixture escaped its provider origin.");
                Interlocked.Increment(ref requests);
                var staleReady = request.RequestUri.Host == "api.commandcode.ai" && staleCommandReplies > 0;
                if (staleReady) staleCommandReplies--;
                if (pause) { pause = false; entered!.TrySetResult(); await release!.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                if (timeout) throw new OperationCanceledException("Synthetic provider timeout");
                return new(staleReady ? HttpStatusCode.OK : responseStatus) { Content = new StringContent(payload) };
            })), nativeCredentialReader: id => logins.GetValueOrDefault(id)?.Credential,
                nativeAccountReader: id => logins.GetValueOrDefault(id), nativeSummaryReader: id =>
                {
                    if (id == "ollama" && blockSummary)
                    {
                        blockSummary = false; summaryEntered!.TrySetResult();
                        Require(summaryRelease.Wait(TimeSpan.FromSeconds(10)), "Summary fixture release timed out.");
                    }
                    return summaries.GetValueOrDefault(id);
                });
            var claude = new ClaudeIntegration(new(false, null), new ClaudeFixtureOperations(() => null), (_, _) => { });
            store = new DashboardStore(settings, vault, providerConnections: connections, claudeIntegration: claude);
            foreach (var id in ids) store.Readings.Remove(id);
            await store.RefreshProviderAccountsAsync();
            Require(requests == 0 && ids.All(id => store.ProviderAccountDisplay(id).Account == summaries[id].Account), "Account detection sent HTTP or depended on a quota result.");
            window = new DashboardWindow(store, settings, vault); window.Show();
            foreach (var id in ids)
            {
                window.Navigate(id); await Idle();
                Require(Identity().IsVisible && IdentityValue("label") == id + "@example.invalid", id + " initial Account is missing before HTTP.");
                Require(ProviderHeaderStatus(window) == "Not connected", id + " initial Settings invented a connection before HTTP.");
                var connectionDraft = Descendants<PasswordBox>(window).First(); connectionDraft.Password = "preserved-display-fixture";
                foreach (var state in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized })
                {
                    responseStatus = state; await store.RefreshProviderAsync(id); await Idle();
                    Require(store.Readings[id].Windows.Count == 0 && store.Readings[id].State is ReadingState.Error or ReadingState.NeedsAuth
                        && Identity().IsVisible && IdentityValue("label") == id + "@example.invalid", id + " failed request erased Account or retained quota.");
                    Require(!ProviderAvailability.ShowsInNotch(id, store.Readings[id], true), id + " failed quota left a notch ring solely because Account exists.");
                    RequireAbsentProviderPresentation(window, store.Readings[id]);
                }
                timeout = true; await store.RefreshProviderAsync(id); timeout = false; await Idle();
                Require(store.Readings[id].Windows.Count == 0 && Identity().IsVisible, id + " timeout erased its detected account.");
                RequireAbsentProviderPresentation(window, store.Readings[id]);
                Capture(window, Path.Combine(directory, "windows-independent-account-" + id + "-failed.png"));
                checks.Add(id + " initial Account without HTTP, first 503/401/timeout retains Account and removes quota");
                foreach (var state in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized })
                {
                    responseStatus = HttpStatusCode.OK; await store.RefreshProviderAsync(id); await Idle();
                    Require(ProviderHeaderStatus(window) == "Connected" && Descendants<ProgressBar>(window).Any()
                        && Identity().IsVisible, id + " successful request did not recover the mounted Settings quota.");
                    responseStatus = state; await store.RefreshProviderAsync(id); await Idle();
                    RequireAbsentProviderPresentation(window, store.Readings[id]);
                    Require(Identity().IsVisible && IdentityValue("label") == id + "@example.invalid"
                        && ReferenceEquals(connectionDraft, Descendants<PasswordBox>(window).First())
                        && connectionDraft.Password == "preserved-display-fixture", id + " failure presentation rebuilt Account or credential inputs.");
                }
                checks.Add(id + " actual 503/401 Settings projection removes duplicate error copy and recovers without replacing Account or drafts");
                window.Navigate("providers"); await Idle();
                var listLabel = ProviderListStatus(window, id);
                RequireFailedProviderListRow(window, store.Readings[id]);
                responseStatus = HttpStatusCode.OK; await store.RefreshProviderAsync(id); await Idle();
                Require(ReferenceEquals(listLabel, ProviderListStatus(window, id)) && listLabel.Text.Contains("25%", StringComparison.Ordinal),
                    id + " provider list failed to recover quota in place.");
                responseStatus = HttpStatusCode.ServiceUnavailable; await store.RefreshProviderAsync(id); await Idle();
                RequireFailedProviderListRow(window, store.Readings[id]);
                Require(ReferenceEquals(listLabel, ProviderListStatus(window, id)) && listLabel.Text.Contains(id + "@example.invalid", StringComparison.Ordinal)
                    && listLabel.Text.Contains("via " + summaries[id].Account!.Source, StringComparison.Ordinal), id + " provider list lost its stable label or independent account source.");
                Capture(window, Path.Combine(directory, "windows-independent-account-" + id + "-list-failed.png"));
                checks.Add(id + " mounted provider list recovers quota and retains Account/source through a later failure without rebuilding its row");
            }

            window.Navigate("commandcode"); responseStatus = HttpStatusCode.OK;
            await store.RefreshProviderAsync("commandcode");
            Require(store.ProviderAccountDisplay("commandcode").Plan == "GOAT" && store.Readings["commandcode"].Windows.Count > 0, "Command Code success did not bind its known plan.");
            responseStatus = HttpStatusCode.ServiceUnavailable; await store.RefreshProviderAsync("commandcode");
            Require(store.ProviderAccountDisplay("commandcode").Plan == "GOAT" && store.Readings["commandcode"].Windows.Count == 0, "Same-owner failure lost the known plan or restored quota.");
            var draft = Descendants<PasswordBox>(window).First(); draft.Password = "unsaved-fixture-draft";
            logins["commandcode"] = new("replacement-command-key", new("replacement@example.invalid", "Command Code"));
            summaries["commandcode"] = NativeAccountSummary.FromLogin(logins["commandcode"])!;
            await store.RefreshProviderAccountAsync("commandcode"); await Idle();
            Require(store.AccountDisplay("commandcode") is { Label: "replacement@example.invalid", Plan: null, Reading: null }
                && IdentityValue("label") == "replacement@example.invalid" && IdentityValue("plan") == ""
                && ReferenceEquals(draft, Descendants<PasswordBox>(window).First()) && draft.Password == "unsaved-fixture-draft", "Account rotation mixed owners or replaced the credential draft.");
            checks.Add("Command Code known plan survives same-owner failure and clears on rotation without replacing inputs");

            window.Navigate("cursor");
            vault.Save("provider:cursor", "WorkosCursorSessionToken=saved::key"); await store.RefreshProviderAccountAsync("cursor");
            Require(store.ProviderAccountDisplay("cursor").Account is { Label: null, Source: "Cursor" }, "Saved credential borrowed the local email.");
            var jar = new BrowserCookieJar([new("WorkosCursorSessionToken", "browser::key", "cursor.com", "/", true, true, 0)], ["cursor.com"]);
            vault.Save("browser:cursor", jar.Serialize()); await store.RefreshProviderAccountAsync("cursor");
            Require(store.ProviderAccountDisplay("cursor").Account is { Label: null, Source: "Cursor" }, "Browser import borrowed the local email.");
            vault.Delete("browser:cursor"); vault.Delete("provider:cursor");
            Environment.SetEnvironmentVariable("COMMAND_CODE_API_KEY", "environment-fixture-key"); await store.RefreshProviderAccountAsync("commandcode");
            Require(store.ProviderAccountDisplay("commandcode") is { Account.Label: null, Account.Source: "Command Code", Plan: null }, "Environment override borrowed local metadata.");
            Environment.SetEnvironmentVariable("COMMAND_CODE_API_KEY", null);
            checks.Add("Saved, browser and environment selections suppress unrelated local labels and plans");

            logins.TryRemove("cursor", out _);
            summaries["cursor"] = NativeAccountSummary.Cursor(new Dictionary<string, byte[]> { ["cursorAuth/cachedEmail"] = System.Text.Encoding.UTF8.GetBytes("display-only@example.invalid") })!;
            var count = requests; await store.RefreshProviderAsync("cursor"); await Idle();
            Require(requests == count && Identity().IsVisible && IdentityValue("label") == "display-only@example.invalid" && store.Readings["cursor"].Windows.Count == 0, "Token-less Cursor summary authorized a request or disappeared.");
            summaries.TryRemove("cursor", out _); await store.RefreshProviderAccountAsync("cursor"); await Idle();
            Require(!Identity().IsVisible && store.AccountDisplay("cursor").Reading is null, "Removed Cursor source retained Account or quota.");
            checks.Add("Token-less Cursor remains display-only; source removal clears the mounted Account section");

            window.Navigate("grok"); logins.TryRemove("grok", out _);
            NativeAccountSummary Expired(string key, string email) => NativeAccountSummary.Grok(JsonSerializer.SerializeToElement(new Dictionary<string, object>
                { ["https://auth.x.ai"] = new { key, email, expires_at = "2020-01-01T00:00:00Z" } }), DateTimeOffset.UtcNow)!;
            summaries["grok"] = Expired("expired-a", "expired-a@example.invalid"); count = requests;
            await store.RefreshProviderAsync("grok"); var firstScope = connections.Scope("grok");
            Require(requests == count && store.ProviderAccountDisplay("grok").Account?.Label == "expired-a@example.invalid", "Expired Grok authorized HTTP or erased Account.");
            store.Readings["grok"] = new("grok", ReadingState.Ready, [new("fixture", "Fixture", 75)], DateTimeOffset.Now);
            summaries["grok"] = Expired("expired-b", "expired-b@example.invalid"); await store.RefreshProviderAccountAsync("grok"); await Idle();
            Require(connections.Scope("grok") == firstScope && store.AccountDisplay("grok") is { Label: "expired-b@example.invalid", Reading: null }, "Expired Grok owners collided because their request scope was identical.");
            checks.Add("Expired Grok never sends HTTP and distinguishes changed owners even when request scopes match");

            window.Navigate("commandcode"); await store.RefreshProviderAccountAsync("commandcode");
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously); pause = true;
            // The old operation must actually produce quota and GOAT; only the
            // retry fails. Otherwise an always-empty 503 would hide a broken guard.
            staleCommandReplies = 4;
            var committed = new List<ProviderReading>();
            void Record(ProviderReading reading) { if (reading.Id == "commandcode") committed.Add(reading); }
            store.ReadingUpdated += Record;
            var pending = store.RefreshProviderAsync("commandcode"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            summaries["commandcode"] = NativeAccountSummary.Create(new("metadata-changed@example.invalid", "Command Code"), null, logins["commandcode"].Credential);
            await store.RefreshProviderAccountAsync("commandcode"); release.TrySetResult(); await pending;
            await store.WaitForProviderIdleAsync("commandcode"); await Idle();
            store.ReadingUpdated -= Record;
            Require(staleCommandReplies == 0 && committed.Count > 0 && committed.All(reading => reading.Windows.Count == 0 && reading.Plan is null)
                && store.AccountDisplay("commandcode").Label == "metadata-changed@example.invalid" && store.Readings["commandcode"].Windows.Count == 0 && store.ProviderAccountDisplay("commandcode").Plan is null,
                "An old in-flight response restored another display owner's quota or plan.");
            checks.Add("Metadata-only rotation invalidates a pending response through the production store generation guard");

            // Exercise the first-read cache check, not only changes between two
            // already published summaries. The credential/email stay identical.
            logins["cursor"] = new("WorkosCursorSessionToken=fixture::fixture-key", new("same@example.invalid", "Cursor"), "pro");
            summaries["cursor"] = NativeAccountSummary.Create(logins["cursor"].Account, "ultra", logins["cursor"].Credential);
            store.InvalidateAccount("cursor");
            store.Readings["cursor"] = new("cursor", ReadingState.Stale, [new("fixture", "Fixture", 75)], DateTimeOffset.Now, Plan: "pro", Account: logins["cursor"].Account);
            await store.RefreshProviderAccountAsync("cursor");
            Require(store.AccountDisplay("cursor") is { Label: "same@example.invalid", Plan: "ultra", Reading: null }, "First summary paired a changed Cursor plan with restored quota.");
            checks.Add("First summary rejects restored Cursor quota after a plan-only change");

            summaryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); summaryRelease.Reset(); blockSummary = true; count = requests;
            var removedPending = store.RefreshProviderAsync("ollama"); await summaryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            settings.Save(settings.Current with { EnabledProviders = settings.Current.EnabledProviders.Where(id => id != "ollama").ToArray() });
            summaryRelease.Set(); await removedPending;
            Require(requests == count && store.ProviderAccountDisplay("ollama").Account is null, "Provider removal during local detection still started HTTP.");
            checks.Add("Provider removal during asynchronous account detection sends no new HTTP request");
            settings.Save(settings.Current with { EnabledProviders = [] }); await Idle();
            Require(ids.All(id => store.ProviderAccountDisplay(id) is { Account: null, Plan: null } && store.AccountDisplay(id).Reading is null), "Removing providers left visible account data.");
            checks.Add("Provider removal clears independent account data");

            File.WriteAllText(Path.Combine(directory, "windows-independent-accounts.json"), JsonSerializer.Serialize(new
                { completed = true, fixture = true, realAccount = false, network = "in-memory only", checks }, JsonOptions));

            StackPanel Identity() => Descendants<StackPanel>(window).Single(x => AutomationProperties.GetAutomationId(x) == "provider.identity");
            string IdentityValue(string field) => Descendants<TextBlock>(Identity()).Single(x => AutomationProperties.GetAutomationId(x) == "provider.identity." + field).Text;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            release?.TrySetResult(); summaryRelease.Set();
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => window?.Close()); Restore(() => store?.Dispose());
            // Drain canceled readers before restoring shared environment/vault
            // fixtures; late continuations must not observe the next test's data.
            if (store is not null)
                try { await store.WaitForAccountWorkIdleAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            foreach (var pair in keys) Restore(() => Environment.SetEnvironmentVariable(pair.Key, pair.Value));
            foreach (var pair in slots) Restore(() => { if (pair.Value is null) vault.Delete(pair.Key); else vault.Save(pair.Key, pair.Value); });
            Restore(() => { if (snapshot is null) File.Delete(CompanionFile.SnapshotPath); else File.WriteAllBytes(CompanionFile.SnapshotPath, snapshot); });
            Restore(() => settings.Save(before));
        }
        if (cleanup.Count > 0) throw new AggregateException("Independent Account fixture cleanup failed.", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
