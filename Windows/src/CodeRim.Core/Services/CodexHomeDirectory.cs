namespace CodeRim.Core.Services;

/// <summary>Keep Codex's empty-home fallback and literal configured directory consistent.</summary>
public static class CodexHomeDirectory
{
    public static string Resolve(string? configured, string profileDirectory)
        => string.IsNullOrEmpty(configured) ? Path.Combine(profileDirectory, ".codex") : configured;

    public static string CredentialPath(string? configured, string profileDirectory)
    {
        var home = Resolve(configured, profileDirectory);
        if (!Path.IsPathFullyQualified(home))
            throw new InvalidOperationException("A relative Codex home cannot be switched safely. Use an absolute CODEX_HOME path.");
        return Path.Combine(home, "auth.json");
    }
}
