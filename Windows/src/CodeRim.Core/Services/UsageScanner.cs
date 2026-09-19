using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
    public static readonly TimeSpan MaximumScanDuration = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<string> roots;
    private string provider = "codex";
    private byte[]? projectKey;
    private readonly Dictionary<string, CachedFile> cache = new(StringComparer.OrdinalIgnoreCase);
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

    public UsageScanner(string provider, IEnumerable<string>? roots = null, byte[]? projectKey = null) : this(roots ?? DefaultRoots(provider))
    {
        if (provider is not ("codex" or "claude")) throw new ArgumentException("Unknown local provider", nameof(provider));
        this.provider = provider;
        this.projectKey = projectKey;
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
        var codex = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(profile, ".codex");
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

        var discovery = DiscoverSources(cancellationToken);
        var sources = discovery.Sources;
        var activePaths = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);
        var partial = discovery.ResourceLimitReached;
        var hasMoreWork = false;
        var resourceLimitReached = discovery.ResourceLimitReached;
        var scannedBytes = 0L;
        var scanStartedAt = Stopwatch.GetTimestamp();

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
                    partial |= cached.Partial;
                    continue;
                }

                var previousEventCount = (cached?.Events.Count ?? 0) + (cached?.Details?.Attachments.Count ?? 0);
                var retainedEventCount = cachedEventCount - previousEventCount;
                if (before.Length > maximumSourceBytes)
                {
                    cache.Remove(source);
                    cachedEventCount = retainedEventCount;
                    partial = true;
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
                    partial = true;
                    resourceLimitReached = true;
                    break;
                }

                var parsed = ParseFile(
                    source,
                    Math.Min(maximumEventsPerSource, remainingEventCapacity),
                    before.Length,
                    cancellationToken, provider, projectKey);
                scannedBytes += before.Length;
                if (parsed.ResourceLimitReached)
                {
                    cache.Remove(source);
                    cachedEventCount = retainedEventCount;
                    partial = true;
                    resourceLimitReached = true;
                    break;
                }

                sourceParsed?.Invoke(source);
                var after = FileStamp.Read(source);
                var changed = before != after;
                var sameFile = parsed.Identity == FileIdentity.TryRead(source);
                if (changed)
                {
                    // A writer may append while we parse a frozen prefix. Keep a
                    // verified prefix useful, and retry its tail on the next scan.
                    hasMoreWork = true;
                    partial = true;
                    var canVerify = after.Length >= before.Length
                        && after.CreationTimeTicks == before.CreationTimeTicks
                        && scannedBytes + before.Length <= maximumBytesPerScan;
                    if (canVerify && sameFile)
                    {
                        scannedBytes += before.Length;
                        sameFile = VerifyPrefix(source, before.Length, parsed, cancellationToken);
                    }
                    else sameFile = false;
                }
                if (!sameFile)
                {
                    cache.Remove(source);
                    cachedEventCount = retainedEventCount;
                    partial = true;
                    hasMoreWork = true;
                    continue;
                }


                cache[source] = new CachedFile(before, parsed.Events, parsed.Partial, parsed.Details);
                cachedEventCount = retainedEventCount + parsed.Events.Count + (parsed.Details?.Attachments.Count ?? 0);
                partial |= parsed.Partial;
            }
            catch (Exception error) when (error is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException)
            {
                cache.Remove(source);
                cachedEventCount = cache.Values.Sum(value => value.Events.Count + (value.Details?.Attachments.Count ?? 0));
                partial = true;
            }
        }

        var events = new List<UsageEvent>();
        var merged = new Dictionary<string, UsageEvent>(StringComparer.Ordinal);
        var eventIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cached in cache.Values)
        {
            foreach (var usageEvent in cached.Events)
            {
                if (provider == "claude")
                {
                    merged[usageEvent.EventKey] = merged.TryGetValue(usageEvent.EventKey, out var existing)
                        ? ClaudeJsonlParser.Merge(existing, usageEvent) : usageEvent;
                    continue;
                }
                if (eventIndices.TryGetValue(usageEvent.EventKey, out var existingIndex))
                {
                    var existing = events[existingIndex];
                    if (existing.Usage.CacheWriteInputTokens is null && usageEvent.Usage.CacheWriteInputTokens is not null
                        && existing.Usage.InputTokens == usageEvent.Usage.InputTokens
                        && existing.Usage.CachedInputTokens == usageEvent.Usage.CachedInputTokens
                        && existing.Usage.OutputTokens == usageEvent.Usage.OutputTokens)
                        events[existingIndex] = existing with { Usage = usageEvent.Usage };
                    continue;
                }

                if (events.Count >= maximumEventCount)
                {
                    partial = true;
                    resourceLimitReached = true;
                    break;
                }

                eventIndices[usageEvent.EventKey] = events.Count;
                events.Add(usageEvent);
            }
        }

        if (provider == "claude") events.AddRange(merged.Values.Take(maximumEventCount));
        var snapshot = Aggregate(events, now, weekStart, partial || hasMoreWork);
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
            Sessions = cache.Values.Where(x => x.Details is not null).Select(x => x.Details! with {
                Attachments = x.Details!.Attachments.Where(a => a.OccurredAt <= now).ToArray() }).ToArray() };
    }

    private DiscoveryResult DiscoverSources(CancellationToken cancellationToken)
    {
        var sources = new List<string>();
        var identities = new HashSet<FileIdentity>();
        var totalSourceBytes = 0L;
        var resourceLimitReached = false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden
                | FileAttributes.System
                | FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
            MaxRecursionDepth = 64,
            ReturnSpecialDirectories = false
        };

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            foreach (var candidate in Directory.EnumerateFiles(root, "*.jsonl", options).Take(maximumSourceCount + 1).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(candidate);
                if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sources.Count >= maximumSourceCount)
                {
                    resourceLimitReached = true;
                    break;
                }

                var info = new FileInfo(fullPath);
                if (info.Length > maximumSourceBytes)
                {
                    resourceLimitReached = true;
                    continue;
                }
                var identity = FileIdentity.TryRead(fullPath);
                if (identity is { } value && !identities.Add(value))
                {
                    continue;
                }
                if (info.Length > maximumTotalSourceBytes - totalSourceBytes)
                {
                    resourceLimitReached = true;
                    continue;
                }

                sources.Add(fullPath);
                totalSourceBytes += info.Length;
            }
        }

        sources.Sort(StringComparer.OrdinalIgnoreCase);
        return new DiscoveryResult(sources, resourceLimitReached);
    }

    private static ParsedFile ParseFile(
        string path,
        int maximumEvents,
        long maximumBytes,
        CancellationToken cancellationToken, string provider = "codex", byte[]? projectKey = null)
    {
        var events = new List<UsageEvent>();
        var attachments = new List<AttachmentObservation>();
        string? parentSession = null;
        var partial = false;
        var sessionId = HashIdentifier(SessionIdentifierFromFilename(path) ?? path);
        var state = UsageNormalizationState.Empty;
        var inheritsHistory = false;
        var historyReplayComplete = true;
        long? inheritedHistoryEndOrdinal = null;
        DateTimeOffset? sessionStartedAt = null;
        var sawSessionMetadata = false;
        var model = "unknown";
        var project = "Unknown project";
        var projectId = "unknown";

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        var identity = FileIdentity.TryRead(stream.SafeFileHandle);
        using var prefixHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var boundedLine in BoundedLineReader.Read(stream, maximumBytes, cancellationToken, prefixHash))
        {
            if (boundedLine.Oversized)
            {
                partial = true;
                continue;
            }

            var line = boundedLine.Bytes!;
            if (provider == "claude")
            {
                if (ClaudeJsonlParser.Parse(line, projectKey) is { } claudeEvent)
                {
                    if (events.Count + attachments.Count >= maximumEvents) return new ParsedFile([], true, true);
                    events.Add(claudeEvent);
                }
                continue;
            }
            if (line.AsSpan().IndexOf("\"input_image\""u8) >= 0)
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(line);
                    var root = document.RootElement; var payload = JsonFields.Object(root, "payload");
                    if (JsonFields.Text(root, "type") == "response_item" && JsonFields.Text(payload, "type") == "message"
                        && JsonFields.Text(payload, "role") == "user" && payload.TryGetProperty("content", out var content)
                        && content.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var images = content.EnumerateArray().Where(x => JsonFields.Text(x, "type") == "input_image").ToArray();
                        if (images.Length > 0 && DateTimeOffset.TryParse(JsonFields.Text(root, "timestamp"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var timestamp))
                        {
                            long? ordinal = root.TryGetProperty("ordinal", out var ordinalValue)
                                && ordinalValue.ValueKind == System.Text.Json.JsonValueKind.Number
                                && ordinalValue.TryGetInt64(out var number) ? number : null;
                            if (inheritsHistory && !historyReplayComplete)
                            {
                                if (inheritedHistoryEndOrdinal is not null && ordinal > inheritedHistoryEndOrdinal
                                    || inheritedHistoryEndOrdinal is null && sessionStartedAt is not null && timestamp >= sessionStartedAt)
                                    historyReplayComplete = true;
                            }
                            if (!inheritsHistory || historyReplayComplete)
                            {
                                if (events.Count + attachments.Count >= maximumEvents) return new ParsedFile([], true, true);
                                // Stable across copied/reformatted logs. The fallback keeps
                                // only a digest of image observations, never image/text data.
                                var identityKey = ordinal?.ToString(CultureInfo.InvariantCulture)
                                    ?? HashIdentifier(string.Join("|", images.Select(x =>
                                        ImageIdentity(x))));
                                attachments.Add(new AttachmentObservation(HashIdentifier($"images|{sessionId}|{timestamp:O}|{identityKey}"), timestamp, images.Length));
                            }
                        }
                        else if (images.Length > 0) partial = true;
                    }
                }
                catch (System.Text.Json.JsonException) { partial = true; }
            }
            if (line.AsSpan().IndexOf("\"turn_context\""u8) >= 0 || line.AsSpan().IndexOf("\"session_meta\""u8) >= 0)
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(line);
                    var payload = JsonFields.Object(document.RootElement, "payload");
                    model = JsonFields.Text(payload, "model") ?? model;
                    if (JsonFields.Text(payload, "cwd") is { } cwd) { project = JsonFields.Folder(cwd); projectId = ClaudeJsonlParser.ProjectIdentity(cwd, projectKey); }
                }
                catch (System.Text.Json.JsonException) { partial = true; }
            }
            if (!ContainsRelevantMarker(line))
            {
                continue;
            }

            var parsed = CodexJsonlParser.Parse(line);
            switch (parsed.Kind)
            {
                case ParsedLineKind.SessionMetadata:
                    if (sawSessionMetadata)
                    {
                        if (inheritsHistory)
                        {
                            historyReplayComplete = false;
                        }
                        break;
                    }

                    sawSessionMetadata = true;
                    var metadata = parsed.SessionMetadata!;
                    if (metadata.Id is not null)
                    {
                        sessionId = HashIdentifier(metadata.Id);
                    }

                    parentSession = metadata.ParentThreadId is null ? null : HashIdentifier(metadata.ParentThreadId);
                    inheritsHistory = metadata.InheritsHistory;
                    sessionStartedAt = metadata.OccurredAt;
                    inheritedHistoryEndOrdinal = metadata.SubagentHistoryStartOrdinal;
                    historyReplayComplete = !metadata.InheritsHistory;
                    break;

                case ParsedLineKind.TaskStarted:
                    if (!inheritsHistory || historyReplayComplete)
                    {
                        break;
                    }

                    var task = parsed.TaskStarted!;
                    if (inheritedHistoryEndOrdinal is not null
                        && task.Ordinal is not null
                        && task.Ordinal.Value > inheritedHistoryEndOrdinal.Value)
                    {
                        historyReplayComplete = true;
                    }
                    else if (inheritedHistoryEndOrdinal is null
                             && sessionStartedAt is not null
                             && task.StartedAt is not null
                             && Math.Floor(task.StartedAt.Value.ToUnixTimeMilliseconds() / 1000d)
                             >= Math.Floor(sessionStartedAt.Value.ToUnixTimeMilliseconds() / 1000d))
                    {
                        historyReplayComplete = true;
                    }
                    break;

                case ParsedLineKind.Token:
                    var observation = parsed.Token!;
                    var isInheritedReplay = false;
                    if (inheritsHistory && !historyReplayComplete)
                    {
                        if (inheritedHistoryEndOrdinal is not null
                            && observation.Ordinal is not null
                            && observation.Ordinal.Value > inheritedHistoryEndOrdinal.Value)
                        {
                            historyReplayComplete = true;
                        }
                        else
                        {
                            isInheritedReplay = true;
                        }
                    }

                    var priorCumulative = state.CumulativeHighWaterMark;
                    var priorObservedAt = state.LastObservedAt;
                    var normalized = UsageNormalizer.Normalize(observation, state);
                    // A repeated cumulative snapshot can supply cache metadata
                    // for the last reported event without adding any new tokens.
                    if (!isInheritedReplay && normalized.Delta is null && events.Count > 0
                        && (priorObservedAt is null || observation.OccurredAt >= priorObservedAt)
                        && observation.CumulativeUsage is { } cumulative && priorCumulative is { } previous
                        && cumulative.InputTokens == previous.InputTokens && cumulative.CachedInputTokens == previous.CachedInputTokens
                        && cumulative.OutputTokens == previous.OutputTokens
                        && observation.LastUsage is { CacheWriteInputTokens: not null } enriched
                        && events[^1] is { } last && last.SessionId == sessionId && last.Usage.CacheWriteInputTokens is null
                        && last.Usage.InputTokens == enriched.InputTokens && last.Usage.CachedInputTokens == enriched.CachedInputTokens
                        && last.Usage.OutputTokens == enriched.OutputTokens && enriched.IsValid)
                        events[^1] = last with { Usage = enriched };
                    state = normalized.State;
                    partial |= state.Quality == DataQuality.Partial;
                    if (isInheritedReplay || normalized.Delta is not { } delta || delta.IsZero)
                    {
                        break;
                    }

                    if (events.Count + attachments.Count >= maximumEvents)
                    {
                        return new ParsedFile([], true, true);
                    }

                    events.Add(new UsageEvent(
                        EventKey(sessionId, observation),
                        observation.OccurredAt,
                        delta, model, project, sessionId, "codex", projectId));
                    break;

                case ParsedLineKind.Malformed:
                    partial = true;
                    break;
            }
        }

        return new ParsedFile(events, partial, false, prefixHash.GetHashAndReset(), identity,
            provider == "codex" ? new SessionDetails(sessionId, parentSession, attachments) : null);
    }

    private static bool VerifyPrefix(string path, long length, ParsedFile parsed, CancellationToken cancellationToken)
    {
        if (parsed.PrefixHash is null) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        if (FileIdentity.TryRead(stream.SafeFileHandle) != parsed.Identity || stream.Length < length) return false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) return false;
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), parsed.PrefixHash)
                && FileIdentity.TryRead(path) == parsed.Identity;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static UsageSnapshot Aggregate(
        IReadOnlyList<UsageEvent> events,
        DateTimeOffset now,
        WeekStart weekStart,
        bool partial)
    {
        var timeZone = TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var todayStart = LocalStartUtc(localNow.Date, timeZone);
        var dayOffset = weekStart == WeekStart.Monday
            ? ((int)localNow.DayOfWeek + 6) % 7
            : (int)localNow.DayOfWeek;
        var weekStartUtc = LocalStartUtc(localNow.Date.AddDays(-dayOffset), timeZone);
        var monthStartUtc = LocalStartUtc(
            new DateTime(localNow.Year, localNow.Month, 1),
            timeZone);

        var today = TokenUsage.Zero;
        var week = TokenUsage.Zero;
        var month = TokenUsage.Zero;
        var allTime = TokenUsage.Zero;
        DateTimeOffset? newest = null;

        foreach (var usageEvent in events)
        {
            if (usageEvent.OccurredAt > now)
            {
                continue;
            }

            allTime = allTime.Add(usageEvent.Usage);
            if (usageEvent.OccurredAt >= monthStartUtc)
            {
                month = month.Add(usageEvent.Usage);
            }

            if (usageEvent.OccurredAt >= weekStartUtc)
            {
                week = week.Add(usageEvent.Usage);
            }

            if (usageEvent.OccurredAt >= todayStart)
            {
                today = today.Add(usageEvent.Usage);
            }

            if (newest is null || usageEvent.OccurredAt > newest.Value)
            {
                newest = usageEvent.OccurredAt;
            }
        }

        var quality = newest is null
            ? partial ? DataQuality.Partial : DataQuality.Unavailable
            : partial ? DataQuality.Partial : DataQuality.Exact;
        return new UsageSnapshot(today, week, month, allTime, quality, newest);
    }

    private static DateTimeOffset LocalStartUtc(DateTime localDate, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }

    private static bool ContainsRelevantMarker(byte[] line)
    {
        var bytes = line.AsSpan();
        return bytes.IndexOf("\"token_count\""u8) >= 0
            || bytes.IndexOf("\"session_meta\""u8) >= 0
            || bytes.IndexOf("\"task_started\""u8) >= 0;
    }

    private static string EventKey(string sessionId, TokenObservation observation)
    {
        var material = string.Join(
            '|',
            "event-v2",
            $"session:{sessionId}",
            $"time:{observation.OccurredAt.UtcDateTime.Ticks}",
            $"ordinal:{observation.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? "none"}",
            $"last:{UsageIdentity(observation.LastUsage)}",
            $"cumulative:{UsageIdentity(observation.CumulativeUsage)}");
        return HashIdentifier(material);
    }

    private static string UsageIdentity(TokenUsage? usage) => usage is null
        ? "none"
        : $"{usage.Value.InputTokens},{usage.Value.CachedInputTokens},{usage.Value.OutputTokens}";

    private static string? SessionIdentifierFromFilename(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (stem.Length < 36)
        {
            return null;
        }

        var suffix = stem[^36..];
        return Guid.TryParse(suffix, out var identifier)
            ? identifier.ToString("D")
            : null;
    }

    private static string ImageIdentity(System.Text.Json.JsonElement image)
    {
        foreach (var name in new[] { "image_url", "url" })
            if (image.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == System.Text.Json.JsonValueKind.String) return value.GetString()!;
                if (value.ValueKind == System.Text.Json.JsonValueKind.Object
                    && value.TryGetProperty("url", out var nested)
                    && nested.ValueKind == System.Text.Json.JsonValueKind.String) return nested.GetString()!;
            }
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream)) WriteCanonical(image, writer);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(System.Text.Json.JsonElement value, System.Text.Json.Utf8JsonWriter writer)
    {
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(property.Value, writer); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(item, writer);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static string HashIdentifier(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record ParsedFile(
        IReadOnlyList<UsageEvent> Events,
        bool Partial,
        bool ResourceLimitReached,
        byte[]? PrefixHash = null,
        FileIdentity? Identity = null,
        SessionDetails? Details = null);
    private sealed record DiscoveryResult(List<string> Sources, bool ResourceLimitReached);
    private sealed record CachedFile(FileStamp Stamp, IReadOnlyList<UsageEvent> Events, bool Partial, SessionDetails? Details);
    private readonly record struct FileStamp(long Length, long LastWriteTicks, long CreationTimeTicks)
    {
        public static FileStamp Read(string path)
        {
            var info = new FileInfo(path);
            return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
        }
    }

    private readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex)
    {
        public static FileIdentity? TryRead(SafeFileHandle handle)
        {
            if (!OperatingSystem.IsWindows() || !GetFileInformationByHandle(handle, out var information))
                return null;
            return new FileIdentity(information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        }

        public static FileIdentity? TryRead(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                using var handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.None);
                return TryRead(handle);
            }
            catch (Exception error) when (error is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException)
            {
                return null;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

#pragma warning disable SYSLIB1054 // This small blittable Win32 call avoids enabling unsafe source-generated interop.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);
#pragma warning restore SYSLIB1054
}

internal sealed record BoundedLine(byte[]? Bytes, bool Oversized);

internal static class BoundedLineReader
{
    public static IEnumerable<BoundedLine> Read(
        Stream stream,
        long maximumBytes,
        CancellationToken cancellationToken,
        IncrementalHash? prefixHash = null)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var lineBuffer = new ArrayBufferWriter<byte>(64 * 1024);
        var oversized = false;
        var totalBytesRead = 0L;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remainingBytes = maximumBytes - totalBytesRead;
                if (remainingBytes <= 0)
                {
                    if (oversized || lineBuffer.WrittenCount > 0)
                    {
                        yield return new BoundedLine(null, true);
                    }
                    yield break;
                }
                var read = stream.Read(
                    readBuffer,
                    0,
                    (int)Math.Min(readBuffer.Length, remainingBytes));
                if (read == 0)
                {
                    if (oversized || lineBuffer.WrittenCount > 0)
                    {
                        yield return new BoundedLine(null, true);
                    }
                    yield break;
                }
                totalBytesRead += read;
                prefixHash?.AppendData(readBuffer, 0, read);

                for (var index = 0; index < read; index++)
                {
                    var value = readBuffer[index];
                    if (value == (byte)'\n')
                    {
                        yield return oversized
                            ? new BoundedLine(null, true)
                            : new BoundedLine(lineBuffer.WrittenMemory.ToArray(), false);
                        lineBuffer.Clear();
                        oversized = false;
                        continue;
                    }

                    if (oversized)
                    {
                        continue;
                    }

                    if (lineBuffer.WrittenCount >= CodexJsonlParser.MaximumLineBytes)
                    {
                        lineBuffer.Clear();
                        oversized = true;
                        continue;
                    }

                    lineBuffer.GetSpan(1)[0] = value;
                    lineBuffer.Advance(1);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }
}
