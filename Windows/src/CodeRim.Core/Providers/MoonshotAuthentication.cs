namespace CodeRim.Core.Providers;

/// <summary>Moonshot credentials belong to a region; selecting another region never reuses a saved key.</summary>
public static class MoonshotAuthentication
{
    public static string? Region(string? value)
    {
        if (value is { Length: > 65536 } || value?.Trim().Any(char.IsControl) == true) return null;
        return Clean(value)?.ToLowerInvariant() switch
        { null or "" or "international" => "international", "china" => "china", _ => null };
    }
    public static string? Credential(string region, Func<string, string?> saved, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(saved); ArgumentNullException.ThrowIfNull(environment);
        if (region is not "international" and not "china") return null;
        var selected = Clean(saved("provider:moonshot:" + region));
        if (selected is not null) return selected;
        // Before region support, manually saved keys were only sent to .ai.
        if (region == "international" && Clean(saved("provider:moonshot")) is { } legacy) return legacy;
        if (Clean(environment("CODEXBAR_MOONSHOT_API_KEY_REGION"))?.ToLowerInvariant() == region
            && Clean(environment("CODEXBAR_MOONSHOT_API_KEY")) is { } configured) return configured;
        if (Region(environment("MOONSHOT_REGION")) != region) return null;
        return Clean(environment("MOONSHOT_API_KEY")) ?? Clean(environment("MOONSHOT_KEY"));
    }
    public static string Endpoint(string region) => region switch
    {
        "international" => "https://api.moonshot.ai/v1/users/me/balance",
        "china" => "https://api.moonshot.cn/v1/users/me/balance",
        _ => throw new ArgumentException("Unknown Moonshot region.", nameof(region))
    };
    private static string? Clean(string? raw)
    {
        var text = raw?.Trim();
        if (text is { Length: >= 2 } && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
            text = text[1..^1].Trim();
        return text is { Length: > 0 and <= 65536 } && !text.Any(char.IsControl) ? text : null;
    }
}
