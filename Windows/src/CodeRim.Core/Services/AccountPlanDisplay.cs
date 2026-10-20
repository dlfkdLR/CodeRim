using System.Globalization;
using System.Text.Json;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Services;

/// <summary>Display metadata only; never changes a saved login's identity or credential.</summary>
public static class AccountPlanDisplay
{
    public static string? Name(string provider, string? subscription, string? profile = null, string? email = null, string? organization = null)
    {
        subscription = Safe(subscription);
        if (subscription is null || subscription.Length > 80) return null;
        if (provider == "codex")
        {
            if (subscription.Equals("prolite", StringComparison.OrdinalIgnoreCase)) return "Pro 5x";
            if (subscription.Equals("pro", StringComparison.OrdinalIgnoreCase)) return "Pro 20x";
        }
        if (provider == "claude" && subscription.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            var tier = ClaudeTier(profile, email, organization);
            if (tier?.Equals("default_claude_max_5x", StringComparison.OrdinalIgnoreCase) == true) return "Max 5x";
            if (tier?.Equals("default_claude_max_20x", StringComparison.OrdinalIgnoreCase) == true) return "Max 20x";
        }
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(subscription.Replace('_', ' ').ToLower(CultureInfo.CurrentCulture));
    }
    private static string? ClaudeTier(string? profile, string? email, string? organization)
    {
        if (profile is not { Length: > 0 and <= 262144 } || Safe(email) is not { } owner || Safe(organization) is not { } workspace) return null;
        try
        {
            using var document = JsonDocument.Parse(profile); var account = Get(document.RootElement, "oauthAccount");
            if (Safe(Text(account, "emailAddress")) != owner || Safe(Text(account, "organizationUuid")) != workspace) return null;
            return Safe(Text(account, "userRateLimitTier")) ?? Safe(Text(account, "organizationRateLimitTier"));
        }
        catch (JsonException) { return null; }
    }
    private static string? Safe(string? value)
    {
        value = value?.Trim();
        return value is { Length: > 0 and <= 320 } && !value.Any(char.IsControl) ? value : null;
    }
}
