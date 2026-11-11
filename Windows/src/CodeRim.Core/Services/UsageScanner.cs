using System.Globalization;
using System.Collections.Concurrent;
using System.Diagnostics;
using CodeRim.Core.Domain;
using CodeRim.Core.Parsing;

namespace CodeRim.Core.Services;

public sealed class UsageScanner
{
    public const int MaximumSourceCount = 50_000;
    public const int MaximumEventCount = 1_000_000;
    public const int MaximumEventsPerSource = 500_000;
    public const long MaximumSourceBytes = 512L * 1_024 * 1_024;
    public const long MaximumBytesPerScan = 4L * 1_024 * 1_024 * 1_024;
    public const long MaximumTotalSourceBytes = 32L * 1_024 * 1_024 * 1_024;
    /// A work budget per scan, not a hard deadline: it includes discovery, no new source starts after it,
    /// and MaximumSourceBytes bounds how far the source in progress can run past it.
    public static readonly TimeSpan MaximumScanDuration = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<string> roots;
    private string provider = "codex";
    private byte[]? projectKey;
    private readonly bool requireCompleteSources;
    private readonly Dictionary<string, CachedFile> cache = new(StringComparer.OrdinalIgnoreCase);
    private static long contentVersions;
    private long contentVersion = Interlocked.Increment(ref contentVersions);
    private readonly ConcurrentQueue<string> invalidatedPaths = new();
    private readonly int maximumSourceCount;
    private readonly int maximumEventCount;
    private readonly int maximumEventsPerSource;
    private readonly long maximumSourceBytes;
    private readonly long maximumBytesPerScan;
    private readonly long maximumTotalSourceBytes;
    private readonly TimeSpan maximumScanDuration;
    private readonly Action<string>? sourceParsed;
    private int invalidationGeneration;
    private int appliedInvalidationGeneration;

    public UsageScanner(IEnumerable<string>? roots = null)
        : this(
            roots,
            MaximumSourceCount,
            MaximumEventCount,
            MaximumEventsPerSource,
            MaximumSourceBytes,
            MaximumBytesPerScan,
            MaximumScanDuration,
            MaximumTotalSourceBytes)
    {
    }

    public UsageScanner(string provider, IEnumerable<string>? roots = null, byte[]? projectKey = null, bool requireCompleteSources = false) : this(roots ?? DefaultRoots(provider))
    {
        if (provider is not ("codex" or "claude")) throw new ArgumentException("Unknown local provider", nameof(provider));
        this.provider = provider;
        this.projectKey = projectKey;
        this.requireCompleteSources = requireCompleteSources;
    }

    internal UsageScanner(
        IEnumerable<string>? roots,
        int maximumSourceCount,
        int maximumEventCount,
        int maximumEventsPerSource,
        long maximumSourceBytes,
        long maximumBytesPerScan,
        TimeSpan maximumScanDuration,
        long maximumTotalSourceBytes = MaximumTotalSourceBytes,
        Action<string>? sourceParsed = null)
    {
        this.roots = (roots ?? DefaultRoots()).Select(Path.GetFullPath).ToArray();
        this.sourceParsed = sourceParsed;
        this.maximumSourceCount = Math.Max(1, maximumSourceCount);
        this.maximumEventCount = Math.Max(1, maximumEventCount);
        this.maximumEventsPerSource = Math.Max(1, maximumEventsPerSource);
        this.maximumSourceBytes = Math.Max(CodexJsonlParser.MaximumLineBytes + 1L, maximumSourceBytes);
        this.maximumBytesPerScan = Math.Max(this.maximumSourceBytes, maximumBytesPerScan);
        this.maximumTotalSourceBytes = Math.Max(this.maximumSourceBytes, maximumTotalSourceBytes);
        this.maximumScanDuration = maximumScanDuration > TimeSpan.Zero
            ? maximumScanDuration
            : TimeSpan.FromSeconds(1);
    }

    public static IReadOnlyList<string> DefaultRoots(string provider = "codex")
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (provider == "claude") return [Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(profile, ".claude"), "projects")];
        var codex = CodexHomeDirectory.Resolve(Environment.GetEnvironmentVariable("CODEX_HOME"), profile);
        return
        [
            Path.Combine(codex, "sessions"),
            Path.Combine(codex, "archived_sessions")
        ];
    }

    public Task<ScanResult> ScanAsync(
        WeekStart weekStart,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(weekStart, DateTimeOffset.Now, cancellationToken), cancellationToken);

    internal Task<ScanResult> ScanAsync(
        WeekStart weekStart,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(weekStart, now, cancellationToken), cancellationToken);

    public void InvalidateCachedSources() => Interlocked.Increment(ref invalidationGeneration);

    public void InvalidateCachedSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        invalidatedPaths.Enqueue(Path.GetFullPath(path));
    }

    internal ScanResult Scan(
        WeekStart weekStart,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var cacheAtStart = cache.ToArray();
        var currentInvalidationGeneration = Volatile.Read(ref invalidationGeneration);
        if (currentInvalidationGeneration != appliedInvalidationGeneration)
        {
            cache.Clear();
            while (invalidatedPaths.TryDequeue(out _))
            {
            }
            appliedInvalidationGeneration = currentInvalidationGeneration;
        }
        else
        {
            while (invalidatedPaths.TryDequeue(out var invalidatedPath))
            {
                cache.Remove(invalidatedPath);
            }
        }

        var scanStartedAt = Stopwatch.GetTimestamp();
        var discovery = UsageSourceDiscovery.Discover(roots, requireCompleteSources, maximumSourceCount, maximumSourceBytes, maximumTotalSourceBytes, cancellationToken);
        var sources = discovery.Sources;
        var activePaths = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);
        var partial = discovery.ResourceLimitReached;
        // Gaps no record marks: something not read at all. A file read in part marks its records instead
        // (below), so only the ranges holding them are partial.
        var gaps = partial;
        var hasMoreWork = false;
        var resourceLimitReached = discovery.ResourceLimitReached;
        var scannedBytes = 0L;

        foreach (var stale in cache.Keys.Where(path => !activePaths.Contains(path)).ToArray())
        {
            cache.Remove(stale);
        }
        var cachedEventCount = cache.Values.Sum(value => value.Events.Count + (value.Details?.Attachments.Count ?? 0));

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var before = FileStamp.Read(source);
                if (cache.TryGetValue(source, out var cached) && cached.Stamp == before)
                {
                    partial |= cached.Partial; gaps |= cached.Partial && cached.Events.Count == 0;
                    continue;
                }

                var previousEventCount = (cached?.Events.Count ?? 0) + (cached?.Details?.Attachments.Count ?? 0);
                var retainedEventCount = cachedEventCount - previousEventCount;
                if (before.Length > maximumSourceBytes)
                {
                    cache.Remove(source);
                    cachedEventCount = retainedEventCount;
                    partial = gaps = true;
                    resourceLimitReached = true;
                    continue;
                }
                if (scannedBytes > 0
                    && (scannedBytes + before.Length > maximumBytesPerScan
                        || Stopwatch.GetElapsedTime(scanStartedAt) >= maximumScanDuration))
                {
                    hasMoreWork = true;
                    break;
                }
                var remainingEventCapacity = maximumEventCount - retainedEventCount;
                if (remainingEventCapacity <= 0)
                {
                    cache.Remove(source);
                    cachedEventCount = retainedEventCount;
                    partial = gaps = true;
                    resourceLimitReached = true;
                    break;
                }

                var parsed = UsageSourceReader.Read(
                    source,
                    Math.Min(maximumEventsPerSource, remainingEventCapacity),
                    before.Length,
                    provider, projectKey, requireCompleteSources, cancellationToken);
                scannedBytes += before.Length;
                if (parsed.ResourceLimitReached)
                {
                    cache.Remove(source);
                    cachedEventCount = retainedEventCount;
                    partial = gaps = true;
                    resourceLimitReached = true;
                    break;
                }

                sourceParsed?.Invoke(source);
                var after = FileStamp.Read(source);
                var changed = before != after;
                var sameFile = parsed.Identity == SourceFileIdentity.TryRead(source);
                if (changed)
                {
                    // A writer may append while we parse a frozen prefix. Keep a
                    // verified prefix useful, and retry its tail on the next scan.
                    hasMoreWork = true;
                    partial = gaps = true;
                    var canVerify = after.Length >= before.Length
                        && after.CreationTimeTicks == before.CreationTimeTicks
                        && scannedBytes + before.Length <= maximumBytesPerScan;
                    if (canVerify && sameFile)
                    {
                        scannedBytes += before.Length;
                        sameFile = UsageSourceReader.VerifyPrefix(source, before.Length, parsed, cancellationToken);
                    }
                    else sameFile = false;
                }
                if (!sameFile)
                {
                    cache.Remove(source);
                    cachedEventCount = retainedEventCount;
                    partial = gaps = true;
                    hasMoreWork = true;
                    continue;
                }


                // A file read only in part (a baseline that could not be confirmed, a missing or decreasing counter,
                // a malformed accounting row) marks what it did yield. The mark is stored with each record, so the
                // periods they fall in stay partial after the file is gone; the file's own flag lasts only as long
                // as the file.
                var yielded = parsed.UncountedUsage ? parsed.Events.Select(e => e.Repaired ? e : e with { Repaired = true }).ToList() : parsed.Events;
                cache[source] = new CachedFile(before, yielded, parsed.Partial, parsed.Details);
                cachedEventCount = retainedEventCount + parsed.Events.Count + (parsed.Details?.Attachments.Count ?? 0);
                partial |= parsed.Partial; gaps |= parsed.Partial && parsed.Events.Count == 0;
            }
            catch (Exception error) when (error is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException)
            {
                cache.Remove(source);
                cachedEventCount = cache.Values.Sum(value => value.Events.Count + (value.Details?.Attachments.Count ?? 0));
                partial = gaps = true;
            }
        }

        var merged = new Dictionary<string, UsageEvent>(StringComparer.Ordinal);
        foreach (var cached in cache.Values)
        {
            foreach (var usageEvent in cached.Events)
            {
                if (provider == "claude")
                {
                    merged[usageEvent.EventKey] = merged.TryGetValue(usageEvent.EventKey, out var claudeExisting)
                        ? ClaudeJsonlParser.Merge(claudeExisting, usageEvent) : usageEvent;
                    continue;
                }
                if (merged.TryGetValue(usageEvent.EventKey, out var existing))
                {
                    existing = existing with { Repaired = existing.Repaired || usageEvent.Repaired };
                    if (existing.Usage.InputTokens == usageEvent.Usage.InputTokens
                        && existing.Usage.CachedInputTokens == usageEvent.Usage.CachedInputTokens
                        && existing.Usage.OutputTokens == usageEvent.Usage.OutputTokens)
                        existing = existing with {
                            Usage = existing.Usage.CacheWriteInputTokens is null && usageEvent.Usage.CacheWriteInputTokens is not null ? usageEvent.Usage : existing.Usage,
                            PricingContext = usageEvent.PricingContext ?? existing.PricingContext };
                    merged[usageEvent.EventKey] = existing;
                    continue;
                }

                if (merged.Count >= maximumEventCount)
                {
                    partial = gaps = true;
                    resourceLimitReached = true;
                    break;
                }

                merged.Add(usageEvent.EventKey, usageEvent);
            }
        }

        var events = merged.Values.Take(maximumEventCount).ToList();
        if (cacheAtStart.Length != cache.Count
            || cacheAtStart.Any(entry => !cache.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry.Value)))
            contentVersion = Interlocked.Increment(ref contentVersions);
        // Attachments become visible as their time arrives, which changes the stored sessions too.
        var dueAttachments = provider == "claude" ? 0 : cache.Values.Sum(x => x.Details?.Attachments.Count(a => a.OccurredAt <= now) ?? 0);
        var snapshot = UsageAggregation.Aggregate(events, now, weekStart, partial || hasMoreWork, sourcesPartial: gaps || hasMoreWork);
        var status = resourceLimitReached
            ? "Local session data limit reached"
            : hasMoreWork
                ? "Importing local history…"
                : snapshot.Quality switch
                {
                    DataQuality.Exact => snapshot.UpdatedAt is null ? "No Codex usage found" : "Updated just now",
                    DataQuality.Partial => snapshot.UpdatedAt is null ? "No Codex usage found" : "Updated just now",
                    DataQuality.Unavailable => sources.Count == 0 ? "Codex sessions not found" : "No Codex usage found",
                    _ => "Unable to read local usage"
                };
        return new ScanResult(snapshot, sources.Count, status.Replace("Codex", provider == "claude" ? "Claude Code" : "Codex", StringComparison.Ordinal), hasMoreWork) { Events = events,
            ContentVersion = contentVersion.ToString(CultureInfo.InvariantCulture) + ":" + dueAttachments.ToString(CultureInfo.InvariantCulture),
            Sessions = provider == "claude" ? events.GroupBy(x => x.SessionId, StringComparer.Ordinal).Select(group =>
            {
                var named = group.Where(x => x.ProjectId != "unknown").OrderByDescending(x => x.OccurredAt)
                    .ThenBy(x => x.Project, StringComparer.Ordinal).FirstOrDefault();
                return new SessionDetails(group.Key, group.Select(x => x.ImportParentSessionId).FirstOrDefault(x => x is not null), []) { StartedAt = group.Min(x => x.OccurredAt),
                    ProjectName = named?.Project, ProjectObservedAt = named?.OccurredAt };
            }).ToArray() : cache.Values.Where(x => x.Details is not null).Select(x => x.Details! with {
                Attachments = x.Details!.Attachments.Where(a => a.OccurredAt <= now).ToArray() }).ToArray() };
    }

    private sealed record CachedFile(FileStamp Stamp, IReadOnlyList<UsageEvent> Events, bool Partial, SessionDetails? Details);
    private readonly record struct FileStamp(long Length, long LastWriteTicks, long CreationTimeTicks)
    {
        public static FileStamp Read(string path)
        {
            var info = new FileInfo(path);
            return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
        }
    }

}
