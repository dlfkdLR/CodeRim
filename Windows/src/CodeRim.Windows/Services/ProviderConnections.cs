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
    public ProviderConnections(CredentialVault vault) { this.vault = vault; }
    public void Dispose() { http.Dispose(); scripts.Dispose(); }
    public async Task<ProviderReading> FetchAsync(string id, AppSettings settings, CancellationToken token)
    {
        try
        {
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
                var keys = id == "copilot" ? new[] { "GH_TOKEN", "GITHUB_TOKEN" } : definition.EnvironmentKeys;
                foreach (var key in keys.Where(k => k.EndsWith("KEY", StringComparison.Ordinal) || k.EndsWith("TOKEN", StringComparison.Ordinal)))
                    if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) { secret = value; break; }
            }
            return await http.FetchAsync(id, secret, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return new(id, ReadingState.Error, [], Message: "Unable to read the provider. Check its connection and refresh.");
        }
    }
    public static string? ResolveCodex()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "codex.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3,
            IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }
}
