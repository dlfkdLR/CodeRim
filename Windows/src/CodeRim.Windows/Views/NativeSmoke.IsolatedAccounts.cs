using System.IO;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task IsolatedAccountsRegression(string directory)
    {
        var checks = new List<string>();
        static JsonElement Json(string text) { using var value = JsonDocument.Parse(text); return value.RootElement.Clone(); }
        var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"email":"new@example.invalid","sub":"synthetic-user","https://api.openai.com/auth":{"chatgpt_account_id":"synthetic-org"}}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var codexCredential = JsonSerializer.Serialize(new { auth_mode = "chatgpt", tokens = new { access_token = "synthetic-access", refresh_token = "synthetic-refresh", id_token = "synthetic." + claims + ".signature", account_id = "synthetic-org" } });
        const string claudeCredential = """{"claudeAiOauth":{"accessToken":"synthetic-access","refreshToken":"synthetic-refresh","expiresAt":4102444800000,"scopes":["user:inference"]}}""";
        const string claudeProfile = """{"oauthAccount":{"emailAddress":"new@example.invalid","accountUuid":"synthetic-user","organizationUuid":"synthetic-org"},"unrelated":"never-save"}""";
        foreach (var provider in new[] { "codex", "claude" })
        foreach (var scenario in new[] { "success", "cancel", "failure", "not-isolated", "wrong-account" })
        {
            string? temporary = null; var loggedIn = false; var loginCalls = 0;
            using var cancellation = new CancellationTokenSource();
            string Home(IReadOnlyDictionary<string, string?> environment)
            {
                var value = environment[provider == "codex" ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR"]!;
                temporary ??= value;
                Require(value == temporary && Directory.Exists(value), "Isolated commands changed or lost the temporary home.");
                Require(!environment.ContainsKey("OPENAI_API_KEY") && !environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"), "Ambient authentication entered a temporary sign-in.");
                Require(!Path.GetFullPath(SavedAccounts.Paths(provider).Credential).StartsWith(value + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Add Account reused the current login directory.");
                return value;
            }
            Task<JsonElement> Rpc(string executable, string method, IReadOnlyDictionary<string, string?> environment, CancellationToken token)
            {
                _ = Home(environment); token.ThrowIfCancellationRequested();
                Require(executable == "synthetic.exe" && method == "account/read", "Sign-in made an unexpected RPC.");
                return Task.FromResult(!loggedIn && scenario != "not-isolated" ? Json("{\"account\":null}")
                    : Json(scenario == "wrong-account" ? """{"account":{"type":"chatgpt","email":"wrong@example.invalid"}}"""
                        : """{"account":{"type":"chatgpt","email":"new@example.invalid"}}"""));
            }
            Task<ProcessResult> Command(string executable, string[] arguments, IReadOnlyDictionary<string, string?> environment, CancellationToken token)
            {
                var home = Home(environment); token.ThrowIfCancellationRequested();
                Require(executable == "synthetic.exe", "Unexpected login executable.");
                if (arguments is ["auth", "status"])
                    return Task.FromResult(!loggedIn && scenario != "not-isolated" ? new ProcessResult(1, "{\"loggedIn\":false}", "")
                        : new ProcessResult(0, JsonSerializer.Serialize(new { loggedIn = true, authMethod = "claude.ai", email = scenario == "wrong-account" ? "wrong@example.invalid" : "new@example.invalid", orgId = "synthetic-org" }), ""));
                Require(provider == "codex" ? arguments is ["-c", "cli_auth_credentials_store=\"file\"", "login"] : arguments is ["auth", "login", "--claudeai"], "Unexpected sign-in arguments.");
                loginCalls++;
                if (scenario == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                if (scenario == "failure") return Task.FromResult(new ProcessResult(1, "", "synthetic failure"));
                if (provider == "codex") GuardedFile.WritePrivate(Path.Combine(home, "auth.json"), codexCredential);
                else { GuardedFile.WritePrivate(Path.Combine(home, ".credentials.json"), claudeCredential); GuardedFile.WritePrivate(Path.Combine(home, ".claude.json"), claudeProfile); }
                loggedIn = true; return Task.FromResult(new ProcessResult(0, "", ""));
            }
            SavedLogin? result = null; Exception? failure = null;
            try { result = await IsolatedAccountSignIn.RunAsync(provider, "synthetic.exe", Command, Rpc, cancellation.Token); }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException) { failure = error; }
            Require(temporary is not null && !Directory.Exists(temporary), "Temporary credentials survived completion or failure.");
            if (scenario == "success")
            {
                Require(failure is null && result?.Identity.Email == "new@example.invalid", "The new account was not verified and returned.");
                Require(provider != "claude" || !result!.Profile!.Contains("never-save", StringComparison.Ordinal), "Unrelated Claude settings entered the account vault.");
            }
            else Require(result is null && failure is not null, "An unverified sign-in returned a saved account.");
            if (scenario == "not-isolated") Require(loginCalls == 0, "Login ran despite a non-isolated CLI.");
            if (scenario == "cancel") Require(failure is OperationCanceledException, "Cancellation was swallowed.");
            checks.Add(provider + ": " + scenario);
        }
        File.WriteAllText(Path.Combine(directory, "windows-isolated-accounts.json"), JsonSerializer.Serialize(new { completed = true, realAccount = false, checks }));
    }
}
