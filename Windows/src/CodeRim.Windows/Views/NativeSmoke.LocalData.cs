using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task LocalDataRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var before = settings.Current; var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var home = Path.Combine(Path.GetTempPath(), "coderim-local-data-" + Guid.NewGuid().ToString("N"));
        DashboardWindow? window = null; DashboardStore? store = null; Exception? failure = null; var cleanup = new List<Exception>();
        Task? activeRebuild = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "sessions")); Environment.SetEnvironmentVariable("CODEX_HOME", home);
            var source = Path.Combine(home, "sessions", "session.jsonl");
            var original = """
                {"type":"session_meta","payload":{"id":"rebuild-fixture","model":"gpt-6-astra"}}
                {"type":"event_msg","timestamp":"2026-09-01T00:00:01Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":20,"cache_write_input_tokens":0},"last_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":20,"cache_write_input_tokens":0}}}}
                """ + "\n";
            File.WriteAllText(source, original);
            var repository = new UsageRepository(Path.Combine(home, "statistics.sqlite"));
            repository.Merge("codex", [new("obsolete", DateTimeOffset.Now.AddDays(-1), new(99999, 0, 1), "gpt-6-astra")]);
            settings.Save(before with { AccountLimitsEnabled = false, EnabledProviders = ["codex"] });
            store = new DashboardStore(settings, vault, providerConnections: new ProviderConnections(vault), usageRepository: repository);
            window = new DashboardWindow(store, settings, vault); window.Show(); window.Navigate("codex"); await Idle();
            Button Button(string id) => Descendants<Button>(window).Single(x => AutomationProperties.GetAutomationId(x) == id);
            var rebuild = Button("local-data.rebuild"); var clear = Button("local-data.clear");
            Require(rebuild.IsEnabled && clear.IsEnabled, "Local data actions are unavailable before an operation.");
            rebuild.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            activeRebuild = store.RebuildStatisticsAsync("codex");
            Require(store.RebuildingProviders.Contains("codex") && !rebuild.IsEnabled && !clear.IsEnabled, "Rebuild did not protect the UI against concurrent local data actions.");
            Require(ReferenceEquals(activeRebuild, store.RebuildStatisticsAsync("codex")), "Duplicate rebuild did not join the running operation.");
            var rejectedClear = false;
            try { await store.ClearLocalHistoryAsync("codex"); }
            catch (InvalidOperationException) { rejectedClear = true; }
            Require(rejectedClear, "A conflicting clear was silently accepted during rebuild.");
            await activeRebuild;
            await MotionUntil(() => !store.RebuildingProviders.Contains("codex"), "Local rebuild did not finish."); await Idle();
            Require(store.DataOperationMessages["codex"] == "Statistics rebuilt." && store.Usage["codex"].AllTime.TotalTokens == 120,
                "Rebuild did not replace obsolete statistics from the actual session source.");
            Require(store.SourceCounts["codex"] == 1 && store.DataStatistics["codex"] is { RecordCount: 1, DatabaseBytes: > 0, OldestRecord: not null },
                "Local source, size and date statistics are incomplete.");
            Require(Descendants<TextBlock>(window).Any(x => x.Text == "Pricing catalog") && Descendants<TextBlock>(window).Any(x => x.Text == "Statistics rebuilt."),
                "Provider settings did not render Sources and Manage Data results.");
            var section = Descendants<TextBlock>(window).Single(x => x.Text == "Codex Local Data");
            var viewport = Descendants<ScrollViewer>(window).Single(x => x.ScrollableHeight > 0 && x.ActualHeight > 200);
            viewport.ScrollToVerticalOffset(viewport.VerticalOffset + section.TranslatePoint(new Point(), viewport).Y - 16); await Idle();
            Capture(window, Path.Combine(directory, "windows-local-data.png"));
            var emptySource = Path.Combine(home, "sessions", "empty.jsonl"); File.WriteAllText(emptySource, "");
            await store.RebuildStatisticsAsync("codex");
            Require(repository.Read("codex").Sum(x => x.Usage.TotalTokens) == 120 && store.DataOperationMessages["codex"].Contains("retained", StringComparison.Ordinal),
                "Valid records from one file hid an empty source during rebuild.");
            File.Delete(emptySource);
            File.AppendAllText(source, "malformed fixture line\n"); await store.RebuildStatisticsAsync("codex");
            Require(repository.Read("codex").Sum(x => x.Usage.TotalTokens) == 120 && store.DataOperationMessages["codex"].Contains("retained", StringComparison.Ordinal),
                "An incomplete source destroyed the previous statistics.");
            File.WriteAllText(source, ""); await store.RebuildStatisticsAsync("codex");
            Require(repository.Read("codex").Sum(x => x.Usage.TotalTokens) == 120 && store.DataOperationMessages["codex"].Contains("retained", StringComparison.Ordinal),
                "An empty source destroyed the previous statistics.");
            File.Delete(source); await store.RebuildStatisticsAsync("codex");
            Require(repository.Read("codex").Sum(x => x.Usage.TotalTokens) == 120 && store.SourceCounts["codex"] == 0,
                "Missing source erased history or retained a stale source count.");
            File.WriteAllText(source, original); await store.ClearLocalHistoryAsync("codex"); await store.RebuildStatisticsAsync("codex");
            Require(repository.Read("codex").Count == 0 && store.Usage["codex"].AllTime.TotalTokens == 0 && File.ReadAllText(source) == original,
                "Rebuild resurrected cleared history or changed original session files.");
            File.WriteAllText(Path.Combine(directory, "windows-local-data.json"), JsonSerializer.Serialize(new { completed = true,
                checks = new List<string> { "Native action reparses real fixture JSONL into atomic derived statistics", "Duplicate rebuild and clear controls are disabled while busy", "Local size/source/date, Sources and operation status render", "Malformed/missing sources retain previous statistics", "Rebuild preserves clear exclusions and original source files" } }));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => window?.Close()); Restore(() => store?.Dispose());
            if (activeRebuild is not null)
                try { await activeRebuild; } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            Restore(() => Environment.SetEnvironmentVariable("CODEX_HOME", previousHome));
            Restore(() => settings.Save(before)); Restore(() => Directory.Delete(home, true));
        }
        if (cleanup.Count > 0) throw new AggregateException("Local data fixture cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
