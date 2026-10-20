using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;
internal static partial class NativeSmoke
{
    private static void ActivityRegression(DashboardStore store)
    {
        var previousConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var root = Path.Combine(AppContext.BaseDirectory, "TestResults", "activity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "projects", "fixture"));
        var previousSessions = store.Sessions;
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", root);
            using var process = Process.GetCurrentProcess();
            var start = new DateTimeOffset(process.StartTime.ToUniversalTime());
            var now = DateTimeOffset.UtcNow;
            string Registry(string id, DateTimeOffset began, DateTimeOffset updated, string state) =>
                JsonSerializer.Serialize(new { pid = process.Id, cwd = "Fixture", sessionId = id, startedAt = began.ToUnixTimeMilliseconds(), statusUpdatedAt = updated.ToUnixTimeMilliseconds(), tempo = state });
            var record = Path.Combine(root, "sessions", "one.json");
            File.WriteAllText(record, Registry("complete", start, now.AddDays(-2), "busy"));
            File.WriteAllText(Path.Combine(root, "projects", "fixture", "complete.jsonl"),
                JsonSerializer.Serialize(new { type = "assistant", timestamp = now.AddDays(-1), message = new { stop_reason = "end_turn" } }) + "\n");
            Require(ClaudeSessions.Read().Single().State == "idle", "Live Claude registry ignored the older explicit transcript completion");
            File.Delete(record);
            foreach (var reverse in new[] { false, true })
            {
                var older = Registry("duplicate", start.AddSeconds(-2), now, "busy");
                var newer = Registry("duplicate", start, now.AddSeconds(-5), "waiting");
                File.WriteAllText(Path.Combine(root, "sessions", "a.json"), reverse ? newer : older);
                File.WriteAllText(Path.Combine(root, "sessions", "b.json"), reverse ? older : newer);
                Require(ClaudeSessions.Read().Single().State == "waiting", "Duplicate Claude registry selection depends on enumeration order");
            }
            store.UpdateSessionActivity([new("turn", "codex", "Fixture", "busy", now.AddMinutes(-3))]);
            store.UpdateSessionActivity([new("turn", "codex", "Fixture", "busy", now)]);
            Require(store.Sessions.Single().Since == now, "New Codex turn kept the previous turn start");
            store.UpdateSessionActivity([new("turn", "claude", "Fixture", "busy", now.AddMinutes(-3))]);
            store.UpdateSessionActivity([new("turn", "claude", "Fixture", "busy", now)]);
            Require(store.Sessions.Single().Since == now.AddMinutes(-3), "Continuing Claude tools reset elapsed turn time");
        }
        finally
        {
            store.UpdateSessionActivity(previousSessions);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousConfig);
            Directory.Delete(root, true);
        }
    }
}
