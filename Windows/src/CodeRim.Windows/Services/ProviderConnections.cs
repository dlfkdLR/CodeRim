using System.IO;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal sealed partial class ProviderConnections : IDisposable
{
    private readonly CredentialVault vault;
    private readonly HttpProviders http;
    private readonly ScriptProviders scripts;
    private readonly NativeProviders native;
    private readonly Func<string, string?> readCredential;
    private readonly Func<CancellationToken, Task<string?>> readCopilotCli;
    private readonly Func<string, string, CancellationToken, Task<ProviderReading>> readAlibabaCli;
    public ProviderConnections(CredentialVault vault, NativeProviders? native = null, HttpProviders? http = null, Func<string, string?>? nativeCredentialReader = null, ScriptProviders? scripts = null, Func<CancellationToken, Task<string?>>? copilotCliReader = null, Func<string, string, CancellationToken, Task<ProviderReading>>? alibabaCliReader = null)
    { this.vault = vault; this.native = native ?? new(); this.http = http ?? new(); this.scripts = scripts ?? new(); readCredential = nativeCredentialReader ?? NativeCredentials.Read; readCopilotCli = copilotCliReader ?? CopilotConnection.ReadCliAsync; readAlibabaCli = alibabaCliReader ?? AlibabaTokenPlanCliUsage.ReadAsync; }
    public void Dispose() { http.Dispose(); scripts.Dispose(); native.Dispose(); }
    public Task<ProviderReading> FetchAsync(string id, AppSettings settings, CancellationToken token)
        => FetchAsync(id, settings, null, token);
    internal Task<ProviderReading> VerifyBrowserAsync(string id, AppSettings settings, BrowserCookieJar browser, CancellationToken token = default)
        => FetchAsync(id, settings, browser, token);
    private async Task<ProviderReading> FetchAsync(string id, AppSettings settings, BrowserCookieJar? browserOverride, CancellationToken token)
    {
        try
        {
            if (id == "jetbrains") return JetBrainsQuota.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetBrains"));
            if (id == "codex")
            {
                if (!settings.AccountLimitsEnabled) return new(id, ReadingState.Disabled, [], Message: "Account limits are turned off in Settings.");
                var executable = settings.CodexExecutable ?? ResolveCodex();
                if (executable is null) return new(id, ReadingState.NeedsAuth, [], Message: "Install Codex and sign in, or select codex.exe in Settings.");
                var result = await AppServerClient.ReadAsync(executable, "account/rateLimits/read", token).ConfigureAwait(false);
                var windows = ProviderParsers.Codex(result);
                return new(id, windows.Count > 0 ? ReadingState.Ready : ReadingState.Unavailable, windows, DateTimeOffset.Now,
                    windows.Count > 0 ? null : "No account limit was returned.");
            }
            if (id == "claude")
            {
                var path = Path.Combine(CompanionFile.DataDirectory, "claude-limits.json");
                if (!File.Exists(path)) return new(id, ReadingState.NeedsAuth, [], Message: "Connect the Claude status line in Settings to read plan limits.");
                if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException();
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
                var root = document.RootElement;
                var scope = LoginIdentity.CurrentClaudeScope();
                if (scope is null || ProviderParsers.Text(root, "accountScope") != scope)
                    return new(id, ReadingState.NeedsAuth, [], Message: "Reconnect the Claude status line and run a new session for the current account.");
                var updated = ProviderParsers.Date(ProviderParsers.Get(root, "updatedAt"));
                var windows = ProviderParsers.Claude(root);
                return new ProviderReading(id, windows.Count > 0 ? ReadingState.Ready : ReadingState.Unavailable, windows, updated).Evaluated(DateTimeOffset.Now);
            }
            string? NativeSetting(string key) => EffectiveSetting(vault, id, key);
            if (id == "copilot")
            {
                async Task<string?> Resolve() => GitHubAuthentication.Configured(vault.Load("provider:copilot"),
                    Environment.GetEnvironmentVariable("GH_TOKEN"), Environment.GetEnvironmentVariable("GITHUB_TOKEN"))
                    ?? readCredential("copilot") ?? await readCopilotCli(token).ConfigureAwait(false);
                var selected = await Resolve().ConfigureAwait(false);
                var reading = await http.FetchAsync(id, selected, token).ConfigureAwait(false);
                return selected == await Resolve().ConfigureAwait(false) ? reading
                    : new(id, ReadingState.Unavailable, [], Message: "The GitHub account changed. Refresh the selected account.");
            }
            if (id == "codebuff")
            {
                CodebuffCredential? Resolve() => CodebuffAuthentication.Resolve(vault.Load("provider:codebuff"),
                    Environment.GetEnvironmentVariable("CODEBUFF_API_KEY"), () => readCredential("codebuff"));
                var selected = Resolve();
                var reading = await native.FetchCodebuffAsync(selected?.Token, selected?.FromAuthFile == true, token).ConfigureAwait(false);
                return selected == Resolve() ? reading : new(id, ReadingState.Unavailable, [], Message: "The connection changed. Refresh the selected account.");
            }
            if (id == "alibaba") return await FetchAlibabaCodingConnectionAsync(browserOverride, token).ConfigureAwait(false);
            if (id == "alibabatokenplan") return await FetchAlibabaConnectionAsync(browserOverride, token).ConfigureAwait(false);
            if (id == "stepfun") return await FetchStepFunConnectionAsync(browserOverride, token).ConfigureAwait(false);
            if (id == "minimax") return await FetchMiniMaxConnectionAsync(browserOverride, token).ConfigureAwait(false);
            if (id == "kimi") return await FetchKimiConnectionAsync(browserOverride, token).ConfigureAwait(false);
            if (id == "deepseek")
            {
                DeepSeekCredential? Resolve() => DeepSeekAuthentication.Resolve(NativeSetting("DEEPSEEK_USAGE_SOURCE"), vault.Load, Environment.GetEnvironmentVariable);
                var selected = Resolve();
                var details = DeepSeekUsageDetails.Enabled(NativeSetting("DEEPSEEK_DETAILED_USAGE"));
                if (selected is null) return new(id, ReadingState.Error, [], Message: "Choose Auto, API or Web as the DeepSeek source.");
                var reading = await http.FetchAsync(id, selected.Token,
                    key => key == "DEEPSEEK_USAGE_SOURCE" ? selected.Source : key == "DEEPSEEK_DETAILED_USAGE" ? details.ToString() : null, token).ConfigureAwait(false);
                return selected == Resolve() && details == DeepSeekUsageDetails.Enabled(NativeSetting("DEEPSEEK_DETAILED_USAGE")) ? reading
                    : new(id, ReadingState.Unavailable, [], Message: "The DeepSeek connection changed. Refresh the selected account.");
            }
            if (id == "moonshot")
            {
                var region = MoonshotAuthentication.Region(NativeSetting("MOONSHOT_REGION"));
                if (region is null) return new(id, ReadingState.Error, [], Message: "Choose International or China as the Moonshot region.");
                return await http.FetchAsync(id, MoonshotAuthentication.Credential(region, vault.Load, Environment.GetEnvironmentVariable),
                    key => key == "MOONSHOT_REGION" ? region : NativeSetting(key), token).ConfigureAwait(false);
            }
            if (id == "gemini")
            {
                var source = AntigravityLocalUsage.Source(NativeSetting("ANTIGRAVITY_USAGE_SOURCE"));
                if (source is null) return new(id, ReadingState.Error, [], Message: "Choose OAuth or Local IDE as the Antigravity usage source.");
                if (source == "local") return await AntigravityLocalConnection.FetchAsync(token).ConfigureAwait(false);
            }
            var ampSource = id == "amp" ? AmpCliUsage.Source(NativeSetting("AMP_USAGE_SOURCE")) : null;
            var browser = browserOverride ?? (BrowserConnections.Domains(id).Length > 0 && (id != "amp" || ampSource == "web") ? BrowserConnections.Load(id, vault) : null);
            string? BrowserCookie(Uri uri) => browser?.Header(uri, DateTimeOffset.UtcNow);
            if (id == "amp")
            {
                // Verify the selected browser profile independently of the active source.
                if (browserOverride is not null) return await native.FetchAmpBrowserAsync(null, BrowserCookie, token).ConfigureAwait(false);
                if (ampSource is null) return new(id, ReadingState.Error, [], Message: "Choose API, CLI, or Web as the Amp usage source.");
                if (ampSource == "cli")
                {
                    var executable = NativeSetting("AMP_EXECUTABLE") ?? ResolveExecutable("amp.exe");
                    if (executable is null) return new(id, ReadingState.NeedsAuth, [], Message: "Install Amp, sign in with amp login, and select amp.exe if it is outside PATH.");
                    return await AmpCliUsage.ReadAsync(executable, token).ConfigureAwait(false);
                }
                if (ampSource == "web")
                    return await native.FetchAmpBrowserAsync(vault.Load("cookie:amp") ?? Environment.GetEnvironmentVariable("AMP_COOKIE")
                        ?? Environment.GetEnvironmentVariable("AMP_COOKIE_HEADER"), browser is null ? null : BrowserCookie, token).ConfigureAwait(false);
                return await native.FetchAsync(id, vault.Load("provider:amp") ?? Environment.GetEnvironmentVariable("AMP_API_KEY"), NativeSetting, token).ConfigureAwait(false);
            }
            if (ScriptProviders.Catalog.ContainsKey(id))
            {
                var definitionSettings = ScriptProviders.Catalog[id].Settings.ToDictionary(x => x.Key,
                    x => EffectiveSetting(vault, id, x.Key), StringComparer.Ordinal);
                string? glmProfile = null;
                if (id == "glm" && definitionSettings.GetValueOrDefault("Z_AI_API_KEY") is { } explicitGlm
                    && GlmAuthentication.Clean(explicitGlm) is null)
                    return new(id, ReadingState.NeedsAuth, [], Message: "Update the GLM API key.");
                if (id == "glm" && definitionSettings.GetValueOrDefault("Z_AI_API_KEY") is null)
                {
                    glmProfile = readCredential("glm");
                    if (GlmAuthentication.Profile(glmProfile) is { } local)
                    {
                        definitionSettings["Z_AI_API_KEY"] = local.Token;
                        definitionSettings["Z_AI_REGION"] = local.Region;
                        // Borrowed tool keys are only sent to their fixed vendor console.
                        definitionSettings["Z_AI_QUOTA_ENDPOINT"] = null;
                        definitionSettings["Z_AI_MODEL_USAGE_ENDPOINT"] = null;
                        definitionSettings["Z_AI_BALANCE_ENDPOINT"] = null;
                    }
                }
                var reading = await scripts.FetchAsync(id, key => definitionSettings.GetValueOrDefault(key), vault.Load("cookie:" + id), browser is null ? null : BrowserCookie, token).ConfigureAwait(false);
                return glmProfile is null || glmProfile == readCredential("glm") ? reading
                    : new(id, ReadingState.Unavailable, [], Message: "The connection changed. Refresh the selected account.");
            }
            var definition = ProviderCatalog.Find(id);
            var secret = vault.Load("provider:" + id);
            if (id == "groq" && secret is null) secret = NativeProviders.GroqEnvironmentCredential(Environment.GetEnvironmentVariable);
            if (secret is null && definition is not null)
            {
                var keys = NativeProviders.CredentialKeys(id) ?? (id == "copilot" ? ["GH_TOKEN", "GITHUB_TOKEN"] : definition.EnvironmentKeys.Where(k => k.EndsWith("KEY", StringComparison.Ordinal) || k.EndsWith("TOKEN", StringComparison.Ordinal) || k.EndsWith("COOKIE", StringComparison.Ordinal)).ToArray());
                foreach (var key in keys)
                    if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) { secret = value; break; }
            }
            if (id == "factory" && browser is null && secret?.TrimStart().StartsWith('{') == true && vault.LoadVersioned("provider:factory") is { })
            {
                // Serialize network refreshes separately from short storage commits. A user
                // can replace/remove the account while a refresh is pending; CAS then fails.
                using var refreshLease = await vault.AcquireRefreshAsync("factory", token).ConfigureAwait(false);
                var saved = vault.LoadVersioned("provider:factory");
                if (saved is null || saved.Value != secret)
                    return new(id, ReadingState.Unavailable, [], Message: "The connection changed. Refresh the selected account.");
                return await native.FetchFactorySessionAsync(saved.Value, NativeSetting,
                    updated => vault.SaveIfUnchanged("provider:factory", saved.Version, updated), token).ConfigureAwait(false);
            }
            if (id == "windsurf")
            {
                var source = WindsurfLocalUsage.Source(NativeSetting("WINDSURF_USAGE_SOURCE"));
                if (source is null) return new(id, ReadingState.Error, [], Message: "Choose Web or Local as the Windsurf usage source.");
                if (source == "local")
                {
                    var path = NativeSetting("WINDSURF_CACHE_PATH");
                    if (string.IsNullOrWhiteSpace(path)) return new(id, ReadingState.Unavailable, [], Message: "Select Windsurf's local state.vscdb file in Settings.");
                    return await Task.Run(() => WindsurfLocalUsage.Read(path), token).ConfigureAwait(false);
                }
            }
            if (id == "bedrock" && BedrockAuthentication.UseProfile(secret, NativeSetting))
            {
                var aws = ResolveExecutable("aws.exe");
                if (aws is null)
                {
                    var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon", "AWSCLIV2", "aws.exe");
                    if (File.Exists(installed)) aws = installed;
                }
                if (aws is null) return new(id, ReadingState.NeedsAuth, [], Message: "Install AWS CLI v2 and sign in to your profile, or enter an AWS key.");
                try
                {
                    var profile = await BedrockAuthentication.ResolveAsync(NativeSetting, arguments => BoundedProcess.RunAsync(aws, arguments,
                        timeout: TimeSpan.FromSeconds(20), maximumBytes: 262144, cancellationToken: token,
                        environment: new Dictionary<string, string?> { ["AWS_PROFILE"] = null, ["AWS_DEFAULT_PROFILE"] = null, ["AWS_PAGER"] = "", ["AWS_CLI_AUTO_PROMPT"] = "off" })).ConfigureAwait(false);
                    return await native.FetchAsync(id, profile.Credential, key => key == "AWS_REGION" ? profile.Region : NativeSetting(key), token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or System.ComponentModel.Win32Exception)
                { return new(id, ReadingState.NeedsAuth, [], Message: "AWS profile could not be loaded. Check the profile name and sign in to AWS CLI again."); }
            }
            if (NativeProviders.Supported.Contains(id))
                return await native.FetchAsync(id, browser is null ? secret ?? readCredential(id) : null, NativeSetting, browser is null ? null : BrowserCookie, token).ConfigureAwait(false);
            return await http.FetchAsync(id, secret, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return new(id, ReadingState.Error, [], Message: "Unable to read the provider. Check its connection and refresh.");
        }
    }
    internal static string? EffectiveSetting(CredentialVault vault, string id, string key)
        => vault.Load("setting:" + id + ":" + key)?.Trim() is { Length: > 0 } saved ? saved
            : Environment.GetEnvironmentVariable(key)?.Trim() is { Length: > 0 } environment ? environment : null;
    internal bool CanCache(string id)
    {
        if (id == "alibabatokenplan") return AlibabaSource(vault) == "web";
        if (id == "jetbrains") return false;
        if (id == "copilot") return GitHubAuthentication.Configured(vault.Load("provider:copilot"),
            Environment.GetEnvironmentVariable("GH_TOKEN"), Environment.GetEnvironmentVariable("GITHUB_TOKEN")) is not null || readCredential("copilot") is not null;
        if (id == "gemini") return AntigravityLocalUsage.Source(EffectiveSetting(vault, id, "ANTIGRAVITY_USAGE_SOURCE")) == "oauth";
        if (id == "windsurf") return WindsurfLocalUsage.Source(EffectiveSetting(vault, "windsurf", "WINDSURF_USAGE_SOURCE")) == "web";
        if (id == "amp") return AmpCliUsage.Source(EffectiveSetting(vault, "amp", "AMP_USAGE_SOURCE")) is "api" or "web";
        if (id != "bedrock") return true;
        var secret = vault.Load("provider:" + id) ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        return !BedrockAuthentication.UseProfile(secret, key => EffectiveSetting(vault, id, key));
    }
    internal string? Scope(string id)
    {
        try
        {
            if (id == "copilot" && !CanCache(id))
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { id, process = Environment.ProcessId, config = CopilotConnection.Directory, hosts = CopilotConnection.ScopeMarker() }))));
            if (id == "jetbrains" || id is "amp" or "windsurf" or "gemini" && !CanCache(id))
            {
                // A source marker keeps the current UI reading between quota polls.
                // It is never an account identity and CanCache=false prevents restore/retention.
                var modeKey = id == "gemini" ? "ANTIGRAVITY_USAGE_SOURCE" : id == "windsurf" ? "WINDSURF_USAGE_SOURCE" : "AMP_USAGE_SOURCE";
                var pathKey = id == "windsurf" ? "WINDSURF_CACHE_PATH" : "AMP_EXECUTABLE";
                var source = JsonSerializer.Serialize(new { id, process = Environment.ProcessId,
                    mode = id == "jetbrains" ? null : EffectiveSetting(vault, id, modeKey),
                    path = id is "jetbrains" or "gemini" ? null : EffectiveSetting(vault, id, pathKey) });
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
            }
            if (id is "codex" or "claude") return SavedAccounts.Current(id).Identity.Id;
            if (id == "alibaba") return AlibabaCodingScope();
            if (id == "alibabatokenplan") return AlibabaScope();
            if (id == "stepfun") return ResolveStepFun().Scope;
            if (id == "minimax") return MiniMaxScope();
            if (id == "kimi")
            {
                var selected = ResolveKimi();
                return selected?.Token is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(selected))));
            }
            if (id == "deepseek")
            {
                var credential = DeepSeekAuthentication.Resolve(EffectiveSetting(vault, id, "DEEPSEEK_USAGE_SOURCE"), vault.Load, Environment.GetEnvironmentVariable);
                return credential?.Token is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { credential,
                        details = DeepSeekUsageDetails.Enabled(EffectiveSetting(vault, id, "DEEPSEEK_DETAILED_USAGE")) }))));
            }
            var definition = ProviderCatalog.Find(id);
            var values = new List<string?> { vault.Load("browser:" + id), vault.Load("provider:" + id), vault.Load("cookie:" + id), readCredential(id) };
            if (id == "moonshot") values.AddRange(new[] { vault.Load("provider:moonshot:international"), vault.Load("provider:moonshot:china") });
            if (id == "bedrock" && !CanCache(id))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var path in new[] { Environment.GetEnvironmentVariable("AWS_CONFIG_FILE") ?? Path.Combine(home, ".aws", "config"),
                    Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE") ?? Path.Combine(home, ".aws", "credentials") })
                {
                    try { values.Add(GuardedFile.Read(path)); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { values.Add("profile-file-unavailable"); }
                }
            }
            if (definition is not null) values.AddRange(definition.EnvironmentKeys.Select(Environment.GetEnvironmentVariable));
            if (NativeProviders.CredentialKeys(id) is { } credentialKeys) values.AddRange(credentialKeys.Select(Environment.GetEnvironmentVariable));
            if (ScriptProviders.Catalog.TryGetValue(id, out var script))
                values.AddRange(script.Settings.Select(x => vault.Load("setting:" + id + ":" + x.Key) ?? Environment.GetEnvironmentVariable(x.Key)));
            values.AddRange(NativeProviders.ScopeAliases(id).Select(x => EffectiveSetting(vault, id, x)));
            values.AddRange(NativeProviders.Settings(id).Select(x => EffectiveSetting(vault, id, x.Key)));
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException or System.Security.Cryptography.CryptographicException)
        { return null; }
    }
    public static string? ResolveExecutable(string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat([Path.Combine(home, ".local", "bin"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")]);
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory.Trim('"'))) continue;
            var executable = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(executable)) return executable;
        }
        return null;
    }
    public static string? ResolveCodex()
    {
        if (ResolveExecutable("codex.exe") is { } path) return path;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "codex.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3,
            IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }
}
