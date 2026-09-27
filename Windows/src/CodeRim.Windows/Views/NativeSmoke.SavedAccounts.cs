using System.IO;
using System.Text.Json;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task SavedAccountsRegression(CredentialVault vault, string directory)
    {
        var variables = new[] { "CODEX_HOME", "OPENAI_API_KEY", "CODEX_API_KEY" };
        var previous = variables.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var saved = vault.Load("accounts:codex");
        var home = Path.Combine(CompanionFile.DataDirectory, "account-switch-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(home, "auth.json"); var checks = new List<object>();
        Exception? failure = null; var cleanup = new List<Exception>(); var completed = false;
        SavedLogin Login(string subject, string generation)
        {
            var claims = new Dictionary<string, object> { ["email"] = subject + "@example.invalid", ["sub"] = subject,
                ["https://api.openai.com/auth"] = new { chatgpt_account_id = "fixture-workspace" } };
            var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var credential = JsonSerializer.Serialize(new { tokens = new { access_token = "synthetic-" + generation,
                refresh_token = "synthetic-" + generation, id_token = "synthetic." + payload + ".signature", account_id = "fixture-workspace" } });
            return new("codex", LoginIdentity.Codex(credential), credential, null);
        }
        static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
        try
        {
            Directory.CreateDirectory(home); CredentialVault.RestrictDirectory(home);
            foreach (var key in variables) Environment.SetEnvironmentVariable(key, key == "CODEX_HOME" ? home : null);
            var a = Login("switch-a", "current"); var old = Login("switch-b", "old"); var fresh = Login("switch-b", "rotated");
            var renewed = Login("switch-b", "newly-renewed"); var other = Login("switch-c", "external");
            var accounts = new SavedAccounts(vault);
            foreach (var scenario in new[] { "stale-row", "already-active", "removed-row", "verification-failure", "refresh-wait",
                "verification-renews", "external-login", "already-active-failure", "managed-policy", "cancel-before-write",
                "cancel-after-write", "renewal-then-failure", "renewal-then-cancel" })
            {
                var sameAccount = scenario is "already-active" or "already-active-failure";
                var initial = sameAccount ? fresh : a;
                if (File.Exists(path)) File.Delete(path); GuardedFile.WritePrivate(path, initial.Credential);
                var entries = scenario == "removed-row" ? new[] { a } : new[] { a, sameAccount || scenario == "refresh-wait" ? old : fresh };
                vault.Save("accounts:codex", JsonSerializer.Serialize(entries));
                using var cancellation = new CancellationTokenSource(); var policyCalls = 0; var verifyCalls = 0;
                Task<JsonElement> Rpc(string executable, string method, CancellationToken token)
                {
                    Require(executable == "synthetic-account-fixture.exe", "Unexpected switch executable.");
                    token.ThrowIfCancellationRequested();
                    if (method == "config/read")
                    {
                        policyCalls++;
                        if (scenario == "cancel-before-write") cancellation.Cancel();
                        return Task.FromResult(Json(scenario == "managed-policy"
                            ? """{"config":{"forced_chatgpt_workspace_id":"different-workspace"}}""" : """{"config":{}}"""));
                    }
                    Require(method == "account/read", "Unexpected switch RPC."); verifyCalls++;
                    if (scenario is "verification-renews" or "renewal-then-failure" or "renewal-then-cancel") GuardedFile.Replace(path, fresh.Credential, renewed.Credential);
                    if (scenario == "external-login") GuardedFile.Replace(path, fresh.Credential, other.Credential);
                    if (scenario is "cancel-after-write" or "renewal-then-cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                    return Task.FromResult(Json(scenario is "verification-failure" or "already-active-failure" or "renewal-then-failure"
                        ? """{"account":null}""" : JsonSerializer.Serialize(new { account = new { type = "chatgpt", email = fresh.Identity.Email } })));
                }
                Exception? error = null;
                try
                {
                    await accounts.SwitchAsync(old, "synthetic-account-fixture.exe", waitForRefresh: () =>
                    {
                        if (scenario == "refresh-wait") vault.Save("accounts:codex", JsonSerializer.Serialize(new[] { a, fresh }));
                        return Task.CompletedTask;
                    }, token: cancellation.Token, codexRpc: Rpc);
                }
                catch (Exception caught) when (caught is not OutOfMemoryException) { error = caught; }
                var expected = scenario switch
                {
                    "removed-row" or "managed-policy" or "cancel-before-write" => initial.Credential,
                    "verification-renews" or "renewal-then-failure" or "renewal-then-cancel" => renewed.Credential,
                    "external-login" => other.Credential,
                    _ => fresh.Credential
                };
                Require(GuardedFile.Read(path) == expected, "Switch changed the wrong credential generation: " + scenario);
                var shouldFail = scenario is "removed-row" or "verification-failure" or "external-login"
                    or "already-active-failure" or "managed-policy" or "cancel-before-write"
                    or "cancel-after-write" or "renewal-then-failure" or "renewal-then-cancel";
                Require(shouldFail ? error is not null : error is null, "Unexpected account switch result: " + scenario);
                Require(error is AccountSwitchCommittedException == (scenario is "verification-failure" or "external-login"
                        or "cancel-after-write" or "renewal-then-failure" or "renewal-then-cancel"),
                    "Switch misreported whether the Codex credential was committed: " + scenario);
                if (scenario == "removed-row") Require(policyCalls == 0 && verifyCalls == 0, "Removed row launched a CLI probe.");
                if (scenario is "managed-policy" or "cancel-before-write") Require(verifyCalls == 0, "Rejected preflight reached account verification.");
                if (scenario == "cancel-before-write") Require(error is OperationCanceledException, "Pre-commit cancellation was swallowed.");
                if (!shouldFail)
                {
                    Require(accounts.Read("codex").Single(account => account.Identity.Id == fresh.Identity.Id).Credential == expected,
                        "The vault did not retain the latest verified login: " + scenario);
                    Require(accounts.Read("codex").Single(account => account.Identity.Id == a.Identity.Id).Credential == a.Credential,
                        "The departing login was lost: " + scenario);
                }
                Require(!SavedAccounts.OperationInProgress, "An account operation remained locked after " + scenario);
                checks.Add(new { scenario, passed = true, policyCalls, verifyCalls, committedFailure = error is AccountSwitchCommittedException });
            }
            completed = true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            foreach (var (key, value) in previous) Restore(() => Environment.SetEnvironmentVariable(key, value));
            Restore(() => { if (saved is null) vault.Delete("accounts:codex"); else vault.Save("accounts:codex", saved); });
            Restore(() => { if (Directory.Exists(home)) Directory.Delete(home, true); });
            Restore(() => File.WriteAllText(Path.Combine(directory, "windows-saved-account-switch.json"), JsonSerializer.Serialize(new {
                completed = completed && cleanup.Count == 0, realAccount = false, vault = "Windows current-user DPAPI", cli = "injected read-only RPC fixture",
                cleanupFailures = cleanup.Count, checks }, JsonOptions)));
        }
        if (cleanup.Count > 0) throw new AggregateException("Saved account regression cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
