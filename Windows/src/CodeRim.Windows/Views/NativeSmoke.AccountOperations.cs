using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static readonly string[] OperationInferenceScopes = ["user:inference"];
    private static async Task SavedAccountOperationRegression(CredentialVault vault, string home, SavedLogin codex, SavedLogin selectedCodex, List<object> checks)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
        {
            checks.Add(new { scenario = "operation-interprocess-fixtures", status = "NOT_RUN", reason = "Requires an isolated hosted runner.", realAccount = false });
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        var expectedName = "CodeRim.AccountOperations." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.User!.Value)));
        Require(AccountOperationLease.Name == expectedName, "Account operation identity depends on a session, provider or data directory.");
        var name = expectedName + ".Test." + Guid.NewGuid().ToString("N");
        var fixture = Path.Combine(Environment.CurrentDirectory, "Windows", "tests", "CodeRim.ProcessFixture", "bin", "Release", "net10.0", "CodeRim.ProcessFixture.exe");
        Require(File.Exists(fixture), "The independent raw mutex fixture was not built.");
        var root = Path.Combine(home, "operation-lease"); Directory.CreateDirectory(root);
        var claudeHome = Path.Combine(root, "claude"); Directory.CreateDirectory(claudeHome); CredentialVault.RestrictDirectory(claudeHome);
        var claudeConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var claudeSaved = vault.Load("accounts:claude");
        var originalCodex = GuardedFile.ReadIfPresent(Path.Combine(home, "auth.json"));
        var originalCodexSaved = vault.Load("accounts:codex");
        Exception? failure = null; var cleanup = new List<Exception>();
        static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
        void Checked(string scenario) => checks.Add(new { scenario = "operation-" + scenario, passed = true, crossSession = false, realAccount = false });
        var operationEntries = 0;
        var accounts = new SavedAccounts(vault, () => { operationEntries++; return AccountOperationLease.Acquire(name); });
        MutexFixture Start(string mode) => StartMutexFixture(fixture, root, name, mode, cleanup);
        string[] Inventory() => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFileSystemEntries(Path.Combine(CompanionFile.DataDirectory, "vault"), "*", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal).ToArray();
        async Task Probe(bool busy)
        {
            using var child = Start("try");
            Require(await child.ReadLine() == (busy ? "busy" : "owned"), "Raw child observed the wrong mutex ownership.");
            Require(await child.Exit() == (busy ? 4 : 0), "Raw child mutex probe failed.");
        }
        try
        {
            var codexPath = Path.Combine(home, "auth.json");
            if (originalCodex is null) GuardedFile.CreateIfAbsent(codexPath, codex.Credential);
            else GuardedFile.Replace(codexPath, originalCodex, codex.Credential);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeHome);
            var oauth = JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = "synthetic-access", refreshToken = "synthetic-refresh",
                expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), scopes = OperationInferenceScopes } });
            var profile = JsonSerializer.Serialize(new { oauthAccount = new { emailAddress = "lease@example.invalid", organizationUuid = "fixture-org", accountUuid = "fixture-claude" } });
            var claude = new SavedLogin("claude", LoginIdentity.Claude(oauth, profile), oauth, profile);
            GuardedFile.WritePrivate(Path.Combine(claudeHome, ".credentials.json"), oauth);
            GuardedFile.WritePrivate(Path.Combine(claudeHome, ".claude.json"), profile);
            vault.Save("accounts:claude", JsonSerializer.Serialize(new[] { claude }));
            vault.Save("accounts:codex", JsonSerializer.Serialize(new[] { codex, selectedCodex }));
            using (var holder = Start("hold"))
            {
                Require(await holder.ReadLine() == "owned", "Raw child did not acquire the operation mutex.");
                using var retained = Mutex.OpenExisting("Global\\" + name);
                Checked("global-namespace");
                foreach (var login in new[] { codex, claude })
                {
                    foreach (var operation in new[] { "save", "save-async", "add", "remove", "switch" })
                    {
                        var before = vault.Version("accounts:" + login.Provider); var paths = SavedAccounts.Paths(login.Provider);
                        var credential = GuardedFile.Read(paths.Credential); var oldProfile = paths.Profile is null ? null : GuardedFile.Read(paths.Profile);
                        var inventory = Inventory();
                        var callbacks = 0; Exception? error = null;
                        Task Wait() { callbacks++; return Task.CompletedTask; }
                        Task<JsonElement> Rpc(string _, string __, CancellationToken ___) { callbacks++; return Task.FromResult(Json("{}")); }
                        try
                        {
                            switch (operation)
                            {
                                case "save": accounts.SaveCurrent(login.Provider); break;
                                case "save-async": await accounts.SaveCurrentAsync(login.Provider, "missing-fixture.exe", Wait); break;
                                case "add": await accounts.AddAsync(login.Provider, "missing-fixture.exe", CancellationToken.None); break;
                                case "remove": accounts.Remove(login.Provider, login.Identity.Id); break;
                                case "switch": await accounts.SwitchAsync(login, "missing-fixture.exe", Wait, Rpc); break;
                            }
                        }
                        catch (Exception caught) when (caught is not OutOfMemoryException) { error = caught; }
                        Require(error is InvalidOperationException && error.Message == AccountOperationLease.BusyMessage, "A blocked service entry failed for a different reason.");
                        Require(callbacks == 0 && !SavedAccounts.OperationInProgress, "A blocked entry performed work or retained local busy.");
                        Require(vault.Version("accounts:" + login.Provider) == before && GuardedFile.Read(paths.Credential) == credential
                            && (paths.Profile is null || GuardedFile.Read(paths.Profile) == oldProfile), "A blocked entry changed saved or active credentials.");
                        Require(Inventory().SequenceEqual(inventory), "A blocked entry created sign-in files.");
                        Checked(login.Provider + "-blocked-" + operation);
                    }
                }
                await AccountBusyViewRegression(accounts, vault, root, () => operationEntries, checks);
                await Probe(true);
                await holder.Send("release"); Require(await holder.Exit() == 0, "Raw holder did not release normally.");
            }
            await Probe(false); Checked("normal-release");

            // Real asynchronous suspension checks both the refresh wait and RPC
            // verification lifetime; immediate Task.FromResult alone cannot do so.
            var refresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<JsonElement> Verify(string _, string method, CancellationToken __)
            {
                if (method == "config/read") return Json("""{"config":{}}""");
                Require(method == "account/read", "Unexpected operation RPC."); verifying.SetResult(); return await response.Task;
            }
            var switching = accounts.SwitchAsync(codex, "synthetic-fixture.exe", () => refresh.Task, Verify);
            Mutex? asyncRetained = null;
            Exception? suspendedFailure = null;
            try
            {
                asyncRetained = Mutex.OpenExisting("Global\\" + name);
                Require(!switching.IsCompleted && SavedAccounts.OperationInProgress, "The account operation did not suspend.");
                await Probe(true);
                Exception? reentry = null; try { accounts.SaveCurrent("codex"); } catch (InvalidOperationException error) { reentry = error; }
                Require(reentry?.Message == "An account operation is already in progress.", "Synchronous SaveCurrent bypassed the local reentrancy guard.");
                refresh.SetResult(); await verifying.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Probe(true);
                response.SetResult(Json(JsonSerializer.Serialize(new { account = new { type = "chatgpt", email = codex.Identity.Email } })));
                await switching.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception error) when (error is not OutOfMemoryException) { suspendedFailure = error; throw; }
            finally
            {
                refresh.TrySetResult(); response.TrySetResult(Json("""{"account":null}"""));
                try { await switching; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (suspendedFailure is null) failure ??= error; else cleanup.Add(error);
                }
                finally { asyncRetained?.Dispose(); }
            }
            Require(!SavedAccounts.OperationInProgress, "Completed operation kept its local guard.");
            await Probe(false); Checked("await-verification-reentry-release");

            Task<JsonElement> FailedVerification(string _, string method, CancellationToken __)
                => Task.FromResult(Json(method == "config/read" ? """{"config":{}}""" : """{"account":null}"""));
            var committed = false;
            try { await accounts.SwitchAsync(selectedCodex, "synthetic-fixture.exe", codexRpc: FailedVerification); }
            catch (AccountSwitchCommittedException) { committed = true; }
            Require(committed && GuardedFile.Read(codexPath) == selectedCodex.Credential && !SavedAccounts.OperationInProgress, "Committed Codex failure restored a login or kept the guard.");
            await Probe(false); Checked("codex-committed-error-release");
            GuardedFile.Replace(codexPath, selectedCodex.Credential, codex.Credential);

            var selectedOAuth = JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = "synthetic-b-access", refreshToken = "synthetic-b-refresh",
                expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), scopes = OperationInferenceScopes } });
            var selectedProfile = JsonSerializer.Serialize(new { oauthAccount = new { emailAddress = "lease-b@example.invalid", organizationUuid = "fixture-org", accountUuid = "fixture-claude-b" } });
            var selectedClaude = new SavedLogin("claude", LoginIdentity.Claude(selectedOAuth, selectedProfile), selectedOAuth, selectedProfile);
            vault.Save("accounts:claude", JsonSerializer.Serialize(new[] { claude, selectedClaude }));
            var rolledBack = false;
            try { await accounts.SwitchAsync(selectedClaude, Path.Combine(root, "missing-cli.exe")); }
            catch (FileNotFoundException) { rolledBack = true; }
            Require(rolledBack && GuardedFile.Read(Path.Combine(claudeHome, ".credentials.json")) == oauth
                && GuardedFile.Read(Path.Combine(claudeHome, ".claude.json")) == profile && !SavedAccounts.OperationInProgress,
                "Claude failure did not restore the exact two fixture writes or release local busy.");
            await Probe(false); Checked("claude-rollback-release");

            using (var cancellation = new CancellationTokenSource())
            {
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var canceled = accounts.SwitchAsync(codex, "synthetic-fixture.exe", () => pending.Task.WaitAsync(cancellation.Token), token: cancellation.Token);
                Exception? cancellationFailure = null;
                try
                {
                    Require(!canceled.IsCompleted, "Cancellation fixture did not suspend."); await Probe(true); cancellation.Cancel();
                    var wasCanceled = false; try { await canceled; } catch (OperationCanceledException) { wasCanceled = true; }
                    Require(wasCanceled && !SavedAccounts.OperationInProgress, "Cancellation was swallowed or retained ownership.");
                }
                catch (Exception error) when (error is not OutOfMemoryException) { cancellationFailure = error; throw; }
                finally
                {
                    cancellation.Cancel(); pending.TrySetResult();
                    try { await canceled; }
                    catch (OperationCanceledException) { }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        if (cancellationFailure is null) failure ??= error; else cleanup.Add(error);
                    }
                }
                await Probe(false); Checked("pending-cancel-release");
                var before = vault.Version("accounts:codex"); var invoked = 0;
                var rejected = false;
                try { await accounts.SwitchAsync(codex, "missing-fixture.exe", () => { invoked++; return Task.CompletedTask; }, token: cancellation.Token); }
                catch (OperationCanceledException) { rejected = true; }
                Require(rejected && invoked == 0 && before == vault.Version("accounts:codex") && !SavedAccounts.OperationInProgress, "Pre-canceled entry performed work.");
                await Probe(false); Checked("pre-cancel-release");
            }

            var context = SynchronizationContext.Current; var snapshot = vault.Version("accounts:codex");
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                var rejected = false; try { accounts.Remove("codex", codex.Identity.Id); }
                catch (InvalidOperationException error) when (error.Message == AccountOperationLease.UiThreadMessage) { rejected = true; }
                Require(rejected, "A no-context account entry was accepted.");
            }
            finally { SynchronizationContext.SetSynchronizationContext(context); }
            var workerRejected = await Task.Run(() =>
            {
                try { accounts.SaveCurrent("codex"); return false; }
                catch (InvalidOperationException error) when (error.Message == AccountOperationLease.UiThreadMessage) { return true; }
            });
            Require(workerRejected && snapshot == vault.Version("accounts:codex") && !SavedAccounts.OperationInProgress, "Worker-thread entry changed the vault or retained local busy.");
            await Probe(false); Checked("ui-contract");

            using (var holder = Start("hold"))
            {
                Require(await holder.ReadLine() == "owned", "Abandonment holder did not acquire.");
                using var retained = Mutex.OpenExisting("Global\\" + name); // do not recreate a vanished object
                await holder.Send("abandon"); Require(await holder.Exit() == 0, "Only the owned fixture should exit without release.");
                using (var lease = AccountOperationLease.Acquire(name)) Require(lease.WasAbandoned, "A new object was mistaken for an abandoned mutex.");
            }
            await Probe(false); Checked("abandoned-retained-object-release");

            var incompatible = name + ".event";
            using (var signal = new EventWaitHandle(false, EventResetMode.ManualReset, "Global\\" + incompatible))
            {
                var rejected = false; try { using var lease = AccountOperationLease.Acquire(incompatible); }
                catch (WaitHandleCannotBeOpenedException) { rejected = true; }
                Require(rejected, "An incompatible kernel object allowed an unlocked account operation.");
            }
            await Probe(false); Checked("incompatible-object");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure ??= error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeConfig));
            Restore(() => { if (claudeSaved is null) vault.Delete("accounts:claude"); else vault.Save("accounts:claude", claudeSaved); });
            Restore(() => { if (originalCodexSaved is null) vault.Delete("accounts:codex"); else vault.Save("accounts:codex", originalCodexSaved); });
            Restore(() =>
            {
                var path = Path.Combine(home, "auth.json");
                if (originalCodex is null) File.Delete(path);
                else if (GuardedFile.Read(path) != originalCodex) GuardedFile.Replace(path, GuardedFile.Read(path), originalCodex);
            });
            Restore(() => Directory.Delete(root, true));
        }
        if (cleanup.Count > 0) throw new AggregateException("Operation fixture cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class MutexFixture(Process process, List<Exception> cleanup) : IDisposable
    {
        internal Task<string?> ReadLine() => process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        internal async Task Send(string command) { await process.StandardInput.WriteLineAsync(command); await process.StandardInput.FlushAsync(); }
        internal async Task<int> Exit() { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); return process.ExitCode; }
        public void Dispose()
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); if (!process.WaitForExit(10000)) throw new IOException("Owned mutex fixture did not exit."); } }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            finally
            {
                try { process.Dispose(); }
                catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            }
        }
    }
    private static MutexFixture StartMutexFixture(string executable, string root, string name, string mode, List<Exception> cleanup)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        start.Environment["SYNTHETIC_RUN_DIR"] = root;
        foreach (var argument in new[] { "account-mutex", name, mode }) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new IOException("Raw mutex fixture could not start.");
        try { _ = process.Handle; return new(process, cleanup); } // retain the exact child created by this test
        catch
        {
            new MutexFixture(process, cleanup).Dispose();
            throw;
        }
    }
}
