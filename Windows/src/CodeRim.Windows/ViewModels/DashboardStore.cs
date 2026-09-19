using System.ComponentModel;
using System.IO;
using System.Text.Json;
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
    private readonly SemaphoreSlim remoteSlots = new(4, 4);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Task> remoteTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> lastRefresh = new(StringComparer.Ordinal);
    private bool pendingRefresh;
    private bool localRefreshing;
    private bool disposed;
    private readonly Dictionary<string, string?> scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> generations = new(StringComparer.Ordinal);
    private int Generation(string id) => generations.GetValueOrDefault(id);
    public Dictionary<string, UsageSnapshot> Usage { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<UsageEvent>> Events { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ProviderReading> Readings { get; } = new(StringComparer.Ordinal);
    public HashSet<string> RefreshingProviders { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<SessionActivity> Sessions { get; private set; } = [];
    public bool IsRefreshing => localRefreshing || RefreshingProviders.Count > 0;
    public string Status { get; private set; } = "Reading local usage…";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<ProviderReading>? ReadingUpdated;
    public event Action? SessionCompleted;
    public bool Synthetic { get; }
    public DashboardStore(AppSettingsStore settings, CredentialVault vault, bool synthetic = false)
    {
        this.settings = settings; Synthetic = synthetic; connections = new ProviderConnections(vault);
        repository = new UsageRepository(Path.Combine(CompanionFile.DataDirectory, "usage.sqlite"));
        var keyPath = Path.Combine(CompanionFile.DataDirectory, "project-key.bin");
        if (!File.Exists(keyPath)) File.WriteAllBytes(keyPath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var projectKey = File.ReadAllBytes(keyPath);
        if (projectKey.Length != 32) throw new InvalidDataException("Project identity key is invalid.");
        foreach (var id in new[] { "codex", "claude" })
        {
            scanners[id] = new UsageScanner(id, projectKey: projectKey);
            if (!synthetic)
            {
                var history = repository.Read(id);
                Events[id] = history;
                Usage[id] = UsageScanner.Aggregate(history, DateTimeOffset.Now, settings.Current.WeekStart, true);
            }
        }
        try
        {
            if (File.Exists(CompanionFile.SnapshotPath))
                foreach (var provider in CompanionFile.Read().Providers)
                {
                    var scope = connections.Scope(provider.Id); scopes[provider.Id] = scope;
                    if (scope is not null && connections.CanCache(provider.Id) && provider.AccountScope == scope)
                        Readings[provider.Id] = provider.Limits with { State = provider.Limits.Windows.Count > 0 ? ReadingState.Stale : provider.Limits.State };
                }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
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
        if (userInitiated) foreach (var scanner in scanners.Values) scanner.InvalidateCachedSources();
        // Local scans never wait for provider network requests.
        foreach (var id in settings.Current.EnabledProviders) EnsureScope(id);
        var local = RefreshLocalAsync();
        var remote = settings.Current.EnabledProviders.Where(id => userInitiated
            || DateTimeOffset.Now - lastRefresh.GetValueOrDefault(id) >= TimeSpan.FromSeconds(60))
            .Select(RefreshProviderAsync).ToArray();
        await Task.WhenAll(remote.Append(local)).ConfigureAwait(true);
    }
    private async Task RefreshLocalAsync()
    {
        if (!await refreshLock.WaitAsync(0).ConfigureAwait(true)) { pendingRefresh = true; return; }
        try
        {
            localRefreshing = true; Changed();
            do
            {
                pendingRefresh = false;
                var enabled = settings.Current.EnabledProviders.ToArray();
                if (Synthetic) { SeedPreview(); return; }
                var previousSessions = Sessions;
                Sessions = await Task.Run(() => ReadSessions(enabled), lifetime.Token).ConfigureAwait(true);
                if (Sessions.Any(x => x.State == "idle" && previousSessions.Any(old => old.Id == x.Id && old.State == "busy")))
                {
                    if (settings.Current.CompletionSound) System.Media.SystemSounds.Asterisk.Play();
                    SessionCompleted?.Invoke();
                }
                Changed();
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
                    Changed();
                }
                Persist();
                if (pendingRefresh) await Task.Yield();
            } while (pendingRefresh && !disposed);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { Status = "Local history could not be refreshed. Your existing reading is retained."; }
        finally { localRefreshing = false; refreshLock.Release(); Changed(); }
    }
    public Task WaitForProviderIdleAsync(string id) => remoteTasks.GetValueOrDefault(id) ?? Task.CompletedTask;
    private static bool PausedForAccount(string id) => id is "codex" or "claude" && SavedAccounts.OperationInProgress;
    public Task RefreshProviderAsync(string id)
    {
        if (disposed || PausedForAccount(id) || !settings.Current.EnabledProviders.Contains(id, StringComparer.Ordinal)) return Task.CompletedTask;
        EnsureScope(id);
        if (remoteTasks.TryGetValue(id, out var running)) return running;
        var task = FetchProviderAsync(id);
        remoteTasks[id] = task;
        return task;
    }
    private async Task FetchProviderAsync(string id)
    {
        // Yield before registering completion so synchronous synthetic/auth paths cannot leave a stale task.
        await Task.Yield();
        RefreshingProviders.Add(id); Changed();
        var entered = false;
        var generation = Generation(id);
        string? requestScope = null;
        try
        {
            await remoteSlots.WaitAsync(lifetime.Token).ConfigureAwait(true); entered = true;
            if (PausedForAccount(id)) return;
            EnsureScope(id); generation = Generation(id); requestScope = scopes.GetValueOrDefault(id);
            lastRefresh[id] = DateTimeOffset.Now;
            if (Synthetic) { SeedPreview(); return; }
            var reading = await connections.FetchAsync(id, settings.Current, lifetime.Token).ConfigureAwait(true);
            EnsureScope(id);
            if (generation != Generation(id) || requestScope != scopes.GetValueOrDefault(id) || !settings.Current.EnabledProviders.Contains(id, StringComparer.Ordinal)) return;
            Readings[id] = ReadingRetention.Merge(reading, requestScope is null || !connections.CanCache(id) ? null : Readings.GetValueOrDefault(id));
            ReadingUpdated?.Invoke(Readings[id]);
            Persist();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        { Status = "The latest reading could not be saved. Retry refresh."; }
        finally
        {
            if (entered) remoteSlots.Release();
            RefreshingProviders.Remove(id); remoteTasks.Remove(id); Changed();
            if (!disposed && generation != Generation(id)) _ = RefreshProviderAsync(id);
        }
    }
    private void Persist()
    {
        if (disposed || Synthetic) return;
        var now = DateTimeOffset.Now;
        CompanionFile.Write(new CompanionSnapshot(1, now, settings.Current.EnabledProviders.Select(id => new CompanionProvider(id,
            ProviderCatalog.Find(id)!.Name, true, Usage.TryGetValue(id, out var usage) ? CompanionFile.Local(usage, now) : null,
            Readings.GetValueOrDefault(id) ?? new(id, ReadingState.Loading, []), scopes.GetValueOrDefault(id))).ToArray()));
    }
    private void SeedPreview()
    {
        Events["codex"] = [new("preview-event", DateTimeOffset.Now.AddMinutes(-2), new(123456, 24000, 56000),
            "gpt-5.6-sol", "CodeRim", "preview-session", "codex", "preview-project")];
        Events["claude"] = [new("preview-claude", DateTimeOffset.Now.AddMinutes(-2), new(12000, 3000, 4000),
            "claude-sonnet-4-6", "CodeRim", "preview-claude-session", "claude", "preview-project")];
        Usage["claude"] = UsageScanner.Aggregate(Events["claude"], DateTimeOffset.Now, settings.Current.WeekStart, false);
        Usage["codex"] = new(new(123456, 24000, 56000), new(340000, 70000, 120000), new(1100000, 250000, 700000), new(4800000, 1000000, 1200000), DataQuality.Exact, DateTimeOffset.Now);
        foreach (var id in settings.Current.EnabledProviders)
            Readings[id] = new(id, ReadingState.Ready, [new("session", "5 hours", 32, DateTimeOffset.Now.AddHours(2), 300), new("weekly", "Weekly", 66, DateTimeOffset.Now.AddDays(3), 10080)], DateTimeOffset.Now, Plan: "Preview account");
        Status = "Synthetic Windows UI verification";
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
        if (!scanners.TryGetValue(id, out var scanner)) return;
        generations[id] = Generation(id) + 1; repository.Clear(id, DateTimeOffset.Now); scanner.InvalidateCachedSources(); Usage.Remove(id); Events.Remove(id); Persist(); Changed();
    }
    private void EnsureScope(string id)
    {
        if (Synthetic) return;
        var scope = connections.Scope(id);
        if (scopes.TryGetValue(id, out var previous) && previous != scope) InvalidateAccount(id);
        scopes[id] = scope;
        if (scope is null) Readings.Remove(id);
    }
    public void InvalidateAccount(string id) { generations[id] = Generation(id) + 1; Readings.Remove(id); lastRefresh.Remove(id); Persist(); Changed(); }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() { disposed = true; lifetime.Cancel(); connections.Dispose(); }
}
