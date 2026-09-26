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
    private static async Task GlmAccountSummaryRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        Require(Path.GetFileName(CompanionFile.DataDirectory).StartsWith("CodeRim-Smoke-", StringComparison.Ordinal), "GLM account regression requires isolated storage.");
        var before = settings.Current;
        var snapshot = File.Exists(CompanionFile.SnapshotPath) ? File.ReadAllBytes(CompanionFile.SnapshotPath) : null;
        var keys = ScriptProviders.Catalog["glm"].Settings.Select(x => x.Key).ToArray();
        var environment = keys.ToDictionary(x => x, Environment.GetEnvironmentVariable);
        var saved = keys.ToDictionary(x => "setting:glm:" + x, x => vault.Load("setting:glm:" + x));
        var profile = GlmAuthentication.Serialize(new("local-fixture", "bigmodel-cn", "OpenCode"));
        var requests = new List<(string Host, string? Key)>(); var localReads = 0; var timeout = false; var pause = false; var includePlan = true;
        var responseStatus = HttpStatusCode.ServiceUnavailable;
        TaskCompletionSource? entered = null; TaskCompletionSource? release = null;
        DashboardStore? store = null; DashboardWindow? window = null;
        Exception? failure = null; var errors = new List<Exception>(); var checks = new List<string>();
        try
        {
            foreach (var key in keys) { Environment.SetEnvironmentVariable(key, null); vault.Delete("setting:glm:" + key); }
            settings.Save(before with { EnabledProviders = ["glm"] });
            var connections = new ProviderConnections(vault, nativeCredentialReader: _ => { localReads++; return profile; },
                scripts: new ScriptProviders(new FactoryFixtureHandler(async request =>
                {
                    Require(request.RequestUri!.Host is "api.z.ai" or "open.bigmodel.cn" or "www.bigmodel.cn", "GLM fixture escaped the vendor origins.");
                    requests.Add((request.RequestUri.Host, request.Headers.Authorization?.Parameter));
                    var status = responseStatus;
                    if (pause) { pause = false; entered!.TrySetResult(); await release!.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                    if (timeout) throw new OperationCanceledException("Synthetic GLM timeout");
                    var json = """{"success":true,"code":200,"data":{"level":"Plus","limits":[{"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":33}]}}""";
                    if (!includePlan) json = json.Replace("\"level\":\"Plus\",", "", StringComparison.Ordinal);
                    return new(status) { Content = new StringContent(json) };
                })));
            var claude = new ClaudeIntegration(new(false, null), new ClaudeFixtureOperations(() => null), (_, _) => { });
            store = new DashboardStore(settings, vault, providerConnections: connections, claudeIntegration: claude);
            store.Readings.Remove("glm"); await store.RefreshProviderAccountsAsync();
            window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate("glm"); await Idle();
            Require(requests.Count == 0 && Identity().IsVisible && Value("source") == "OpenCode" && Value("plan") == ""
                && Destination() == "https://open.bigmodel.cn/usage", "GLM initial Account depended on quota or lost its local console region.");
            checks.Add("Detected GLM tool and region render before HTTP with no invented plan");
            Capture(window, Path.Combine(directory, "windows-glm-account-initial.png"));
            var draft = Descendants<PasswordBox>(window).First(); draft.Password = "unsaved-glm-fixture";
            foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized })
            {
                responseStatus = status; await store.RefreshProviderAsync("glm"); await Idle();
                RequireAbsentProviderPresentation(window, store.Readings["glm"]);
                Require(Identity().IsVisible && Value("source") == "OpenCode" && Destination() == "https://open.bigmodel.cn/usage", "GLM error erased independent Account or changed its region.");
            }
            timeout = true; await store.RefreshProviderAsync("glm"); timeout = false; await Idle();
            RequireAbsentProviderPresentation(window, store.Readings["glm"]);
            Require(Identity().IsVisible && ReferenceEquals(draft, Descendants<PasswordBox>(window).First()) && draft.Password == "unsaved-glm-fixture", "GLM error replaced the account section or credential draft.");
            checks.Add("First503/401/timeout retains local Account and draft while removing failed quota");
            responseStatus = HttpStatusCode.OK; await store.RefreshProviderAsync("glm"); await Idle();
            Require(store.Readings["glm"].Headline?.UsedPercent == 33 && Value("plan") == "Plus", "GLM success did not publish the actual fixture quota and plan.");
            window.Navigate("providers"); await Idle(); var row = ProviderListStatus(window, "glm");
            Require(row.Text.Contains("Plus", StringComparison.Ordinal) && row.Text.Contains('%'), "GLM list omitted its successful plan/quota.");
            responseStatus = HttpStatusCode.ServiceUnavailable; await store.RefreshProviderAsync("glm"); await Idle();
            Require(ReferenceEquals(row, ProviderListStatus(window, "glm")) && row.Text.Contains("Plus", StringComparison.Ordinal), "GLM failed list discarded same-owner plan or replaced its row.");
            RequireFailedProviderListRow(window, store.Readings["glm"]);
            window.Navigate("glm"); await Idle();
            Require(Value("plan") == "Plus" && store.Readings["glm"].Windows.Count == 0, "GLM same-owner error erased known plan or retained quota.");
            Capture(window, Path.Combine(directory, "windows-glm-account-failed.png"));
            checks.Add("Successful quota/plan reaches Settings and list; same-owner failure retains plan but no quota");
            includePlan = false; responseStatus = HttpStatusCode.OK; await store.RefreshProviderAsync("glm"); await Idle();
            Require(store.Readings["glm"].Headline?.UsedPercent == 33 && store.Readings["glm"].Plan is null && Value("plan") == "",
                "Successful GLM quota without a plan retained an obsolete plan.");
            responseStatus = HttpStatusCode.ServiceUnavailable; await store.RefreshProviderAsync("glm"); await Idle();
            Require(Value("plan") == "" && store.ProviderAccountDisplay("glm").Plan is null,
                "GLM failure resurrected a plan cleared by the last successful response.");
            checks.Add("Successful response without level clears the previous plan; a later failure cannot resurrect it");
            includePlan = true; responseStatus = HttpStatusCode.OK; await store.RefreshProviderAsync("glm"); await Idle();
            Require(Value("plan") == "Plus", "GLM rotation fixture did not restore the positive plan precondition.");
            draft = Descendants<PasswordBox>(window).First(); draft.Password = "retained-glm-draft";
            profile = GlmAuthentication.Serialize(new("replacement-fixture", "global", "Claude Code"));
            await store.RefreshProviderAccountAsync("glm"); await Idle();
            Require(Value("source") == "Claude Code" && Value("plan") == "" && Destination() == "https://z.ai/manage-apikey/apikey-list"
                && store.AccountDisplay("glm").Reading is null && ReferenceEquals(draft, Descendants<PasswordBox>(window).First())
                && draft.Password == "retained-glm-draft", "GLM rotation retained another owner's plan/quota/link or replaced inputs.");
            checks.Add("Tool/key/region rotation clears prior plan and quota and updates the fixed management link in place");

            vault.Save("setting:glm:Z_AI_API_KEY", " "); Environment.SetEnvironmentVariable("Z_AI_API_KEY", "environment-fixture");
            responseStatus = HttpStatusCode.ServiceUnavailable;
            vault.Save("setting:glm:Z_AI_REGION", "bigmodel-cn"); var reads = localReads;
            Require(connections.ReadAccountSummary("glm")?.Account is { Source: "api", Region: "bigmodel-cn" } && localReads == reads,
                "Explicit GLM display borrowed local metadata or ignored the blank saved-key fallback.");
            await store.RefreshProviderAccountAsync("glm"); await store.RefreshProviderAsync("glm"); await Idle();
            Require(Value("source") == "api" && Destination() == "https://open.bigmodel.cn/usage" && requests[^1] == ("open.bigmodel.cn", "environment-fixture"),
                "GLM environment-key display and request region disagree.");
            vault.Save("setting:glm:Z_AI_API_KEY", "saved-fixture"); vault.Save("setting:glm:Z_AI_REGION", "global");
            await store.RefreshProviderAccountAsync("glm"); await store.RefreshProviderAsync("glm"); await Idle();
            Require(Value("source") == "api" && Destination() == "https://z.ai/manage-apikey/apikey-list" && requests[^1] == ("api.z.ai", "saved-fixture"),
                "GLM saved key lost precedence or used a borrowed local console.");
            checks.Add("Saved/environment explicit keys suppress local tool metadata and use the selected request region");
            vault.Save("setting:glm:Z_AI_API_KEY", "invalid\nkey"); var count = requests.Count;
            await store.RefreshProviderAccountAsync("glm"); await store.RefreshProviderAsync("glm"); await Idle();
            Require(requests.Count == count && !Identity().IsVisible && store.Readings["glm"].State == ReadingState.NeedsAuth,
                "Invalid explicit GLM key fell back to a local Account or authorized a request.");
            checks.Add("Invalid explicit credentials never fall back to a local identity or authorize HTTP");

            vault.Delete("setting:glm:Z_AI_API_KEY"); Environment.SetEnvironmentVariable("Z_AI_API_KEY", null);
            profile = GlmAuthentication.Serialize(new("racing-old", "global", "OpenCode")); await store.RefreshProviderAccountAsync("glm");
            responseStatus = HttpStatusCode.OK; entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously); pause = true;
            var committed = new List<ProviderReading>(); void Record(ProviderReading reading) { if (reading.Id == "glm") committed.Add(reading); }
            store.ReadingUpdated += Record;
            var pending = store.RefreshProviderAsync("glm"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            profile = GlmAuthentication.Serialize(new("racing-new", "bigmodel-cn", "ZCode")); responseStatus = HttpStatusCode.ServiceUnavailable;
            release.TrySetResult(); await pending; await store.WaitForProviderIdleAsync("glm"); await Idle(); store.ReadingUpdated -= Record;
            Require(Value("source") == "ZCode" && Value("plan") == "" && Destination() == "https://open.bigmodel.cn/usage"
                && committed.All(x => x.Windows.Count == 0 && x.Plan is null), "In-flight GLM response published another tool account's plan/quota.");
            checks.Add("In-flight successful old-owner plan/quota is discarded after local tool rotation");
            settings.Save(settings.Current with { EnabledProviders = [] }); await Idle();
            Require(store.ProviderAccountDisplay("glm") is { Account: null, Plan: null }, "Disabled GLM retained independent Account metadata.");
            checks.Add("Provider removal clears the independent Account and known plan");
            File.WriteAllText(Path.Combine(directory, "windows-glm-account-summary.json"), JsonSerializer.Serialize(new
                { completed = true, fixture = true, realAccount = false, network = "in-memory only", localCredentials = "injected profile", checks }, JsonOptions));

            StackPanel Identity() => Descendants<StackPanel>(window).Single(x => AutomationProperties.GetAutomationId(x) == "provider.identity");
            string Value(string field) => Descendants<TextBlock>(Identity()).Single(x => AutomationProperties.GetAutomationId(x) == "provider.identity." + field).Text;
            string Destination() => Descendants<TextBlock>(Identity()).SelectMany(x => x.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Single().NavigateUri.AbsoluteUri;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            release?.TrySetResult();
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { errors.Add(error); } }
            Restore(() => window?.Close()); Restore(() => store?.Dispose());
            if (store is not null)
                try { await store.WaitForAccountWorkIdleAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception error) when (error is not OutOfMemoryException) { errors.Add(error); }
            foreach (var pair in environment) Restore(() => Environment.SetEnvironmentVariable(pair.Key, pair.Value));
            foreach (var pair in saved) Restore(() => { if (pair.Value is null) vault.Delete(pair.Key); else vault.Save(pair.Key, pair.Value); });
            Restore(() => { if (snapshot is null) File.Delete(CompanionFile.SnapshotPath); else File.WriteAllBytes(CompanionFile.SnapshotPath, snapshot); });
            Restore(() => settings.Save(before));
        }
        if (errors.Count > 0) throw new AggregateException("GLM Account fixture cleanup failed.", failure is null ? errors : errors.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
