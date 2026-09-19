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
    private readonly NativeProviders native = new();
    public ProviderConnections(CredentialVault vault) { this.vault = vault; }
    public void Dispose() { http.Dispose(); scripts.Dispose(); native.Dispose(); }
    public async Task<ProviderReading> FetchAsync(string id, AppSettings settings, CancellationToken token)
    {
        try
        {
            if (id == "jetbrains") return JetBrainsQuota.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetBrains"));
            if (id == "codex")
            {
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
            if (ScriptProviders.Catalog.ContainsKey(id))
            {
                var definitionSettings = ScriptProviders.Catalog[id].Settings.ToDictionary(x => x.Key,
                    x => vault.Load("setting:" + id + ":" + x.Key) ?? Environment.GetEnvironmentVariable(x.Key), StringComparer.Ordinal);
                return await scripts.FetchAsync(id, key => definitionSettings.GetValueOrDefault(key), vault.Load("cookie:" + id), token).ConfigureAwait(false);
            }
            var definition = ProviderCatalog.Find(id);
            var secret = vault.Load("provider:" + id);
            if (secret is null && definition is not null)
            {
                var keys = NativeProviders.CredentialKeys(id) ?? (id == "copilot" ? ["GH_TOKEN", "GITHUB_TOKEN"] : definition.EnvironmentKeys.Where(k => k.EndsWith("KEY", StringComparison.Ordinal) || k.EndsWith("TOKEN", StringComparison.Ordinal) || k.EndsWith("COOKIE", StringComparison.Ordinal)).ToArray());
                foreach (var key in keys)
                    if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) { secret = value; break; }
            }
            if (NativeProviders.Supported.Contains(id))
                return await native.FetchAsync(id, secret ?? NativeCredentials.Read(id), key => vault.Load("setting:" + id + ":" + key) ?? Environment.GetEnvironmentVariable(key), token).ConfigureAwait(false);
            return await http.FetchAsync(id, secret, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return new(id, ReadingState.Error, [], Message: "Unable to read the provider. Check its connection and refresh.");
        }
    }
    internal string? Scope(string id)
    {
        try
        {
            if (id == "jetbrains") return null;
            if (id is "codex" or "claude") return SavedAccounts.Current(id).Identity.Id;
            var definition = ProviderCatalog.Find(id);
            var values = new List<string?> { vault.Load("provider:" + id), vault.Load("cookie:" + id), NativeCredentials.Read(id) };
            if (definition is not null) values.AddRange(definition.EnvironmentKeys.Select(Environment.GetEnvironmentVariable));
            if (ScriptProviders.Catalog.TryGetValue(id, out var script))
                values.AddRange(script.Settings.Select(x => vault.Load("setting:" + id + ":" + x.Key) ?? Environment.GetEnvironmentVariable(x.Key)));
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
