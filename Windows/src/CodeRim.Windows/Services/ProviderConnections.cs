using System.IO;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal sealed class ProviderConnections : IDisposable
{
    private readonly CredentialVault vault;
    private readonly HttpProviders http = new();
    private readonly ScriptProviders scripts = new();
    private readonly NativeProviders native;
    public ProviderConnections(CredentialVault vault, NativeProviders? native = null) { this.vault = vault; this.native = native ?? new(); }
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
            var browser = browserOverride ?? (BrowserConnections.Domains(id).Length > 0 ? BrowserConnections.Load(id, vault) : null);
            string? BrowserCookie(Uri uri) => browser?.Header(uri, DateTimeOffset.UtcNow);
            if (ScriptProviders.Catalog.ContainsKey(id))
            {
                var definitionSettings = ScriptProviders.Catalog[id].Settings.ToDictionary(x => x.Key,
                    x => vault.Load("setting:" + id + ":" + x.Key) ?? Environment.GetEnvironmentVariable(x.Key), StringComparer.Ordinal);
                return await scripts.FetchAsync(id, key => definitionSettings.GetValueOrDefault(key), vault.Load("cookie:" + id), browser is null ? null : BrowserCookie, token).ConfigureAwait(false);
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
            string? NativeSetting(string key) => vault.Load("setting:" + id + ":" + key)?.Trim() is { Length: > 0 } configured ? configured : Environment.GetEnvironmentVariable(key);
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
            if (id == "amp")
            {
                var source = AmpCliUsage.Source(NativeSetting("AMP_USAGE_SOURCE"));
                if (source is null) return new(id, ReadingState.Error, [], Message: "Choose API or CLI as the Amp usage source.");
                if (source == "cli")
                {
                    var executable = NativeSetting("AMP_EXECUTABLE") ?? ResolveExecutable("amp.exe");
                    if (executable is null) return new(id, ReadingState.NeedsAuth, [], Message: "Install Amp, sign in with amp login, and select amp.exe if it is outside PATH.");
                    return await AmpCliUsage.ReadAsync(executable, token).ConfigureAwait(false);
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
                return await native.FetchAsync(id, browser is null ? secret ?? NativeCredentials.Read(id) : null, NativeSetting, browser is null ? null : BrowserCookie, token).ConfigureAwait(false);
            return await http.FetchAsync(id, secret, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return new(id, ReadingState.Error, [], Message: "Unable to read the provider. Check its connection and refresh.");
        }
    }
    internal bool CanCache(string id)
    {
        if (id == "jetbrains") return false;
        if (id == "windsurf") return WindsurfLocalUsage.Source(vault.Load("setting:windsurf:WINDSURF_USAGE_SOURCE") ?? Environment.GetEnvironmentVariable("WINDSURF_USAGE_SOURCE")) == "web";
        if (id == "amp") return AmpCliUsage.Source(vault.Load("setting:amp:AMP_USAGE_SOURCE") ?? Environment.GetEnvironmentVariable("AMP_USAGE_SOURCE")) == "api";
        if (id != "bedrock") return true;
        var secret = vault.Load("provider:" + id) ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        return !BedrockAuthentication.UseProfile(secret, key => vault.Load("setting:" + id + ":" + key)?.Trim() is { Length: > 0 } value ? value : Environment.GetEnvironmentVariable(key));
    }
    internal string? Scope(string id)
    {
        try
        {
            if (id == "jetbrains" || id is "amp" or "windsurf" && !CanCache(id))
            {
                // A source marker keeps the current UI reading between quota polls.
                // It is never an account identity and CanCache=false prevents restore/retention.
                var modeKey = id == "windsurf" ? "WINDSURF_USAGE_SOURCE" : "AMP_USAGE_SOURCE";
                var pathKey = id == "windsurf" ? "WINDSURF_CACHE_PATH" : "AMP_EXECUTABLE";
                var source = JsonSerializer.Serialize(new { id, process = Environment.ProcessId,
                    mode = id == "jetbrains" ? null : vault.Load("setting:" + id + ":" + modeKey) ?? Environment.GetEnvironmentVariable(modeKey),
                    path = id == "jetbrains" ? null : vault.Load("setting:" + id + ":" + pathKey) ?? Environment.GetEnvironmentVariable(pathKey) });
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
            }
            if (id is "codex" or "claude") return SavedAccounts.Current(id).Identity.Id;
            var definition = ProviderCatalog.Find(id);
            var values = new List<string?> { vault.Load("browser:" + id), vault.Load("provider:" + id), vault.Load("cookie:" + id), NativeCredentials.Read(id) };
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
            values.AddRange(NativeProviders.ScopeAliases(id).Select(x => vault.Load("setting:" + id + ":" + x) ?? Environment.GetEnvironmentVariable(x)));
            values.AddRange(NativeProviders.Settings(id).Select(x => vault.Load("setting:" + id + ":" + x.Key) ?? Environment.GetEnvironmentVariable(x.Key)));
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
