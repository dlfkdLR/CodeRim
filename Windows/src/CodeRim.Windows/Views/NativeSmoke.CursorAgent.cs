using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;
using Microsoft.Data.Sqlite;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task CursorAgentRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        Require(Path.GetFileName(CompanionFile.DataDirectory).StartsWith("CodeRim-Smoke-", StringComparison.Ordinal), "Cursor agent regression requires isolated storage.");
        var before = settings.Current;
        var snapshot = File.Exists(CompanionFile.SnapshotPath) ? File.ReadAllBytes(CompanionFile.SnapshotPath) : null;
        string[] slotNames = ["provider:cursor", "browser:cursor"];
        var slots = slotNames.ToDictionary(x => x, vault.Load);
        var environmentKeys = (NativeProviders.CredentialKeys("cursor") ?? []).ToDictionary(x => x, Environment.GetEnvironmentVariable);
        var root = Path.Combine(CompanionFile.DataDirectory, "cursor-agent-" + Guid.NewGuid().ToString("N"));
        var roaming = Path.Combine(root, "AppData", "Roaming");
        var authPath = Path.Combine(roaming, "Cursor", "auth.json");
        var configPath = Path.Combine(root, ".cursor", "cli-config.json");
        var editorPath = Path.Combine(roaming, "Cursor", "User", "globalStorage", "state.vscdb");
        var environment = new Dictionary<string, string?> { ["APPDATA"] = roaming };
        var now = DateTimeOffset.UtcNow;
        CursorLocalConnection Read() => NativeCredentials.ReadCursor(root, roaming, environment.GetValueOrDefault, now);
        string Jwt(string owner, bool expired = false) => "header." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
            { sub = owner, exp = now.ToUnixTimeSeconds() + (expired ? -60 : 3600) })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture-signature";
        static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        void Agent(string owner, string label, bool expired = false)
        {
            Write(authPath, JsonSerializer.Serialize(new { accessToken = Jwt(owner, expired) }));
            Write(configPath, JsonSerializer.Serialize(new { authInfo = new { authId = owner, email = label } }));
        }
        var observations = new List<object>(); var requests = 0;
        var expectedCookie = "WorkosCursorSessionToken=agent-owner::" + Jwt("agent-owner");
        var responseStatus = HttpStatusCode.OK; var pause = false;
        TaskCompletionSource? entered = null; TaskCompletionSource? release = null;
        DashboardStore? store = null; DashboardWindow? window = null;
        Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            foreach (var key in environmentKeys.Keys) Environment.SetEnvironmentVariable(key, null);
            foreach (var key in slots.Keys) vault.Delete(key);
            settings.Save(before with { EnabledProviders = ["cursor"] });
            var connections = new ProviderConnections(vault, new NativeProviders(new FactoryFixtureHandler(async request =>
            {
                Require(request.RequestUri is { Host: "cursor.com", AbsolutePath: "/api/usage-summary" }, "Cursor fixture escaped the quota endpoint.");
                Interlocked.Increment(ref requests);
                var cookie = request.Headers.GetValues("Cookie").Single();
                Require(cookie == expectedCookie, "Cursor request did not use the selected fixture session cookie.");
                var status = responseStatus; var used = 25;
                if (pause)
                {
                    pause = false; used = 75; entered!.TrySetResult();
                    await release!.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                return new(status) { Content = new StringContent(JsonSerializer.Serialize(new { individualUsage = new { plan = new { autoPercentUsed = used } }, membershipType = "pro" })) };
            })), nativeCredentialReader: id => id == "cursor" ? Read().Login?.Credential : null,
                nativeAccountReader: id => id == "cursor" ? Read().Login : null,
                nativeSummaryReader: id => id == "cursor" ? Read().Summary : null);
            var claude = new ClaudeIntegration(new(false, null), new ClaudeFixtureOperations(() => null), (_, _) => { });
            store = new DashboardStore(settings, vault, providerConnections: connections, claudeIntegration: claude);
            store.Readings.Remove("cursor");
            window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate("cursor");

            Write(configPath, """{"authInfo":{"authId":"agent-owner","email":"agent@example.invalid"}}""");
            await store.RefreshProviderAsync("cursor"); await Idle();
            Require(requests == 0 && Identity().IsVisible && IdentityValue("label") == "agent@example.invalid"
                && IdentityValue("source") == "cursor-agent" && store.Readings["cursor"].Windows.Count == 0,
                "Agent metadata-only detection authorized HTTP or failed to render Account.");
            Record("metadata-only");

            Agent("agent-owner", "agent@example.invalid"); responseStatus = HttpStatusCode.ServiceUnavailable;
            await store.RefreshProviderAsync("cursor"); await Idle();
            Require(requests == 1 && store.Readings["cursor"].State == ReadingState.Error && IdentityValue("label") == "agent@example.invalid"
                && store.Readings["cursor"].Windows.Count == 0, "Agent failure erased its Account or retained quota.");
            Record("usage-failure");
            responseStatus = HttpStatusCode.OK; await store.RefreshProviderAsync("cursor"); await Idle();
            Require(store.Readings["cursor"] is { State: ReadingState.Ready, Headline.UsedPercent: 25 } && IdentityValue("plan") == "",
                "Current agent session did not recover its own quota or invented a local plan.");
            Record("agent-ready"); Capture(window, Path.Combine(directory, "windows-cursor-agent-ready.png"));

            vault.Save("provider:cursor", "WorkosCursorSessionToken=saved::fixture-key");
            expectedCookie = "WorkosCursorSessionToken=saved::fixture-key";
            await store.RefreshProviderAsync("cursor"); await Idle();
            Require(store.ProviderAccountDisplay("cursor").Account is { Label: null, Source: "Cursor" }
                && IdentityValue("label") == "" && store.Readings["cursor"].Headline?.UsedPercent == 25, "Saved Cursor key borrowed the agent identity.");
            Record("explicit-key-isolation");
            var jar = new BrowserCookieJar([new("WorkosCursorSessionToken", "browser::fixture-key", "cursor.com", "/", true, true, 0)], ["cursor.com"]);
            expectedCookie = "WorkosCursorSessionToken=browser::fixture-key";
            vault.Save("browser:cursor", jar.Serialize()); await store.RefreshProviderAsync("cursor"); await Idle();
            Require(store.ProviderAccountDisplay("cursor").Account?.Label is null && store.Readings["cursor"].Headline?.UsedPercent == 25,
                "Cursor browser import borrowed the agent identity.");
            Record("browser-isolation"); vault.Delete("browser:cursor"); vault.Delete("provider:cursor");
            expectedCookie = "WorkosCursorSessionToken=agent-owner::" + Jwt("agent-owner");

            Agent("agent-owner", "agent@example.invalid", expired: true); var count = requests;
            await store.RefreshProviderAsync("cursor"); await Idle();
            Require(requests == count && store.Readings["cursor"].Windows.Count == 0 && IdentityValue("label") == "agent@example.invalid",
                "Expired agent token sent HTTP or lost display-only identity.");
            Record("expired-token");

            Agent("agent-owner", "agent@example.invalid"); await store.RefreshProviderAccountAsync("cursor");
            var committed = new List<ProviderReading>();
            void OnReading(ProviderReading reading) { if (reading.Id == "cursor") committed.Add(reading); }
            store.ReadingUpdated += OnReading;
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously); pause = true;
            var pending = store.RefreshProviderAsync("cursor"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Agent("new-owner", "new@example.invalid"); expectedCookie = "WorkosCursorSessionToken=new-owner::" + Jwt("new-owner");
            await store.RefreshProviderAccountAsync("cursor"); release.TrySetResult();
            await pending; await store.WaitForProviderIdleAsync("cursor"); await Idle(); store.ReadingUpdated -= OnReading;
            Require(committed.Count > 0 && committed.All(reading => reading.Headline?.UsedPercent != 75)
                && store.Readings["cursor"].Headline?.UsedPercent == 25 && IdentityValue("label") == "new@example.invalid",
                "Pending old agent response crossed into the replacement account.");
            Record("in-flight-owner-rotation");

            Write(configPath, """{"authInfo":{"authId":"old-owner","email":"old@example.invalid"}}""");
            await store.RefreshProviderAsync("cursor"); await Idle();
            Require(!Identity().IsVisible && store.Readings["cursor"].Headline?.UsedPercent == 25
                && Read().Login!.Credential.StartsWith("WorkosCursorSessionToken=new-owner::", StringComparison.Ordinal),
                "Mismatched agent metadata was displayed for another token owner.");
            Record("unbound-metadata-hidden");
            Agent("new-owner", "new@example.invalid");

            Directory.CreateDirectory(Path.GetDirectoryName(editorPath)!);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = editorPath, Pooling = false }.ToString()))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE ItemTable(key TEXT PRIMARY KEY,value TEXT); INSERT INTO ItemTable VALUES('cursorAuth/accessToken','editor-token'),('cursorAuth/stripeMembershipAuthId','editor-owner'),('cursorAuth/cachedEmail','editor@example.invalid'),('cursorAuth/stripeMembershipType','pro')";
                command.ExecuteNonQuery();
            }
            expectedCookie = "WorkosCursorSessionToken=editor-owner::editor-token";
            await store.RefreshProviderAsync("cursor"); await Idle();
            Require(IdentityValue("label") == "editor@example.invalid" && IdentityValue("source") == "Cursor" && IdentityValue("plan") == "Pro"
                && Read().Login?.Credential == "WorkosCursorSessionToken=editor-owner::editor-token", "Agent stole the active editor's quota ownership.");
            Record("editor-precedence");
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = editorPath, Pooling = false }.ToString()))
            {
                db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE ItemTable SET value=NULL WHERE key='cursorAuth/cachedEmail'"; command.ExecuteNonQuery();
            }
            await store.RefreshProviderAsync("cursor"); await Idle();
            Require(!Identity().IsVisible && Read().Login?.Account.Source == "Cursor" && store.Readings["cursor"].Headline?.UsedPercent == 25,
                "Missing editor email borrowed an unrelated agent label.");
            Record("editor-without-email"); File.Delete(editorPath); expectedCookie = "WorkosCursorSessionToken=new-owner::" + Jwt("new-owner");
            await store.RefreshProviderAsync("cursor"); await Idle();
            Require(IdentityValue("label") == "new@example.invalid" && IdentityValue("source") == "cursor-agent", "Removing editor session failed to rediscover the agent.");
            Record("agent-fallback-restored");

            environment["AGENT_CLI_CREDENTIAL_STORE"] = "memory"; count = requests;
            await store.RefreshProviderAsync("cursor"); await Idle();
            Require(requests == count && !Identity().IsVisible && store.Readings["cursor"].Windows.Count == 0,
                "Explicit in-memory agent store resurrected the old persisted token.");
            Record("memory-store-no-fallback");
            Receipt(completed: true);

            StackPanel Identity() => Descendants<StackPanel>(window).Single(x => AutomationProperties.GetAutomationId(x) == "provider.identity");
            string IdentityValue(string field) => Descendants<TextBlock>(Identity()).Single(x => AutomationProperties.GetAutomationId(x) == "provider.identity." + field).Text;
            void Record(string stage)
            {
                observations.Add(new { stage, accountVisible = Identity().IsVisible, label = IdentityValue("label"), source = IdentityValue("source"),
                    state = store.Readings.GetValueOrDefault("cursor")?.State.ToString(), windows = store.Readings.GetValueOrDefault("cursor")?.Windows.Count ?? 0,
                    used = store.Readings.GetValueOrDefault("cursor")?.Headline?.UsedPercent, requests });
                Receipt(completed: false);
            }
            void Receipt(bool completed) => File.WriteAllText(Path.Combine(directory, "windows-cursor-agent.json"), JsonSerializer.Serialize(new
                { completed, fixture = true, realAccount = false, files = "isolated current editor/agent stores", network = "in-memory only", observations }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            release?.TrySetResult();
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => window?.Close()); Restore(() => store?.Dispose());
            if (store is not null)
                try { await store.WaitForAccountWorkIdleAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            foreach (var pair in environmentKeys) Restore(() => Environment.SetEnvironmentVariable(pair.Key, pair.Value));
            foreach (var pair in slots) Restore(() => { if (pair.Value is null) vault.Delete(pair.Key); else vault.Save(pair.Key, pair.Value); });
            Restore(() => { if (snapshot is null) File.Delete(CompanionFile.SnapshotPath); else File.WriteAllBytes(CompanionFile.SnapshotPath, snapshot); });
            Restore(() => settings.Save(before)); Restore(() => { if (Directory.Exists(root)) Directory.Delete(root, true); });
        }
        if (cleanup.Count > 0) throw new AggregateException("Cursor agent regression cleanup failed.", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
