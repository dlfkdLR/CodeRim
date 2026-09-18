using System.ComponentModel;
using System.IO;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.ViewModels;

internal sealed class DashboardStore : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettingsStore settings;
    private readonly ProviderConnections connections;
    private readonly UsageRepository repository;
    private readonly Dictionary<string, UsageScanner> scanners = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private DateTimeOffset lastRemoteRefresh;
    private bool pendingRefresh;
    private bool pendingUserRefresh;
    private bool disposed;
    private readonly Dictionary<string, int> generations = new(StringComparer.Ordinal);
    private int Generation(string id) => generations.GetValueOrDefault(id);
    public Dictionary<string, UsageSnapshot> Usage { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<UsageEvent>> Events { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ProviderReading> Readings { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<SessionActivity> Sessions { get; private set; } = [];
    public bool IsRefreshing { get; private set; }
    public string Status { get; private set; } = "Reading local usage…";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<ProviderReading>? ReadingUpdated;
    public bool Synthetic { get; }
    public DashboardStore(AppSettingsStore settings, CredentialVault vault, bool synthetic = false)
    {
        this.settings = settings; Synthetic = synthetic; connections = new ProviderConnections(vault);
        repository = new UsageRepository(Path.Combine(CompanionFile.DataDirectory, "usage.sqlite"));
        var keyPath = Path.Combine(CompanionFile.DataDirectory, "project-key.bin");
        if (!File.Exists(keyPath)) File.WriteAllBytes(keyPath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var projectKey = File.ReadAllBytes(keyPath);
        if (projectKey.Length != 32) throw new InvalidDataException("Project identity key is invalid.");
        foreach (var id in new[] { "codex", "claude" }) scanners[id] = new UsageScanner(id, projectKey: projectKey);
    }
    public void Invalidate(IReadOnlyCollection<string>? paths)
    {
        foreach (var scanner in scanners.Values)
        {
            if (paths is null) scanner.InvalidateCachedSources();
            else foreach (var path in paths) scanner.InvalidateCachedSource(path);
        }
    }
    public async Task RefreshAsync(bool userInitiated = false)
    {
        if (disposed) return;
        if (!await refreshLock.WaitAsync(0).ConfigureAwait(true)) { pendingRefresh = true; pendingUserRefresh |= userInitiated; return; }
        try
        {
            IsRefreshing = true; Changed();
            var enabled = settings.Current.EnabledProviders.ToArray();
            if (Synthetic)
            {
                Usage["codex"] = new(new(123456, 24000, 56000), new(340000, 70000, 120000), new(1100000, 250000, 700000), new(4800000, 1000000, 1200000), DataQuality.Exact, DateTimeOffset.Now);
                Readings["codex"] = new("codex", ReadingState.Ready, [new("session", "5 hours", 32, DateTimeOffset.Now.AddHours(2), 300), new("weekly", "Weekly", 66, DateTimeOffset.Now.AddDays(3), 10080)], DateTimeOffset.Now, Plan: "Synthetic preview");
                Status = "Synthetic Windows UI verification"; return;
            }
            foreach (var id in enabled.Where(scanners.ContainsKey))
            {
                var generation = Generation(id);
                var scan = await scanners[id].ScanAsync(settings.Current.WeekStart, lifetime.Token).ConfigureAwait(true);
                if (generation != Generation(id)) continue;
                var events = await Task.Run(() => repository.Merge(id, scan.Events), lifetime.Token).ConfigureAwait(true);
                if (generation != Generation(id)) continue;
                pendingRefresh |= scan.HasMoreWork;
                Events[id] = events;
                Usage[id] = UsageScanner.Aggregate(events, DateTimeOffset.Now, settings.Current.WeekStart, scan.Snapshot.Quality == DataQuality.Partial || scan.HasMoreWork);
                Status = scan.StatusMessage;
            }
            if (userInitiated || DateTimeOffset.Now - lastRemoteRefresh >= TimeSpan.FromSeconds(60))
            {
                lastRemoteRefresh = DateTimeOffset.Now;
                foreach (var id in enabled)
                {
                    var generation = Generation(id);
                    var reading = await connections.FetchAsync(id, settings.Current, lifetime.Token).ConfigureAwait(true);
                    if (generation != Generation(id) || !settings.Current.EnabledProviders.Contains(id, StringComparer.Ordinal)) continue;
                    if (reading.State == ReadingState.Error && Readings.TryGetValue(id, out var previous) && previous.Windows.Count > 0)
                        reading = previous with { State = ReadingState.Stale, Message = reading.Message };
                    Readings[id] = reading; ReadingUpdated?.Invoke(reading);
                }
            }
            foreach (var id in Readings.Keys.ToArray()) Readings[id] = Readings[id].Evaluated(DateTimeOffset.Now);
            var previousSessions = Sessions;
            Sessions = await Task.Run(() => ReadSessions(enabled), lifetime.Token).ConfigureAwait(true);
            if (settings.Current.CompletionSound && Sessions.Any(x => x.State == "idle" && previousSessions.Any(old => old.Id == x.Id && old.State == "busy")))
                System.Media.SystemSounds.Asterisk.Play();
            CompanionFile.Write(new CompanionSnapshot(1, DateTimeOffset.Now, enabled.Select(id => new CompanionProvider(id,
                ProviderCatalog.Find(id)!.Name, true, Usage.TryGetValue(id, out var usage) ? CompanionFile.Local(usage, DateTimeOffset.Now) : null,
                Readings.GetValueOrDefault(id) ?? new(id, ReadingState.Loading, []))).ToArray()));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { Status = "Local history could not be refreshed. Your existing reading is retained."; }
        finally
        {
            IsRefreshing = false; refreshLock.Release(); Changed();
            if (pendingRefresh && !disposed)
            {
                var refreshRemote = pendingUserRefresh; pendingRefresh = false; pendingUserRefresh = false;
                await Task.Yield(); _ = RefreshAsync(refreshRemote);
            }
        }
    }
    private static List<SessionActivity> ReadSessions(string[] enabled)
    {
        var sessions = new List<SessionActivity>();
        if (enabled.Contains("claude", StringComparer.Ordinal)) sessions.AddRange(ClaudeSessions.Read());
        if (!enabled.Contains("codex", StringComparer.Ordinal)) return sessions;
        var root = UsageScanner.DefaultRoots()[0];
        if (!Directory.Exists(root)) return sessions;
        var files = Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 32 })
            .Take(50000).OrderByDescending(File.GetLastWriteTimeUtc).Take(64);
        foreach (var path in files) if (ActivityReader.ReadCodex(path, DateTimeOffset.Now) is { } activity) sessions.Add(activity);
        return sessions;
    }
    public void Clear(string id)
    {
        generations[id] = Generation(id) + 1; repository.Clear(id, DateTimeOffset.Now); scanners[id].InvalidateCachedSources(); Usage.Remove(id); Events.Remove(id); Changed();
    }
    public void InvalidateAccount(string id) { generations[id] = Generation(id) + 1; Readings.Remove(id); lastRemoteRefresh = default; Changed(); }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() { disposed = true; lifetime.Cancel(); connections.Dispose(); }
}
