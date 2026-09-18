using System.IO;
using System.Text.Json.Nodes;
using CodeRim.Core.Services;

namespace CodeRim.Core.Services;

public static class ClaudeHookInstaller
{
    public static string SettingsPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude"), "settings.json");
        }
    }
    public static bool HasOtherStatusLine()
    {
        if (!File.Exists(SettingsPath)) return false;
        var json = JsonNode.Parse(GuardedFile.Read(SettingsPath));
        var command = json?["statusLine"]?["command"]?.GetValue<string>();
        return !string.IsNullOrEmpty(command) && command != Command("claude-status");
    }
    public static string Command(string operation)
    {
        // Encoded PowerShell has no shell-sensitive path interpolation, under Git Bash or PowerShell.
        var executable = Path.Combine(AppContext.BaseDirectory, "CodeRimCLI.exe").Replace("'", "''");
        var script = "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); $OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); "
            + "$buffer=New-Object char[] 524289; $count=[Console]::In.ReadBlock($buffer,0,$buffer.Length); if($count -gt 524288){exit 1}; if($count -eq 0){exit 0}; "
            + "[String]::new($buffer,0,$count) | & '" + executable + "' " + operation + "; exit $LASTEXITCODE";
        return "powershell.exe -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
    }
    public static void Install(bool replaceExisting = false)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("This installer requires Windows.");
        if (HasOtherStatusLine() && !replaceExisting) throw new InvalidOperationException("An existing status line needs explicit replacement approval.");
        var executable = Path.Combine(AppContext.BaseDirectory, "CodeRimCLI.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Install the complete CodeRim Windows package first.");
        var path = SettingsPath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var before = File.Exists(path) ? GuardedFile.Read(path) : null;
        var root = before is null ? new JsonObject() : JsonNode.Parse(before)!.AsObject();

        root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = Command("claude-status") };
        var hooks = root["hooks"]?.AsObject() ?? new JsonObject();
        if (root["hooks"] is null) root["hooks"] = hooks;
        var starts = hooks["SessionStart"]?.AsArray() ?? new JsonArray();
        if (hooks["SessionStart"] is null) hooks["SessionStart"] = starts;
        if (!starts.Any(x => x?["hooks"]?.AsArray().Any(h => h?["command"]?.GetValue<string>() == Command("claude-session-start")) == true))
            starts.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Command("claude-session-start") }) });
        var after = root.ToJsonString();
        if (before is not null)
        {
            var backup = path + ".coderim-backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            GuardedFile.WritePrivate(backup, before); GuardedFile.Replace(path, before, after);
        }
        else
        {
            GuardedFile.WritePrivate(path, after);
        }
    }
}
