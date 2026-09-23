using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeRim.Core.Services;

public static partial class ClaudeHookInstaller
{
    private const string StateSuffix = ".coderim-state.json";
    private sealed record InstallState(int Version, bool HadOriginal, JsonNode? OriginalStatusLine, string[] Commands);
    private static JsonObject ParseSettings(string json) => JsonNode.Parse(json) as JsonObject
        ?? throw new InvalidDataException("Claude settings must contain a JSON object.");
    private static string? StatusCommand(JsonNode? status) => status is JsonObject value
        && value["command"] is JsonValue command && command.TryGetValue<string>(out var text) ? text : null;

    public static void Uninstall()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("This installer requires Windows.");
        UninstallAt(SettingsPath);
    }

    // Keep recovery state beside the selected Claude configuration, so changing
    // CLAUDE_CONFIG_DIR cannot restore another installation's status line.
    internal static void InstallAt(string settingsPath, string executable, bool replaceExisting = false, Action? beforeCommit = null)
    {
        var path = PreparePath(settingsPath);
        using var lease = Acquire(path);
        var before = ReadOptional(path);
        var after = Configure(before, executable, replaceExisting);
        var statePath = path + StateSuffix; var stateBefore = ReadOptional(statePath);
        var prior = ReadState(stateBefore);
        var original = before is null ? null : ParseSettings(before);
        var currentCommand = StatusCommand(original?["statusLine"]);
        var installed = Command("claude-status", executable);
        var previousIsOurs = currentCommand is not null && (prior?.Commands.Contains(currentCommand, StringComparer.Ordinal)
            ?? IsOwned(currentCommand, "claude-status"));
        var commands = previousIsOurs && currentCommand != installed ? new[] { currentCommand!, installed } : [installed];
        var state = prior is null
            ? new InstallState(1, !previousIsOurs && original?.ContainsKey("statusLine") == true,
                previousIsOurs ? null : original?["statusLine"]?.DeepClone(), commands)
            : prior with { Commands = commands };
        var stateAfter = JsonSerializer.Serialize(state);
        RequireWritable(after); RequireWritable(stateAfter);
        if (before != after && before is not null)
            GuardedFile.WritePrivate(path + ".coderim-backup-" + Guid.NewGuid().ToString("N"), before);
        // Journal first. If the settings write fails or the process exits here,
        // either the old or the newly installed command can still be removed and
        // the first-captured user value restored. Never journal an arbitrary path.
        Commit(statePath, stateBefore, stateAfter);
        beforeCommit?.Invoke();
        Commit(path, before, after);
    }

    internal static void UninstallAt(string settingsPath, Action? beforeCommit = null)
    {
        var path = Path.GetFullPath(settingsPath); CheckPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(path))) return;
        using var lease = Acquire(path);
        var before = ReadOptional(path); var statePath = path + StateSuffix; var stateBefore = ReadOptional(statePath);
        var state = ReadState(stateBefore);
        if (before is not null)
        {
            var root = ParseSettings(before); var current = StatusCommand(root["statusLine"]);
            // A user-edited command, including a different CodeRim command, wins.
            // Legacy installs have no restoration record: remove only recognizably
            // owned commands, never guess which timestamped backup was original.
            var owned = current is not null && (state?.Commands.Contains(current, StringComparer.Ordinal) ?? IsOwned(current, "claude-status"));
            var changed = false;
            if (owned)
            {
                if (state?.HadOriginal == true) root["statusLine"] = state.OriginalStatusLine?.DeepClone();
                else root.Remove("statusLine");
                changed = true;
            }
            if (root["hooks"] is JsonObject hooks && hooks["SessionStart"] is JsonArray starts)
            {
                var hooksChanged = false;
                for (var i = starts.Count - 1; i >= 0; i--)
                {
                    if (starts[i] is not JsonObject group || group["hooks"] is not JsonArray handlers) continue;
                    var removed = false;
                    for (var j = handlers.Count - 1; j >= 0; j--)
                        if (IsOwned(StatusCommand(handlers[j]), "claude-session-start"))
                        { handlers.RemoveAt(j); removed = true; changed = hooksChanged = true; }
                    if (removed && handlers.Count == 0) starts.RemoveAt(i);
                }
                if (hooksChanged && starts.Count == 0) hooks.Remove("SessionStart");
                if (hooksChanged && hooks.Count == 0) root.Remove("hooks");
            }
            beforeCommit?.Invoke();
            if (changed) Commit(path, before, root.ToJsonString());
        }
        // Retain recovery information on any failure, including a concurrent
        // settings update; only a completed uninstall discards it.
        if (stateBefore is not null)
        {
            if (ReadOptional(statePath) != stateBefore) throw new IOException("The Claude integration changed. Try again.");
            File.Delete(statePath);
        }
    }

    private static InstallState? ReadState(string? json)
    {
        if (json is null) return null;
        var state = JsonSerializer.Deserialize<InstallState>(json);
        if (state is null || state.Version != 1 || state.Commands is not { Length: > 0 and <= 2 }
            || state.Commands.Any(command => !IsOwned(command, "claude-status")))
            throw new InvalidDataException("The Claude integration recovery record is invalid.");
        return state;
    }
    private static string? ReadOptional(string path)
    {
        CheckPath(path);
        try { return GuardedFile.Read(path); }
        catch (FileNotFoundException) { return null; }
    }
    private static void Commit(string path, string? before, string after)
    {
        CheckPath(path);
        RequireWritable(after);
        if (before == after) return;
        if (before is null) GuardedFile.WritePrivate(path, after);
        else GuardedFile.Replace(path, before, after);
    }
    private static void RequireWritable(string value)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) > 262144)
            throw new InvalidDataException("Claude settings or their recovery record exceed the supported size.");
    }
    private static string PreparePath(string path)
    {
        path = Path.GetFullPath(path); CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); CheckPath(path);
        return path;
    }
    private static FileStream Acquire(string path)
    {
        var gate = path + ".coderim.lock"; CheckPath(gate);
        if (!File.Exists(gate))
        {
            try { GuardedFile.WritePrivate(gate, ""); }
            catch (IOException) when (File.Exists(gate)) { }
        }
        CheckPath(gate);
        // A busy operation fails without writes; never delete this file because
        // another process may already hold its inode as the commit boundary.
        return new FileStream(gate, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked Claude settings cannot be changed safely.");
    }
}
