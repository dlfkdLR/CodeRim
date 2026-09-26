using System.IO;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal sealed partial class ProviderConnections
{
    // Shared with FetchAsync so display never falls back to a local identity
    // when a manually saved or environment credential owns the request.
    private string? ConfiguredCredential(string id)
    {
        var secret = vault.Load("provider:" + id);
        if (id == "groq" && secret is null) secret = NativeProviders.GroqEnvironmentCredential(Environment.GetEnvironmentVariable);
        if (secret is null && ProviderCatalog.Find(id) is { } definition)
        {
            var keys = NativeProviders.CredentialKeys(id) ?? (id == "copilot" ? ["GH_TOKEN", "GITHUB_TOKEN"] : definition.EnvironmentKeys.Where(k => k.EndsWith("KEY", StringComparison.Ordinal) || k.EndsWith("TOKEN", StringComparison.Ordinal) || k.EndsWith("COOKIE", StringComparison.Ordinal)).ToArray());
            foreach (var key in keys)
                if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) { secret = value; break; }
        }
        return secret;
    }

    internal NativeAccountSummary? ReadAccountSummary(string id)
    {
        if (!NativeAccountSummary.Supports(id)) return null;
        try
        {
            if (id == "glm")
            {
                // Use exactly the script connector's effective key selection.
                // An explicit key suppresses local tool metadata and its region.
                return EffectiveSetting(vault, id, "Z_AI_API_KEY") is { } key
                    ? NativeAccountSummary.Glm(new(key, EffectiveSetting(vault, id, "Z_AI_REGION") ?? "global", "api"))
                    : NativeAccountSummary.Glm(GlmAuthentication.Profile(readCredential(id)));
            }
            if (id == "copilot")
            {
                // Match the request's trimmed saved/GH_TOKEN/GITHUB_TOKEN
                // precedence, including blank aliases. Never borrow a CLI label.
                var selected = GitHubAuthentication.Configured(vault.Load("provider:copilot"),
                    Environment.GetEnvironmentVariable("GH_TOKEN"), Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
                return selected is not null ? NativeAccountSummary.FromCredential(id, selected) : readNativeSummary(id);
            }
            // Of these providers only Cursor supports a browser import.
            // Validate and fingerprint the same jar used by the request path.
            if (BrowserConnections.Domains(id).Length > 0 && BrowserConnections.Load(id, vault) is { } browser)
                return NativeAccountSummary.FromCredential(id, browser.Serialize(), "browser");
            if (ConfiguredCredential(id) is { } configured)
                return NativeAccountSummary.FromCredential(id, configured);
            return readNativeSummary(id);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or System.Security.Cryptography.CryptographicException or ArgumentException) { return null; }
    }
}
