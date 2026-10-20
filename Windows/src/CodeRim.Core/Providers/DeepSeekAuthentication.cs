namespace CodeRim.Core.Providers;

public sealed record DeepSeekCredential(string Source, string? Token);

public static class DeepSeekAuthentication
{
    public static string? Source(string? value) => value?.Trim().ToLowerInvariant() switch
    { null or "" or "auto" => "auto", "api" => "api", "web" => "web", _ => null };

    public static DeepSeekCredential? Resolve(string? source, Func<string, string?> saved, Func<string, string?> environment)
    {
        var mode = Source(source);
        if (mode is null) return null;
        static string? First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        // Do not touch an unrelated encrypted slot: an unusable Web session must not break API mode.
        if (mode != "web")
        {
            var api = First(saved("provider:deepseek"), environment("DEEPSEEK_API_KEY"), environment("DEEPSEEK_KEY"));
            if (mode == "api" || api is not null) return new("api", api);
        }
        return new("web", First(saved("provider:deepseek:web"), environment("DEEPSEEK_PLATFORM_TOKEN"), environment("DEEPSEEK_USER_TOKEN")));
    }
}
