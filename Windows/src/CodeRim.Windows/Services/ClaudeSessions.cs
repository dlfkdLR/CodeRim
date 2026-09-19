using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CodeRim.Core.Parsing;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal static class ClaudeSessions
{
    public static IReadOnlyList<SessionActivity> Read()
    {
        var results = new Dictionary<string, SessionActivity>(StringComparer.Ordinal);
        var projects = UsageScanner.DefaultRoots("claude")[0];
        var directory = Path.Combine(Path.GetDirectoryName(projects)!, "sessions");
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return [];
        var transcripts = Directory.Exists(projects) ? Directory.EnumerateFiles(projects, "*.jsonl", new EnumerationOptions
        { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(50000).ToLookup(Path.GetFileNameWithoutExtension, StringComparer.Ordinal) : null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(512))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 65536) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path)); var root = document.RootElement;
                if (ProviderParsers.Count(root, "pid") is not { } pid || pid > int.MaxValue) continue;
                using var process = Process.GetProcessById((int)pid); if (process.HasExited) continue;
                var started = ProviderParsers.Date(ProviderParsers.Get(root, "startedAt"), milliseconds: true);
                if (started.HasValue && Math.Abs((process.StartTime.ToUniversalTime() - started.Value.UtcDateTime).TotalSeconds) > 10) continue;
                var id = ProviderParsers.Text(root, "sessionId"); if (string.IsNullOrEmpty(id)) continue;
                var state = ProviderParsers.Text(root, "tempo") ?? ProviderParsers.Text(root, "status");
                state = state switch { "blocked" or "waiting" => "waiting", "active" or "busy" => "busy", "idle" => "idle", _ => null };
                var updated = ProviderParsers.Date(ProviderParsers.Get(root, "statusUpdatedAt"), milliseconds: true)
                    ?? ProviderParsers.Date(ProviderParsers.Get(root, "updatedAt"), milliseconds: true) ?? started ?? DateTimeOffset.Now;
                var transcript = transcripts?[id].FirstOrDefault(); var activity = transcript is null ? null : ActivityReader.ReadClaude(transcript, DateTimeOffset.Now);
                if (state is null) { if (activity is null) continue; state = activity.State; updated = activity.Since; }
                else if (activity is { State: "idle" } && activity.Since > updated) { state = "idle"; updated = activity.Since; }
                var cwd = ProviderParsers.Text(root, "cwd") ?? "Claude session";
                var name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
                results[id] = new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id))), "claude", name, state, updated)
                    { ProcessId = started.HasValue ? (int)pid : null,
                        ProcessStartedAt = started.HasValue ? new DateTimeOffset(process.StartTime.ToUniversalTime()) : null };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return results.Values.OrderByDescending(x => x.Since).ToArray();
    }
}
