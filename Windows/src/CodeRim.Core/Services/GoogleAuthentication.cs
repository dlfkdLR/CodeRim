using System.Text.Json;
using System.Text.Json.Nodes;
namespace CodeRim.Core.Services;
public static class GoogleAuthentication
{
    public static string Read(string credentialPath, string configDirectory, string? activeConfiguration = null)
    {
        var auth = JsonNode.Parse(GuardedFile.Read(credentialPath)) as JsonObject ?? throw new InvalidDataException("Invalid Google login.");
        if (string.IsNullOrWhiteSpace(activeConfiguration))
        {
            try { activeConfiguration = GuardedFile.Read(Path.Combine(configDirectory, "active_config"), 4096).Trim(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        activeConfiguration = string.IsNullOrWhiteSpace(activeConfiguration) ? "default" : activeConfiguration;
        if (activeConfiguration.Length <= 128 && activeConfiguration.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_'))
        {
            try
            {
                var core = false;
                foreach (var line in GuardedFile.Read(Path.Combine(configDirectory, "configurations", "config_" + activeConfiguration)).Split('\n'))
                {
                    var text = line.Trim();
                    if (text.StartsWith('[')) { core = text.Equals("[core]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (!core || text.StartsWith('#') || text.StartsWith(';')) continue;
                    var pair = text.Split('=', 2);
                    if (pair.Length == 2 && pair[0].Trim().Equals("project", StringComparison.OrdinalIgnoreCase) && pair[1].Trim() is { Length: > 0 } project)
                        auth["coderim_project"] = project;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return auth.ToJsonString();
    }
}
