using System.IO;
using System.Text;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
                "cancel-after-write", "renewal-then-failure", "renewal-then-cancel",
                "signed-out", "signed-out-verification-failure", "signed-out-renewal-then-failure", "signed-out-cancel-after-write", "signed-out-cancel-before-write" })
            {
                var signedOut = scenario.StartsWith("signed-out", StringComparison.Ordinal);
                var behavior = scenario.StartsWith("signed-out-", StringComparison.Ordinal) ? scenario[11..] : scenario;
                var sameAccount = scenario is "already-active" or "already-active-failure";
                var initial = sameAccount ? fresh : a;
                if (File.Exists(path)) File.Delete(path); if (!signedOut) GuardedFile.WritePrivate(path, initial.Credential);
                var entries = scenario == "removed-row" ? new[] { a } : new[] { a, sameAccount || scenario == "refresh-wait" ? old : fresh };
                vault.Save("accounts:codex", JsonSerializer.Serialize(signedOut ? new[] { fresh } : entries));
                using var cancellation = new CancellationTokenSource(); var policyCalls = 0; var verifyCalls = 0;
                Task<JsonElement> Rpc(string executable, string method, CancellationToken token)
                {
                    Require(executable == "synthetic-account-fixture.exe", "Unexpected switch executable.");
                    token.ThrowIfCancellationRequested();
                    if (method == "config/read")
                    {
                        policyCalls++;
                        if (behavior == "cancel-before-write") cancellation.Cancel();
                        return Task.FromResult(Json(scenario == "managed-policy"
                            ? """{"config":{"forced_chatgpt_workspace_id":"different-workspace"}}""" : """{"config":{}}"""));
                    }
                    Require(method == "account/read", "Unexpected switch RPC."); verifyCalls++;
                    if (behavior is "verification-renews" or "renewal-then-failure" or "renewal-then-cancel") GuardedFile.Replace(path, fresh.Credential, renewed.Credential);
                    if (scenario == "external-login") GuardedFile.Replace(path, fresh.Credential, other.Credential);
                    if (behavior is "cancel-after-write" or "renewal-then-cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                    return Task.FromResult(Json(behavior is "verification-failure" or "already-active-failure" or "renewal-then-failure"
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
                var expected = behavior switch
                {
                    "removed-row" or "managed-policy" or "cancel-before-write" => signedOut ? null : initial.Credential,
                    "verification-renews" or "renewal-then-failure" or "renewal-then-cancel" => renewed.Credential,
                    "external-login" => other.Credential,
                    _ => fresh.Credential
                };
                Require(GuardedFile.ReadIfPresent(path) == expected, "Switch changed the wrong credential generation: " + scenario);
                var shouldFail = behavior is "removed-row" or "verification-failure" or "external-login"
                    or "already-active-failure" or "managed-policy" or "cancel-before-write"
                    or "cancel-after-write" or "renewal-then-failure" or "renewal-then-cancel";
                Require(shouldFail ? error is not null : error is null, "Unexpected account switch result: " + scenario);
                Require(error is AccountSwitchCommittedException == (behavior is "verification-failure" or "external-login"
                        or "cancel-after-write" or "renewal-then-failure" or "renewal-then-cancel"),
                    "Switch misreported whether the Codex credential was committed: " + scenario);
                if (scenario == "removed-row") Require(policyCalls == 0 && verifyCalls == 0, "Removed row launched a CLI probe.");
                if (scenario is "managed-policy" or "cancel-before-write") Require(verifyCalls == 0, "Rejected preflight reached account verification.");
                if (behavior == "cancel-before-write") Require(error is OperationCanceledException, "Pre-commit cancellation was swallowed.");
                if (!shouldFail)
                {
                    Require(accounts.Read("codex").Single(account => account.Identity.Id == fresh.Identity.Id).Credential == expected,
                        "The vault did not retain the latest verified login: " + scenario);
                    Require(signedOut ? accounts.Read("codex").Count == 1 : accounts.Read("codex").Single(account => account.Identity.Id == a.Identity.Id).Credential == a.Credential,
                        "The departing login was lost: " + scenario);
                }
                Require(!SavedAccounts.OperationInProgress, "An account operation remained locked after " + scenario);
                checks.Add(new { scenario, passed = true, policyCalls, verifyCalls, committedFailure = error is AccountSwitchCommittedException });
            }
            await SavedAccountFileRegression(home, checks);
            completed = true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            foreach (var (key, value) in previous) Restore(() => Environment.SetEnvironmentVariable(key, value));
            Restore(() => { if (saved is null) vault.Delete("accounts:codex"); else vault.Save("accounts:codex", saved); });
            // A failed junction assertion must unwind and release its directory
            // lease before cleanup. Preserve both the assertion and cleanup errors.
            Restore(() =>
            {
                var fixtureRoot = Path.Combine(home, "file-cases-한글-😀");
                if (Directory.Exists(fixtureRoot) && (File.GetAttributes(fixtureRoot) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(fixtureRoot);
            });
            Restore(() => { if (Directory.Exists(home)) Directory.Delete(home, true); });
            Restore(() => { if (Directory.Exists(home + "-moved")) Directory.Delete(home + "-moved", true); });
            Restore(() => File.WriteAllText(Path.Combine(directory, "windows-saved-account-switch.json"), JsonSerializer.Serialize(new {
                completed = completed && cleanup.Count == 0, realAccount = false, vault = "Windows current-user DPAPI", cli = "injected read-only RPC fixture",
                cleanupFailures = cleanup.Count, checks }, JsonOptions)));
        }
        if (cleanup.Count > 0) throw new AggregateException("Saved account regression cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static async Task SavedAccountFileRegression(string home, List<object> checks)
    {
        var root = Path.Combine(home, "file-cases-한글-😀"); Directory.CreateDirectory(root);
        var file = Path.Combine(root, "auth.json");
        void Checked(string scenario) => checks.Add(new { scenario, passed = true });
        static void Rejected(Action action, string reason)
        {
            try { action(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            throw new InvalidOperationException(reason);
        }
        Require(GuardedFile.ReadIfPresent(file) is null, "A missing login was not recognized.");
        GuardedFile.CreateIfAbsent(file, "synthetic-login");
        Require(GuardedFile.Read(file) == "synthetic-login", "The new login was not published intact.");
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var acl = System.IO.FileSystemAclExtensions.GetAccessControl(new FileInfo(file));
            Require(acl.AreAccessRulesProtected, "A restored login inherited directory access.");
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                Require(rule.AccessControlType != System.Security.AccessControl.AccessControlType.Allow || rule.IdentityReference == identity.User,
                    "A restored login grants another user access.");
        }
        Checked("signed-out-private-publication");
        Rejected(() => GuardedFile.CreateIfAbsent(file, "must-not-win"), "An existing login was overwritten.");
        Require(GuardedFile.Read(file) == "synthetic-login", "A competing login was lost.");
        Checked("signed-out-existing-login-wins"); File.Delete(file);
        var missingParent = Path.Combine(root, "missing", "auth.json");
        Rejected(() => GuardedFile.ReadIfPresent(missingParent), "A missing parent was treated as signed out.");
        Rejected(() => GuardedFile.CreateIfAbsent(missingParent, "private"), "An unverified directory was created.");
        Require(!Directory.Exists(Path.GetDirectoryName(missingParent)), "A missing login directory was created.");
        Checked("signed-out-missing-parent-rejected");
        Directory.CreateDirectory(file);
        Rejected(() => GuardedFile.ReadIfPresent(file), "A directory was treated as a missing login.");
        Rejected(() => GuardedFile.CreateIfAbsent(file, "private"), "A directory was replaced by a login.");
        Directory.Delete(file); Checked("signed-out-directory-leaf-rejected");
        GuardedFile.WritePrivate(file, "unreadable");
        using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Rejected(() => GuardedFile.ReadIfPresent(file), "An inaccessible login was treated as absent.");
            Rejected(() => GuardedFile.CreateIfAbsent(file, "private"), "An inaccessible login was replaced.");
        }
        Require(GuardedFile.Read(file) == "unreadable", "An unreadable login was modified.");
        File.Delete(file); Checked("signed-out-unreadable-login-preserved");
        using (var start = new ManualResetEventSlim(false))
        {
            var writers = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            {
                start.Wait();
                try { GuardedFile.CreateIfAbsent(file, "writer-" + index); return index; }
                catch (IOException) { return -1; }
            })).ToArray();
            start.Set(); var results = await Task.WhenAll(writers);
            Require(results.Count(index => index >= 0) == 1, "Concurrent login publishers did not have exactly one winner.");
            Require(GuardedFile.Read(file) == "writer-" + results.Single(index => index >= 0), "A concurrent login was truncated or overwritten.");
        }
        Require(!Directory.EnumerateFiles(root, "*.tmp").Any(), "A private staging file leaked.");
        File.Delete(file); Checked("signed-out-concurrent-publication");
        // Negative control: the old metadata-only access really permits rename.
        // Keep a real open handle during both moves, so a passing protection test
        // cannot be explained by a directory which was already immovable.
        using (var metadata = SavedAccountJunction.OpenMetadataOnly(root))
        {
            Directory.Move(root, root + "-metadata");
            Directory.Move(root + "-metadata", root);
        }
        Checked("signed-out-metadata-only-rename-control");
        // Probe the production lease without expanding Core's public API for QA.
        var leaseType = typeof(GuardedFile).Assembly.GetType("CodeRim.Core.Services.LoginDirectoryLease", throwOnError: true)!;
        var acquire = leaseType.GetMethod("Acquire", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        using ((IDisposable)acquire.Invoke(null, [file])!)
        {
            Rejected(() => Directory.Move(root, root + "-moved"), "The login directory was moved while pinned.");
            Rejected(() => Directory.Move(home, home + "-moved"), "A login ancestor was moved while pinned.");
            var redirect = Path.Combine(home, "junction-redirection-target"); Directory.CreateDirectory(redirect);
            Rejected(() => SavedAccountJunction.CreateJunction(root, redirect), "The pinned login directory became a junction.");
            Require((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "The login directory was redirected.");
            Require(!Directory.EnumerateFileSystemEntries(redirect).Any(), "A credential escaped the pinned directory.");
            GuardedFile.CreateIfAbsent(file, "pinned-private");
        }
        Require(GuardedFile.Read(file) == "pinned-private", "Pinned directory publication failed.");
        File.Delete(file); Directory.Move(root, root + "-moved"); Directory.Move(root + "-moved", root);
        Checked("signed-out-directory-lease-and-release");
        var link = Path.Combine(home, "file-junction");
        try
        {
            SavedAccountJunction.CreateJunction(link, root);
            Rejected(() => GuardedFile.ReadIfPresent(Path.Combine(link, "absent.json")), "A linked parent was treated as signed out.");
            Rejected(() => GuardedFile.CreateIfAbsent(Path.Combine(link, "absent.json"), "private"), "A credential was created through a linked parent.");
            Rejected(() => GuardedFile.ReadIfPresent(link), "A linked leaf was treated as signed out.");
            Rejected(() => GuardedFile.CreateIfAbsent(link, "private"), "A linked leaf was replaced.");
            Require(!File.Exists(Path.Combine(root, "absent.json")), "A credential escaped into the link target.");
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
        Checked("signed-out-junction-rejection");

    }

    private static class SavedAccountJunction
    {
    internal static SafeFileHandle OpenMetadataOnly(string path)
    {
        var handle = CreateFileW(path, 0x80, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var code = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new IOException("Fixture metadata handle failed", new Win32Exception(code));
    }
    internal static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        using var handle = CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Fixture junction failed", new Win32Exception(Marshal.GetLastWin32Error()));
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var data = new byte[16 + substitute.Length + 2 + display.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), checked((ushort)display.Length));
        substitute.CopyTo(data, 16); display.CopyTo(data, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Fixture junction failed", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int length, IntPtr output, int outputLength, out int returned, IntPtr overlapped);

    }

}
