using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Services;

/// Account sources available to the Windows migration candidate. This describes
/// the source before any provider connector, Settings page or scope cache runs.
public static class OwnedProviderAccess
{
#if CODERIM_RUST_ACCOUNTING
    public static bool Enabled => true;
#else
    public static bool Enabled => false;
#endif
    public const string Unavailable = "This provider has no supported account connection here. Local usage history remains available where supported.";

    // These credentials are documented API keys or explicitly supplied OAuth
    // tokens. A browser-session or external application's stored login is never
    // an alternative when the key is absent.
    public static IReadOnlyList<string> CredentialKeys(string id)
    {
        if (id is "ollama-local" or "wayfinder" or "codex" or "claude") return [];
        if (id == "copilot") return ["GH_TOKEN", "GITHUB_TOKEN"];
        if (id == "gemini") return ["ANTIGRAVITY_OAUTH_CREDENTIALS_JSON"];
        if (id == "grok") return [];
        if (id == "devin") return ["DEVIN_BEARER_TOKEN", "DEVIN_AUTHORIZATION"];
        if (id == "bedrock") return ["AWS_SECRET_ACCESS_KEY"];
        if (id == "deepseek") return ["DEEPSEEK_API_KEY", "DEEPSEEK_KEY"];
        if (id == "openrouter") return ["OPENROUTER_API_KEY"];
        if (id == "moonshot") return ["MOONSHOT_API_KEY", "MOONSHOT_KEY"];
        var keys = ProviderCatalog.Find(id)?.EnvironmentKeys ?? [];
        return keys.Where(key => key.EndsWith("_API_KEY", StringComparison.Ordinal)
            || key.EndsWith("_ADMIN_KEY", StringComparison.Ordinal)
            || key is "FIREWORKS_KEY" or "XI_API_KEY" or "VENICE_KEY").ToArray();
    }

    public static bool IsExplicitApiKey(string key) =>
        key.EndsWith("_API_KEY", StringComparison.Ordinal) || key.EndsWith("_ADMIN_KEY", StringComparison.Ordinal)
        || key is "DEEPSEEK_KEY" or "MOONSHOT_KEY" or "FIREWORKS_KEY" or "VENICE_KEY" or "AWS_SECRET_ACCESS_KEY";

    public static bool IsPlainApiKey(string value) => value.Trim() is { Length: > 0 } key
        && !key.Equals("Session", StringComparison.OrdinalIgnoreCase) && !key.Contains("stytch_session", StringComparison.Ordinal)
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool HasKey(string id) => CredentialKeys(id).Count > 0;
    public static bool HasLocalEndpoint(string id) => id is "ollama-local" or "wayfinder";
    public static bool IsConnectionSetting(string key) =>
        !(key.Contains("COOKIE", StringComparison.Ordinal) || key.Contains("SESSION", StringComparison.Ordinal)
          || key.Contains("PASSWORD", StringComparison.Ordinal) || key.EndsWith("_PATH", StringComparison.Ordinal)
          || key.EndsWith("_HOME", StringComparison.Ordinal) || key.EndsWith("_EXECUTABLE", StringComparison.Ordinal)
          || key.EndsWith("_CLI", StringComparison.Ordinal) || key.Contains("PROFILE", StringComparison.Ordinal)
          || key.EndsWith("_SOURCE", StringComparison.Ordinal) || key.EndsWith("_AUTH_MODE", StringComparison.Ordinal));

    public static GoogleOAuthClient? ExplicitGoogleClient(Func<string, string?> value)
    {
        var id = value("ANTIGRAVITY_OAUTH_CLIENT_ID")?.Trim();
        var secret = value("ANTIGRAVITY_OAUTH_CLIENT_SECRET")?.Trim();
        return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret) ? new(id, secret) : null;
    }

    public static SignInPlan Plan(string id, Func<string, string?> value)
    {
        if (id is "codex" or "claude")
            return new(SignInKind.Settings, ProviderCatalog.Find(id)!.Name, null, null, "Add or select an account in CodeRim to sign in with the official provider.");
        if (id == "copilot") return ProviderSignIn.For(id) with
        { Note = "Approve the device code on github.com, or enter your GitHub access token in CodeRim." };
        string name = ProviderCatalog.Find(id)?.Name ?? id;
        if (id == "gemini" && ExplicitGoogleClient(value) is not null)
            return new(SignInKind.InApp, name, null, null, "Choose a Google account in your browser to sign in.")
                { InApp = InAppKind.AntigravityGoogle };
        var note = id == "gemini" ? "Configure a Google OAuth client for CodeRim to sign in."
            : HasKey(id) ? "Enter your provider API credential in CodeRim."
            : HasLocalEndpoint(id) ? "Use the configured local service." : Unavailable;
        return new(SignInKind.Settings, name, null, null, note);
    }
}
