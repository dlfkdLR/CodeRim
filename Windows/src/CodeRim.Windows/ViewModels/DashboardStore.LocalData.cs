using System.IO;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.ViewModels;

internal sealed partial class DashboardStore
{
    internal Dictionary<string, int> SourceCounts { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, LocalDataStatistics> DataStatistics { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> DataOperationMessages { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> RebuildingProviders { get; } = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> localDataTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> localDataClearOperations = new(StringComparer.Ordinal);
    internal bool LocalDataBusy(string id) => localRefreshing || RebuildingProviders.Contains(id);

    internal Task RebuildStatisticsAsync(string id) => StartLocalDataOperation(id, clear: false);
    internal Task ClearLocalHistoryAsync(string id) => StartLocalDataOperation(id, clear: true);
    private Task StartLocalDataOperation(string id, bool clear)
    {
        if (disposed || Synthetic || !scanners.ContainsKey(id)) return Task.CompletedTask;
        if (localDataTasks.TryGetValue(id, out var active)) return localDataClearOperations[id] == clear ? active
            : Task.FromException(new InvalidOperationException("A different local data operation is already running."));
        RebuildingProviders.Add(id);
        DataOperationMessages[id] = clear ? "Clearing local history…" : "Rebuilding statistics…";
        var task = RunLocalDataOperationAsync(id, clear); localDataTasks[id] = task; localDataClearOperations[id] = clear; Changed(); return task;
    }
    private async Task RunLocalDataOperationAsync(string id, bool clear)
    {
        await Task.Yield(); // Register the shared task before any synchronous completion.
        var entered = false; var committed = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await refreshLock.WaitAsync(cancellation.Token).ConfigureAwait(true); entered = true;
            if (clear)
            {
                generations[id] = Generation(id) + 1;
                var cutoff = DateTimeOffset.Now;
                await Task.Run(() => repository.Clear(id, cutoff), cancellation.Token).ConfigureAwait(true); committed = true;
                scanners[id].InvalidateCachedSources(); Usage[id] = UsageSnapshot.Empty; Events.Remove(id); SessionDetails.Remove(id);
                DataStatistics[id] = await Task.Run(() => repository.Statistics(id), lifetime.Token).ConfigureAwait(true);
                DataOperationMessages[id] = "Local history cleared."; Persist(); return;
            }
            // A separate scanner preserves the last usable cache until a complete,
            // bounded read can atomically replace derived records.
            var scanner = new UsageScanner(id, projectKey: projectKey, requireCompleteSources: true);
            ScanResult? scan = null;
            for (var pass = 0; pass < 4; pass++)
            {
                scan = await scanner.ScanAsync(settings.Current.WeekStart, cancellation.Token).ConfigureAwait(true);
                if (!scan.HasMoreWork) break;
            }
            if (scan is not null) SourceCounts[id] = scan.SourceCount;
            if (scan is null || scan.SourceCount == 0 || scan.Events.Count == 0 || scan.HasMoreWork || scan.Snapshot.Quality == DataQuality.Partial)
            {
                DataOperationMessages[id] = scan?.SourceCount == 0 ? "No session files found. Existing statistics were retained."
                    : scan?.Events.Count == 0 ? "No usable usage records found. Existing statistics were retained."
                    : "Some session files could not be fully read. Existing statistics were retained.";
                return;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var events = await Task.Run(() => repository.Rebuild(id, scan.Events, scan.Sessions), cancellation.Token).ConfigureAwait(true);
            committed = true;
            // A rebuild's strict validation must not change ordinary incremental
            // reads. Discard the old cache but restore its tolerant source policy.
            scanners[id] = new UsageScanner(id, projectKey: projectKey); SourceCounts[id] = scan.SourceCount;
            Events[id] = events; SessionDetails[id] = repository.ReadSessionDetails(id);
            Usage[id] = UsageScanner.Aggregate(events, DateTimeOffset.Now, settings.Current.WeekStart, false);
            DataStatistics[id] = await Task.Run(() => repository.Statistics(id), lifetime.Token).ConfigureAwait(true);
            DataOperationMessages[id] = "Statistics rebuilt."; Persist();
        }
        catch (OperationCanceledException)
        { DataOperationMessages[id] = committed ? "Local data updated. Refresh to reload the summary." : "The operation was interrupted. Existing statistics were retained."; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or Microsoft.Data.Sqlite.SqliteException)
        { DataOperationMessages[id] = committed ? "Local data updated. The summary could not be refreshed." : "Local data could not be updated. Existing statistics were retained."; }
        finally
        {
            if (entered) refreshLock.Release(); RebuildingProviders.Remove(id); localDataTasks.Remove(id); localDataClearOperations.Remove(id); Changed();
            if (entered && pendingRefresh && !disposed) _ = RefreshLocalAsync();
        }
    }
}
