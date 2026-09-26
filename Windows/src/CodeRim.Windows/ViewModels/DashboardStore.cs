using System.ComponentModel;
using System.IO;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.ViewModels;

internal sealed partial class DashboardStore : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettingsStore settings;
    private readonly ProviderConnections connections;
    private readonly UsageRepository repository;
    private readonly byte[] projectKey;
    private readonly Dictionary<string, UsageScanner> scanners = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly SemaphoreSlim remoteSlots = new(4, 4);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Task> remoteTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> lastRefresh = new(StringComparer.Ordinal);
    private bool pendingRefresh;
    private bool localRefreshing;
    private Task? localScanTask;
    private Task? activityTask;
    private bool disposed;
    private readonly Dictionary<string, string?> scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> generations = new(StringComparer.Ordinal);
    private int Generation(string id) => generations.GetValueOrDefault(id);
    public Dictionary<string, UsageSnapshot> Usage { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<UsageEvent>> Events { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<SessionDetails>> SessionDetails { get; } = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, CodexAnalyticsLabel> CodexAnalyticsLabels { get; set; } = new Dictionary<string, CodexAnalyticsLabel>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProviderReading> Readings { get; } = new(StringComparer.Ordinal);
    public HashSet<string> RefreshingProviders { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<SessionActivity> Sessions { get; private set; } = [];
    public bool IsRefreshing => localRefreshing || RebuildingProviders.Count > 0 || RefreshingProviders.Count > 0;
    public string Status { get; private set; } = "Reading local usage…";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<ProviderReading>? ReadingUpdated;
    public event Action<SessionActivity>? SessionAttentionRequested;
    public bool Synthetic { get; }
    private readonly ClaudeIntegrationHost claudeHost;
    internal ClaudeIntegration Claude => claudeHost.Integration;
    internal bool ClaudeAvailable => Claude.Available;
    internal string[] AvailableUsageProviders => ClaudeAvailable ? ["codex", "claude"] : ["codex"];
    private string[] ReadableProviders => settings.Current.EnabledProviders.Where(id => id is not ("codex" or "claude"))
        .Concat(AvailableUsageProviders).Distinct(StringComparer.Ordinal).ToArray();
    private bool CanReadProvider(string id) => id == "codex" || (id == "claude" ? ClaudeAvailable : settings.Current.EnabledProviders.Contains(id, StringComparer.Ordinal));
    private string? claudeAvailabilityKey;
    internal void StartClaudePolling() => claudeHost.Start();
    private void ClaudeChanged()
    {
        if (disposed) return;
        var next = ClaudeAvailable ? Claude.Account?.Id : null;
        if (next != claudeAvailabilityKey)
        {
            claudeAvailabilityKey = next; InvalidateAccount("claude");
            if (next is not null) { _ = RefreshLocalAsync(); _ = RefreshProviderAsync("claude"); }
        }
        Changed();
    }
    internal ProfileUsageStore ProfileHistory { get; }
    public DashboardStore(AppSettingsStore settings, CredentialVault vault, bool synthetic = false, ProviderConnections? providerConnections = null, ProfileUsageStore? profileHistory = null, UsageRepository? usageRepository = null, ClaudeIntegration? claudeIntegration = null)
    {
        this.settings = settings; Synthetic = synthetic; connections = providerConnections ?? new ProviderConnections(vault);
        ProfileHistory = profileHistory ?? new ProfileUsageStore(settings, synthetic || providerConnections is not null); ProfileHistory.Changed += Changed;
        repository = usageRepository ?? new UsageRepository(Path.Combine(CompanionFile.DataDirectory, "usage.sqlite"));
        var keyPath = Path.Combine(CompanionFile.DataDirectory, "project-key.bin");
        if (!File.Exists(keyPath)) File.WriteAllBytes(keyPath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        projectKey = File.ReadAllBytes(keyPath);
        if (projectKey.Length != 32) throw new InvalidDataException("Project identity key is invalid.");
        foreach (var id in new[] { "codex", "claude" })
        {
            scanners[id] = new UsageScanner(id, projectKey: projectKey);
            if (!synthetic)
            {
                var history = repository.Read(id);
                Events[id] = history; SessionDetails[id] = repository.ReadSessionDetails(id);
                Usage[id] = LocalTokenPresentation.CompletedRead(UsageScanner.Aggregate(history, DateTimeOffset.Now, settings.Current.WeekStart, true));
            }
        }
        try
        {
            if (File.Exists(CompanionFile.SnapshotPath))
                foreach (var provider in CompanionFile.Read().Providers)
                {
                    var scope = connections.Scope(provider.Id); scopes[provider.Id] = scope;
                    if (scope is not null && connections.CanCache(provider.Id) && provider.AccountScope == scope)
                        Readings[provider.Id] = provider.RestartLimits with { State = provider.RestartLimits.Windows.Count > 0 ? ReadingState.Stale : provider.RestartLimits.State };
                }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
        claudeHost = new ClaudeIntegrationHost(settings, synthetic, claudeIntegration);
        Claude.Changed += ClaudeChanged;
        settings.SettingsChanged += SessionTokenSettingsChanged;
        settings.SettingsChanged += NativeAccountsSettingsChanged;
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
        foreach (var id in ReadableProviders) EnsureScope(id);
        var claude = userInitiated || Synthetic ? Claude.RefreshAsync() : Task.CompletedTask;
        var profile = ProfileHistory.RefreshAsync(userInitiated);
        var activity = RefreshActivityAsync();
        var local = RefreshLocalAsync();
        var remote = ReadableProviders.Where(id => userInitiated
            || DateTimeOffset.Now - lastRefresh.GetValueOrDefault(id) >= TimeSpan.FromSeconds(60))
            .Select(RefreshProviderAsync).ToArray();
        await Task.WhenAll(remote.Append(local).Append(activity).Append(profile).Append(claude)).ConfigureAwait(true);
    }
    internal Task WaitForLocalIdleAsync() => localScanTask ?? Task.CompletedTask;
    internal Task RefreshLocalAsync()
    {
        if (disposed) return Task.CompletedTask;
        if (localScanTask is { } running) { pendingRefresh = true; return running; }
        return localScanTask = RunLocalRefreshAsync();
    }
    private async Task RunLocalRefreshAsync()
    {
        await Task.Yield();
        if (!await refreshLock.WaitAsync(0).ConfigureAwait(true)) { pendingRefresh = true; localScanTask = null; return; }
        try
        {
            localRefreshing = true; Changed();
            var passes = 0;
            do
            {
                pendingRefresh = false;
                var enabled = ReadableProviders;
                if (Synthetic) { SeedPreview(); return; }
                foreach (var id in enabled.Where(scanners.ContainsKey))
                {
                    var generation = Generation(id);
                    try
                    {
                        var scan = await scanners[id].ScanAsync(settings.Current.WeekStart, lifetime.Token).ConfigureAwait(true);
                        if (generation != Generation(id)) continue;
                        var imported = await Task.Run(() =>
                        {
                            var retained = repository.Merge(id, scan.Events, scan.Sessions);
                            return (Events: retained, Sessions: repository.ReadSessionDetails(id), Statistics: repository.Statistics(id),
                                Labels: id == "codex" ? ReadAnalyticsLabels(retained, lifetime.Token) : null);
                        }, lifetime.Token).ConfigureAwait(true);
                        if (generation != Generation(id)) continue;
                        pendingRefresh |= scan.HasMoreWork;
                        var events = imported.Events;
                        Events[id] = events; SessionDetails[id] = imported.Sessions;
                        if (imported.Labels is { } labels) CodexAnalyticsLabels = labels;
                        SourceCounts[id] = scan.SourceCount;
                        DataStatistics[id] = imported.Statistics;
                        Usage[id] = LocalTokenPresentation.CompletedRead(UsageScanner.Aggregate(events, DateTimeOffset.Now, settings.Current.WeekStart, scan.Snapshot.Quality == DataQuality.Partial || scan.HasMoreWork));
                        Status = scan.StatusMessage;
                        if (settings.Current.DebugLogging) AppDiagnostics.Record(id, scan.Snapshot.Quality.ToString(), events.Count);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                        or System.Security.SecurityException or Microsoft.Data.Sqlite.SqliteException)
                    {
                        if (generation != Generation(id)) continue;
                        Usage[id] = LocalTokenPresentation.AfterFailure(Usage.GetValueOrDefault(id));
                        Status = "Local history could not be refreshed. Your existing reading is retained.";
                    }
                    Changed();
                }
                Persist();
                // A continuously appended source must not keep a refresh alive
                // forever. Watcher/timer/manual refresh will collect the next tail.
                if (++passes >= 2) break;
                if (pendingRefresh) await Task.Delay(250, lifetime.Token).ConfigureAwait(true);
            } while (pendingRefresh && !disposed);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { Status = "Local history could not be refreshed. Your existing reading is retained."; }
        finally { localRefreshing = false; localScanTask = null; refreshLock.Release(); Changed(); }
    }
    public Task WaitForProviderIdleAsync(string id) => remoteTasks.GetValueOrDefault(id) ?? Task.CompletedTask;
    private static bool PausedForAccount(string id) => id is "codex" or "claude" && SavedAccounts.OperationInProgress;
    public Task RefreshProviderAsync(string id)
    {
        if (disposed || PausedForAccount(id) || !CanReadProvider(id)) return Task.CompletedTask;
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
            await RefreshProviderAccountAsync(id).ConfigureAwait(true);
            if (disposed || !CanReadProvider(id)) return;
            await remoteSlots.WaitAsync(lifetime.Token).ConfigureAwait(true); entered = true;
            if (disposed || PausedForAccount(id) || !CanReadProvider(id)) return;
            EnsureScope(id); generation = Generation(id); requestScope = scopes.GetValueOrDefault(id);
            // EnsureScope can invalidate an earlier summary after a credential
            // switch. Capture the selected display owner before sending HTTP.
            if (!accountSummaries.ContainsKey(id)) await RefreshProviderAccountAsync(id).ConfigureAwait(true);
            if (disposed || PausedForAccount(id) || !CanReadProvider(id) || generation != Generation(id)
                || requestScope != scopes.GetValueOrDefault(id)) return;
            var accountVersion = accountSummaries.GetValueOrDefault(id)?.Version;
            lastRefresh[id] = DateTimeOffset.Now;
            if (Synthetic) { SeedPreview(); return; }
            var result = await connections.FetchForStoreAsync(id, settings.Current, requestScope, lifetime.Token).ConfigureAwait(true);
            await RefreshProviderAccountAsync(id).ConfigureAwait(true);
            // No await between the request/generation checks, exact rotation-version
            // acceptance and the normal current-scope guard on this owning UI context.
            if (generation == Generation(id) && requestScope == scopes.GetValueOrDefault(id)
                && connections.TryAcceptScopeRotation(id, requestScope, result.Rotation, out var rotatedScope))
            { scopes[id] = rotatedScope; requestScope = rotatedScope; }
            EnsureScope(id);
            var reading = result.Reading;
            if (disposed || generation != Generation(id) || requestScope != scopes.GetValueOrDefault(id) || !CanReadProvider(id)
                || accountVersion != accountSummaries.GetValueOrDefault(id)?.Version) return;
            Readings[id] = ReadingRetention.Merge(reading, requestScope is null || !connections.CanCache(id) ? null : Readings.GetValueOrDefault(id));
            if (id is "commandcode" or "glm" && accountVersion is not null && reading.State is ReadingState.Ready or ReadingState.Partial)
            {
                if (reading.Plan is { } plan) accountPlans[id] = plan;
                // GLM's latest successful response owns its optional level.
                // Only a failed fetch may retain the last known plan.
                else if (id == "glm") accountPlans.Remove(id);
            }
            if (settings.Current.DebugLogging) AppDiagnostics.Record(id, Readings[id].State.ToString(), Readings[id].Windows.Count);
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
        CompanionFile.Write(CreateCompanionSnapshot(DateTimeOffset.Now));
    }
    internal CompanionSnapshot CreateCompanionSnapshot(DateTimeOffset now) => new(1, now,
        settings.Current.EnabledProviders.Select(id =>
        {
            var raw = Readings.GetValueOrDefault(id) ?? new(id, ReadingState.Loading, []);
            var display = raw;
            if (id is "codex" or "claude")
            {
                // Capture ownership and raw plan together; a newly selected login must
                // never be paired with the previous account's cached quota or plan.
                var account = AccountDisplay(id);
                var owned = account.Reading ?? new(id, ReadingState.NeedsAuth, []);
                if (id == "codex" && !settings.Current.AccountLimitsEnabled)
                    owned = owned with { State = ReadingState.Disabled, Windows = [], Message = "Account limits are turned off in Settings." };
                // Additional-window visibility is a Usage UI preference on Mac;
                // the companion retains every reported bucket before Pro filtering.
                display = CompanionLimitPresentation.ForDisplay(owned, account.RawPlan);
            }
            return new CompanionProvider(id, ProviderCatalog.Find(id)!.Name, true,
                Usage.TryGetValue(id, out var usage) ? CompanionFile.Local(usage, now) : null,
                // Account display metadata stays in the owner-scoped restart cache,
                // not the public quota projection used by companion consumers.
                display with { Account = null }, scopes.GetValueOrDefault(id),
                id is "codex" or "claude" || raw.Account is not null ? raw : null);
        }).ToArray());
    private void SeedPreview()
    {
        Events["codex"] = [new("preview-event", DateTimeOffset.Now.AddMinutes(-2), new(123456, 24000, 56000, 0),
            "gpt-5.6-sol", "CodeRim", "preview-session", "codex", "preview-project"),
            new("preview-child-event", DateTimeOffset.Now.AddDays(-1), new(100, 0, 20, 0),
                "gpt-5.6-sol", "CodeRim", "preview-child", "codex", "preview-project")];
        SessionDetails["codex"] = [new("preview-session", null, [new("preview-image", DateTimeOffset.Now.AddMinutes(-3), 2)]),
            new("preview-child", "preview-session", [])];
        Events["claude"] = [new("preview-claude", DateTimeOffset.Now.AddMinutes(-2), new(12000, 3000, 4000),
            "claude-sonnet-4-6", "CodeRim", "preview-claude-session", "claude", "preview-project")];
        Usage["claude"] = UsageScanner.Aggregate(Events["claude"], DateTimeOffset.Now, settings.Current.WeekStart, false);
        Usage["codex"] = new(new(123456, 24000, 56000), new(340000, 70000, 120000), new(1100000, 250000, 700000), new(4800000, 1000000, 1200000), DataQuality.Exact, DateTimeOffset.Now);
        foreach (var id in ReadableProviders)
            Readings[id] = new(id, ReadingState.Ready, [new("session", "5 hours", 32, DateTimeOffset.Now.AddHours(2), 300), new("weekly", "Weekly", 66, DateTimeOffset.Now.AddDays(3), 10080)], DateTimeOffset.Now, Plan: "Preview account");
        if (Readings.TryGetValue("codex", out var codex))
            Readings["codex"] = codex with { Windows = [..codex.Windows,
                new("review", "Code review", 14, DateTimeOffset.Now.AddDays(2)),
                new("rate-limit-reset-credits", "Reset credits", null, RemainingCount: 2, Unit: "resets", DisplayValue: "2 resets remaining")] };
        Status = "Synthetic Windows UI verification";
    }
    public Task RefreshActivityAsync()
    {
        if (disposed || Synthetic) return Task.CompletedTask;
        return activityTask ??= ReadActivityAsync();
    }
    private async Task ReadActivityAsync()
    {
        await Task.Yield();
        var enabled = ReadableProviders;
        var versions = enabled.ToDictionary(id => id, Generation, StringComparer.Ordinal);
        try
        {
            var includeUnknown = settings.Current.ShowUnknownSessions;
            var current = await Task.Run(() => ReadSessions(enabled, includeUnknown, lifetime.Token), lifetime.Token).ConfigureAwait(true);
            if (disposed) return;
            var valid = current.Where(item => CanReadProvider(item.Provider)
                && versions.GetValueOrDefault(item.Provider, -1) == Generation(item.Provider)).ToArray();
            UpdateSessionActivity(valid);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            if (settings.Current.DebugLogging) AppDiagnostics.Record("activity", "LocalReadUnavailable", 0);
        }
        finally { activityTask = null; }
    }
    internal void UpdateSessionActivity(IReadOnlyList<SessionActivity> current)
    {
        var previous = Sessions;
        current = current.Select(item => previous.FirstOrDefault(old => item.Provider == "claude" && old.Id == item.Id && old.Provider == item.Provider
            && old.State == item.State && old.ProcessStartedAt == item.ProcessStartedAt) is { } old
                ? item with { Since = old.Since } : item).ToArray();
        Sessions = current;
        var finished = current.Any(x => x.State == "idle" && previous.Any(old => old.Id == x.Id && old.Provider == x.Provider && old.State == "busy"));
        var blocked = current.Any(x => x.State == "waiting" && previous.Any(old => old.Id == x.Id && old.Provider == x.Provider && old.State == "busy"));
        if (settings.Current.CompletionSound)
        {
            if (finished) SessionChime.Play(settings.Current.FinishedSound);
            else if (blocked) SessionChime.Play(settings.Current.BlockedSound);
        }
        if (finished || blocked)
            SessionAttentionRequested?.Invoke(current.Where(x => x.State is "idle" or "waiting"
                && previous.Any(old => old.Id == x.Id && old.Provider == x.Provider && old.State == "busy"))
                .OrderByDescending(x => x.Since).First());
        // Publish at the mutation boundary. Every caller, including updates that
        // only change a task title/state and reuse the token cache, needs this.
        if (!previous.SequenceEqual(Sessions)) Changed();
    }

    private static List<SessionActivity> ReadSessions(string[] enabled, bool includeUnknown, CancellationToken cancellationToken)
    {
        var sessions = new List<SessionActivity>();
        if (enabled.Contains("claude", StringComparer.Ordinal)) sessions.AddRange(ClaudeSessions.Read(cancellationToken));
        if (!enabled.Contains("codex", StringComparer.Ordinal)) return sessions;
        var root = UsageScanner.DefaultRoots()[0];
        if (includeUnknown) sessions.AddRange(CodexRemoteActivity.Read(Path.Combine(Path.GetDirectoryName(root)!, "sqlite", "codex-dev.db"), DateTimeOffset.Now, cancellationToken));
        if (!Directory.Exists(root)) return sessions;
        var files = Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 32 })
            .Take(50000).OrderByDescending(File.GetLastWriteTimeUtc).Take(64);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.Now - File.GetLastWriteTimeUtc(path) <= TimeSpan.FromHours(6)
                && ActivityReader.ReadCodex(path, DateTimeOffset.Now, cancellationToken) is { } activity) sessions.Add(activity);
        }
        return CodexActivityCatalogue.Enrich(sessions, Path.Combine(Path.GetDirectoryName(root)!, "state_5.sqlite"),
            Path.Combine(Path.GetDirectoryName(root)!, "sqlite", "codex-dev.db"), cancellationToken).ToList();
    }
    private static IReadOnlyDictionary<string, CodexAnalyticsLabel> ReadAnalyticsLabels(IReadOnlyList<UsageEvent> events, CancellationToken token)
    {
        var root = Path.GetDirectoryName(UsageScanner.DefaultRoots()[0])!;
        return CodexActivityCatalogue.ReadAnalyticsLabels(events.Select(x => x.SessionId).Distinct(StringComparer.Ordinal).ToArray(),
            Path.Combine(root, "state_5.sqlite"), Path.Combine(root, "sqlite", "codex-dev.db"), token);
    }
    private void EnsureScope(string id)
    {
        if (Synthetic) return;
        var scope = connections.Scope(id);
        if (scopes.TryGetValue(id, out var previous) && previous != scope) InvalidateAccount(id);
        scopes[id] = scope;
        if (scope is null) Readings.Remove(id);
    }
    public void InvalidateAccount(string id)
    {
        generations[id] = Generation(id) + 1; Readings.Remove(id); lastRefresh.Remove(id);
        accountSummaries.Remove(id); accountPlans.Remove(id);
        try { Persist(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        { Status = "The latest reading could not be saved. Retry refresh."; }
        // In-memory ownership invalidation must complete even on a read-only disk.
        Changed();
    }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() { disposed = true; Claude.Changed -= ClaudeChanged; claudeHost.Dispose(); ProfileHistory.Changed -= Changed; ProfileHistory.Dispose(); settings.SettingsChanged -= SessionTokenSettingsChanged; settings.SettingsChanged -= NativeAccountsSettingsChanged; lifetime.Cancel(); CancelSessionTokenReads(); sessionTokens.Clear(); accountSummaries.Clear(); accountPlans.Clear(); connections.Dispose(); }
}
