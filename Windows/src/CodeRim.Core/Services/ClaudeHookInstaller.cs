using System.Text;
using System.Text.Json.Nodes;

namespace CodeRim.Core.Services;

public static class ClaudeHookInstaller
{
    private const string CommandPrefix = "powershell.exe -NoProfile -NonInteractive -EncodedCommand ";
    private const string InputScript = "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); $OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); "
        + "$buffer=New-Object char[] 524289; $count=[Console]::In.ReadBlock($buffer,0,$buffer.Length); if($count -gt 524288){exit 1}; if($count -eq 0){exit 0}; "
        + "[String]::new($buffer,0,$count) | & '";
    public static string SettingsPath => Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "settings.json");
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "CodeRimCLI.exe");
    public static bool HasOtherStatusLine()
    {
        if (!File.Exists(SettingsPath)) return false;
        var root = JsonNode.Parse(GuardedFile.Read(SettingsPath));
        var command = root?["statusLine"]?["command"]?.GetValue<string>();
        return !string.IsNullOrEmpty(command) && !IsOwned(command, "claude-status");
    }
    public static string Command(string operation, string? executable = null)
    {
        if (operation is not ("claude-status" or "claude-session-start")) throw new ArgumentException("Unknown hook operation.", nameof(operation));
        var script = InputScript + (executable ?? Executable).Replace("'", "''", StringComparison.Ordinal) + "' " + operation + "; exit $LASTEXITCODE";
        return CommandPrefix + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }
    private static bool IsOwned(string? command, string operation)
    {
        if (command is not { Length: > 0 and < 262144 }) return false;
        static bool IsHelper(string path) => path.Replace('\\', '/').EndsWith("/CodeRimCLI.exe", StringComparison.OrdinalIgnoreCase);
        if (command.StartsWith('"') && command.EndsWith("\" " + operation, StringComparison.Ordinal))
            return IsHelper(command[1..^(operation.Length + 2)]);
        if (!command.StartsWith(CommandPrefix, StringComparison.Ordinal)) return false;
        try
        {
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(command[CommandPrefix.Length..]));
            var prefix = script.StartsWith(InputScript, StringComparison.Ordinal) ? InputScript : "& '";
            var suffix = "' " + operation + "; exit $LASTEXITCODE";
            if (!script.StartsWith(prefix, StringComparison.Ordinal) || !script.EndsWith(suffix, StringComparison.Ordinal)) return false;
            var escapedPath = script[prefix.Length..^suffix.Length];
            // An escaped apostrophe is the only quote form our installer emits.
            if (escapedPath.Replace("''", "", StringComparison.Ordinal).Contains('\''))
                return false;
            return IsHelper(escapedPath);
        }
        catch (FormatException) { return false; }
    }
    public static string Configure(string? before, string executable, bool replaceExisting = false)
    {
        var root = before is null ? new JsonObject() : JsonNode.Parse(before)!.AsObject();
        var previous = root["statusLine"]?["command"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(previous) && !IsOwned(previous, "claude-status") && !replaceExisting)
            throw new InvalidOperationException("An existing status line needs explicit replacement approval.");
        root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = Command("claude-status", executable) };
        var hooks = root["hooks"]?.AsObject() ?? new JsonObject();
        if (root["hooks"] is null) root["hooks"] = hooks;
        var starts = hooks["SessionStart"]?.AsArray() ?? new JsonArray();
        if (hooks["SessionStart"] is null) hooks["SessionStart"] = starts;
        for (var i = starts.Count - 1; i >= 0; i--)
        {
            if (starts[i]?["hooks"] is not JsonArray handlers) continue;
            var removed = false;
            for (var j = handlers.Count - 1; j >= 0; j--)
                if (IsOwned(handlers[j]?["command"]?.GetValue<string>(), "claude-session-start"))
                { handlers.RemoveAt(j); removed = true; }
            if (removed && handlers.Count == 0) starts.RemoveAt(i);
        }
        starts.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Command("claude-session-start", executable) }) });
        return root.ToJsonString();
    }
    public static void Install(bool replaceExisting = false)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("This installer requires Windows.");
        if (!File.Exists(Executable)) throw new FileNotFoundException("Install the complete CodeRim Windows package first.");
        var path = SettingsPath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var before = File.Exists(path) ? GuardedFile.Read(path) : null;
        var after = Configure(before, Executable, replaceExisting);
        if (before == after) return;
        if (before is not null)
        {
            var backup = path + ".coderim-backup-" + Guid.NewGuid().ToString("N");
            GuardedFile.WritePrivate(backup, before); GuardedFile.Replace(path, before, after);
        }
        else GuardedFile.WritePrivate(path, after);
    }
}
