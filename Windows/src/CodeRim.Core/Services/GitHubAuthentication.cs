using System.Text.Json;
namespace CodeRim.Core.Services;

/// <summary>Read only github.com's active GitHub CLI token; never infer an account from an unrelated environment token.</summary>
public static class GitHubAuthentication
{
    public static string ConfigurationDirectory(string home, Func<string, string?> environment, bool windows)
    {
        ArgumentNullException.ThrowIfNull(environment);
        static string? Absolute(string? value) => value?.Trim() is { Length: > 0 } path && Path.IsPathFullyQualified(path) ? path : null;
        return Absolute(environment("GH_CONFIG_DIR")) ?? (Absolute(environment("XDG_CONFIG_HOME")) is { } xdg ? Path.Combine(xdg, "gh") : null)
            ?? (windows && Absolute(environment("APPDATA")) is { } appData ? Path.Combine(appData, "GitHub CLI") : Path.Combine(home, ".config", "gh"));
    }
    public static string? Configured(string? saved, string? gh, string? github)
    {
        foreach (var value in new[] { saved, gh, github }) if (value?.Trim() is { Length: > 0 } token) return token;
        return null;
    }
    public static string? ReadHosts(string directory)
    {
        try { return GuardedFile.Read(Path.Combine(directory, "hosts.yml")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }
    public static string? ParseHosts(string? text)
    {
        if (text is null || text.Length > 262144) return null;
        var active = false; var seenHost = false; var stack = new List<(int Indent, string Key, bool Container)>();
        var childIndents = new Dictionary<string, int>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal); var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var indent = line.TakeWhile(c => c is ' ' or '\t').Count();
            var colon = trimmed.IndexOf(':');
            if (colon < 0) { if (active) return null; continue; }
            var key = trimmed[..colon]; var raw = trimmed[(colon + 1)..].Trim();
            if (indent == 0)
            {
                active = key == "github.com";
                stack.Clear();
                if (active) { if (seenHost || (raw.Length > 0 && !raw.StartsWith('#'))) return null; seenHost = true; }
                continue;
            }
            if (!active) continue;
            if (line[..indent].Contains('\t') || key.Length == 0 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) return null;
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            if (stack.Count > 0 && !stack[^1].Container) return null;
            var parent = string.Join("/", stack.Select(item => item.Key));
            if (childIndents.TryGetValue(parent, out var expectedIndent) && expectedIndent != indent) return null;
            childIndents[parent] = indent;
            var path = parent.Length == 0 ? key : parent + "/" + key;
            if (!paths.Add(path)) return null;
            if (raw.Length == 0 || raw.StartsWith('#')) { stack.Add((indent, key, true)); continue; }
            var scalar = Scalar(raw);
            if (scalar is null) return null; // Unsupported YAML is left to gh itself.
            values[path] = scalar;
            stack.Add((indent, key, false));
        }
        values.TryGetValue("oauth_token", out var rootToken); values.TryGetValue("user", out var user);
        string? selected = null;
        if (user is { Length: > 0 } && user.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            values.TryGetValue("users/" + user + "/oauth_token", out selected);
        if (rootToken is { Length: > 0 } && selected is { Length: > 0 } && rootToken != selected) return null;
        return Token(rootToken) ?? Token(selected);
    }
    public static string? Token(string? text) => text?.Trim() is { Length: > 0 and <= 65536 } value && !value.Any(char.IsControl) ? value : null;
    private static string? Scalar(string raw)
    {
        try
        {
            if (raw.StartsWith('"')) return JsonSerializer.Deserialize<string>(raw);
            if (raw.StartsWith('\'')) return raw.Length >= 2 && raw.EndsWith('\'') ? raw[1..^1].Replace("''", "'", StringComparison.Ordinal) : null;
            if ("!&*[{|>".Contains(raw[0], StringComparison.Ordinal)) return null;
            var comment = raw.IndexOf(" #", StringComparison.Ordinal);
            return (comment < 0 ? raw : raw[..comment]).Trim();
        }
        catch (JsonException) { return null; }
    }
}
