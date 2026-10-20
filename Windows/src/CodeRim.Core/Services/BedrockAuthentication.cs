using System.Text.Json;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Services;
public static class BedrockAuthentication
{
    public sealed record Profile(string Credential, string Region);
    public static bool UseProfile(string? credential, Func<string, string?> setting)
    {
        var mode = setting("CODEXBAR_BEDROCK_AUTH_MODE")?.Trim();
        if (string.Equals(mode, "profile", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(mode, "keys", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(credential)) return true;
        if (credential.TrimStart().StartsWith('{')) return false;
        return string.IsNullOrWhiteSpace(setting("AWS_ACCESS_KEY_ID"))
            && (!string.IsNullOrWhiteSpace(setting("AWS_PROFILE")) || !string.IsNullOrWhiteSpace(setting("AWS_DEFAULT_PROFILE")));
    }
    public static async Task<Profile> ResolveAsync(Func<string, string?> setting, Func<IReadOnlyList<string>, Task<string>> run)
    {
        ArgumentNullException.ThrowIfNull(setting); ArgumentNullException.ThrowIfNull(run);
        var profile = setting("AWS_PROFILE")?.Trim(); if (string.IsNullOrEmpty(profile)) profile = setting("AWS_DEFAULT_PROFILE")?.Trim();
        if (string.IsNullOrEmpty(profile)) profile = "default";
        if (profile.Length > 256 || profile.Any(char.IsControl)) throw new InvalidDataException("Invalid AWS profile name.");
        var output = await run(["configure", "export-credentials", "--profile", profile, "--format", "process"]).ConfigureAwait(false);
        if (output.Length > 262144) throw new InvalidDataException("AWS profile output is too large.");
        using var json = JsonDocument.Parse(output); var root = json.RootElement;
        string? Clean(string key) { var value = Text(root, key)?.Trim(); return value is { Length: > 0 } && !value.Any(char.IsControl) ? value : null; }
        var key = Clean("AccessKeyId"); var secret = Clean("SecretAccessKey"); var session = Clean("SessionToken");
        if (key is null || secret is null) throw new InvalidDataException("AWS CLI returned an incomplete profile.");
        if (Get(root, "Expiration").ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
            && (Date(Get(root, "Expiration")) is not { } expiration || expiration <= DateTimeOffset.UtcNow))
            throw new InvalidDataException("AWS profile has expired. Sign in to the AWS CLI again.");
        var region = setting("AWS_REGION")?.Trim(); if (string.IsNullOrEmpty(region)) region = setting("AWS_DEFAULT_REGION")?.Trim();
        if (string.IsNullOrEmpty(region))
        {
            try { region = (await run(["configure", "get", "region", "--profile", profile]).ConfigureAwait(false)).Trim(); }
            catch (IOException) { }
        }
        if (string.IsNullOrEmpty(region)) region = "us-east-1";
        if (region.Length > 64 || !region.All(x => char.IsAsciiLetterOrDigit(x) || x == '-')) throw new InvalidDataException("Invalid AWS profile region.");
        return new(JsonSerializer.Serialize(new { AccessKeyId = key, SecretAccessKey = secret, SessionToken = session }), region);
    }
}
