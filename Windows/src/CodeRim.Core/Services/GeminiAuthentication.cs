using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Services;
public static class GeminiAuthentication
{
    private static readonly JsonDocumentOptions SettingsOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime Modified, string Metadata)> ClientCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaximumClientBytes = 32 * 1024 * 1024;
    public static string BuildCredential(string profile, IEnumerable<string> clientSources)
    {
        var node = JsonNode.Parse(profile) as JsonObject ?? throw new InvalidDataException("Invalid Gemini login.");
        if (node["client_id"] is null || node["client_secret"] is null)
            foreach (var source in clientSources)
            {
                if (source.Length > MaximumClientBytes) continue;
                var id = Regex.Match(source, """OAUTH_CLIENT_ID\s*=\s*['"]([\w.\-]+)['"]""", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(500));
                var secret = Regex.Match(source, """OAUTH_CLIENT_SECRET\s*=\s*['"]([\w\-]+)['"]""", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(500));
                if (!id.Success || !secret.Success) continue;
                node["client_id"] = id.Groups[1].Value; node["client_secret"] = secret.Groups[1].Value; break;
            }
        return node.ToJsonString();
    }
    public static string? Read(string home, IEnumerable<string> npmRoots)
    {
        var settings = Path.Combine(home, ".gemini", "settings.json");
        if (File.Exists(settings))
        {
            using var document = JsonDocument.Parse(GuardedFile.Read(settings), SettingsOptions);
            var auth = Text(Get(Get(document.RootElement, "security"), "auth"), "selectedType");
            if (auth is not null && auth != "oauth-personal") return null;
        }
        var profile = GuardedFile.Read(Path.Combine(home, ".gemini", "oauth_creds.json"));
        IEnumerable<string> Sources()
        {
            var budget = 96L * 1024 * 1024;
            foreach (var root in npmRoots.Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).Take(32))
            {
                var module = Path.Combine(root, "node_modules", "@google", "gemini-cli");
                var fixedPaths = new[] {
                    Path.Combine(module, "node_modules", "@google", "gemini-cli-core", "dist", "src", "code_assist", "oauth2.js"),
                    Path.Combine(root, "node_modules", "@google", "gemini-cli-core", "dist", "src", "code_assist", "oauth2.js"),
                    Path.Combine(module, "dist", "src", "code_assist", "oauth2.js") };
                var paths = fixedPaths.AsEnumerable();
                var bundle = Path.Combine(module, "bundle");
                try
                {
                    if (Directory.Exists(bundle) && (File.GetAttributes(bundle) & FileAttributes.ReparsePoint) == 0)
                        paths = paths.Concat(Directory.EnumerateFiles(bundle, "*.js", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                            .Take(512).Select(path => new FileInfo(path)).Where(file => file.Length <= MaximumClientBytes).OrderByDescending(file => file.Length).Take(16).Select(file => file.FullName).ToArray());
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                foreach (var path in paths)
                {
                    string? source = null;
                    try
                    {
                        if (!File.Exists(path)) continue;
                        var file = new FileInfo(path);
                        if (file.Length > MaximumClientBytes) continue;
                        if (ClientCache.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc) source = cached.Metadata;
                        else
                        {
                            if (file.Length > budget) continue;
                            budget -= file.Length;
                            var contents = GuardedFile.Read(path, MaximumClientBytes);
                            using var metadata = JsonDocument.Parse(BuildCredential("{}", [contents]));
                            var id = Text(metadata.RootElement, "client_id"); var secret = Text(metadata.RootElement, "client_secret");
                            source = id is null || secret is null ? "" : "OAUTH_CLIENT_ID='" + id + "';OAUTH_CLIENT_SECRET='" + secret + "';";
                            if (ClientCache.Count >= 128) ClientCache.Clear();
                            ClientCache[path] = (file.Length, file.LastWriteTimeUtc, source);
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or RegexMatchTimeoutException) { }
                    if (source is { Length: > 0 }) yield return source;
                }
            }
        }
        return BuildCredential(profile, Sources());
    }
}
