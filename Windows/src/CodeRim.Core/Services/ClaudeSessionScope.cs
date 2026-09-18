using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Services;

/// <summary>Quota callbacks stay bound to the login observed by the session-start hook.</summary>
public static class ClaudeSessionScope
{
    public static bool Register(string directory, string sessionId, string scope)
    {
        Validate(sessionId, scope);
        var path = PathFor(directory, sessionId); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) return Matches(directory, sessionId, scope);
        try
        {
            GuardedFile.WritePrivate(path, scope);
            return true;
        }
        catch (IOException) { return Matches(directory, sessionId, scope); }
    }
    public static bool Matches(string directory, string sessionId, string scope)
    {
        Validate(sessionId, scope);
        var path = PathFor(directory, sessionId);
        try { return new FileInfo(path).Length <= 128 && File.ReadAllText(path) == scope; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    private static void Validate(string session, string scope)
    {
        if (session.Length is < 1 or > 256 || scope.Length != 64 || !scope.All(char.IsAsciiHexDigit)) throw new InvalidDataException("A verified session identity is required.");
    }
    private static string PathFor(string directory, string session) => Path.Combine(directory, "claude-session-scopes", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session))) + ".scope");
}
