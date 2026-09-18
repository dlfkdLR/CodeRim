import CryptoKit
import Darwin
import Foundation

struct CollectorRefreshResult: Sendable {
    let snapshot: UsageSnapshot
    let sourceCount: Int
    let processedBytes: Int64
    let fingerprintBytesRead: Int64
    let statistics: DataStatistics
    let hasMoreWork: Bool
    let maintenanceWarning: String?
}

struct DataStatistics: Equatable, Sendable {
    let databaseBytes: Int64
    let oldestRecord: Date?
    let newestRecord: Date?

    static let empty = DataStatistics(databaseBytes: 0, oldestRecord: nil, newestRecord: nil)
}

private enum CodexUsageCollectorError: Error {
    case sourceUnavailable
    case sourceChangedDuringRead
}

private struct SourceProcessResult {
    let processedBytes: Int64
    let fingerprintBytesRead: Int64
    let hasMore: Bool
}

private struct FingerprintAdvanceResult {
    let bytesRead: Int64
    let reachedTarget: Bool
}

private struct SourceFingerprintAccumulator {
    static let versionPrefix = "v2:"

    private var authenticator: HMAC<SHA256>
    private var legacyAuthenticator: HMAC<SHA256>?
    private(set) var authenticatedOffset: Int64 = 0

    init(key: Data, legacyOffset: Int64? = nil) {
        let symmetricKey = SymmetricKey(data: key)
        var authenticator = HMAC<SHA256>(key: symmetricKey)
        authenticator.update(data: Data("CodeRim.source-fingerprint.v2|".utf8))
        self.authenticator = authenticator

        if let legacyOffset {
            var legacy = HMAC<SHA256>(key: symmetricKey)
            legacy.update(data: Data("size:\(legacyOffset)|".utf8))
            legacyAuthenticator = legacy
        }
    }

    mutating func advance(
        fileDescriptor: Int32,
        to offset: Int64,
        maximumBytes: Int64
    ) throws -> FingerprintAdvanceResult {
        guard offset >= authenticatedOffset else {
            throw CodexUsageCollectorError.sourceChangedDuringRead
        }
        let startingOffset = authenticatedOffset
        let byteLimit = authenticatedOffset + max(0, maximumBytes)
        while authenticatedOffset < offset, authenticatedOffset < byteLimit {
            try Task.checkCancellation()
            let byteCount = Int(min(
                Int64(256 * 1_024),
                offset - authenticatedOffset,
                byteLimit - authenticatedOffset
            ))
            let sample = try readFingerprintSample(
                fileDescriptor: fileDescriptor,
                startingAt: authenticatedOffset,
                byteCount: byteCount
            )
            authenticator.update(data: sample)
            legacyAuthenticator?.update(data: sample)
            authenticatedOffset += Int64(byteCount)
        }
        return FingerprintAdvanceResult(
            bytesRead: authenticatedOffset - startingOffset,
            reachedTarget: authenticatedOffset == offset
        )
    }

    var fingerprint: String {
        let copy = authenticator
        return Self.versionPrefix + Self.hexDigest(copy.finalize())
    }

    var legacyFingerprint: String? {
        guard let copy = legacyAuthenticator else { return nil }
        return Self.hexDigest(copy.finalize())
    }

    mutating func discardLegacyFingerprint() {
        legacyAuthenticator = nil
    }

    private static func hexDigest(_ authenticationCode: HMAC<SHA256>.MAC) -> String {
        Data(authenticationCode).map { String(format: "%02x", $0) }.joined()
    }
}

private struct FingerprintVerificationState {
    let fileIdentity: String
    let generation: Int64
    let committedOffset: Int64
    let contentFingerprint: String
    let observedSize: Int64
    let modificationTimeNanoseconds: Int64
    let statusChangeTimeNanoseconds: Int64
    var accumulator: SourceFingerprintAccumulator

    func matches(
        _ checkpoint: SourceCheckpoint,
        openedSize: Int64,
        openedModificationTime: Int64,
        openedStatusChangeTime: Int64
    ) -> Bool {
        fileIdentity == checkpoint.fileIdentity
            && generation == checkpoint.generation
            && committedOffset == checkpoint.committedOffset
            && contentFingerprint == checkpoint.contentFingerprint
            && observedSize == openedSize
            && modificationTimeNanoseconds == openedModificationTime
            && statusChangeTimeNanoseconds == openedStatusChangeTime
            && accumulator.authenticatedOffset <= checkpoint.committedOffset
    }
}

private func readFingerprintSample(
    fileDescriptor: Int32,
    startingAt start: Int64,
    byteCount: Int
) throws -> Data {
    var bytes = [UInt8](repeating: 0, count: byteCount)
    let readCount = bytes.withUnsafeMutableBytes { buffer -> Int in
        guard let baseAddress = buffer.baseAddress else { return 0 }
        var total = 0
        while total < byteCount {
            let result = Darwin.pread(
                fileDescriptor,
                baseAddress.advanced(by: total),
                byteCount - total,
                off_t(start + Int64(total))
            )
            if result < 0, errno == EINTR { continue }
            guard result > 0 else { return total }
            total += result
        }
        return total
    }
    guard readCount == byteCount else {
        throw CodexUsageCollectorError.sourceChangedDuringRead
    }
    return Data(bytes)
}

private enum CollectorResourceLimits {
    static let maximumBytesPerRefresh: Int64 = 32 * 1_024 * 1_024
    static let maximumRefreshDuration: Duration = .seconds(5)
}

actor CodexUsageCollector {
    private let provider: UsageProvider
    private let database: SQLiteDatabase
    private let roots: [URL]
    private let discovery = CodexSourceDiscovery()
    private let parser = CodexJSONLParser()
    private let normalizer = UsageNormalizer()
    private let maximumBytesPerRefresh: Int64
    private let maximumRefreshDuration: Duration
    private var sourceFingerprintKeyData: Data?
    private var fingerprintVerificationStates: [String: FingerprintVerificationState] = [:]
    private var readSnapshots: [String: SourceReadSnapshot] = [:]
    private var nextSourcePath: String?

    init(
        database: SQLiteDatabase,
        roots: [URL],
        provider: UsageProvider = .codex,
        maximumBytesPerRefresh: Int64 = CollectorResourceLimits.maximumBytesPerRefresh,
        maximumRefreshDuration: Duration = CollectorResourceLimits.maximumRefreshDuration
    ) {
        self.database = database
        self.provider = provider
        self.roots = roots
        self.maximumBytesPerRefresh = max(
            Int64((CodexJSONLParser.maximumLineBytes + 1) * 2),
            maximumBytesPerRefresh
        )
        self.maximumRefreshDuration = maximumRefreshDuration
    }

    func cachedSnapshot(now: Date = Date(), calendar: Calendar = .current, weekStart: WeekStart) async throws -> UsageSnapshot {
        try await database.usageSnapshot(now: now, calendar: calendar, weekStart: weekStart)
    }

    func analyticsSnapshot(
        range: AnalyticsRange,
        through end: Date = Date(),
        calendar: Calendar = .current
    ) async throws -> AnalyticsSnapshot {
        let snapshot = try await database.analyticsSnapshot(range: range, through: end, calendar: calendar)
        guard provider == .codex else { return snapshot }
        let directories = Set(roots.map { $0.deletingLastPathComponent() })
        let threads = directories.flatMap { directory in
            CodexStore.threads(in: directory.appendingPathComponent("state_5.sqlite"),
                               desktopStore: directory.appendingPathComponent("sqlite/codex-dev.db"),
                               includeArchived: true)
        }
        let projectSessions = try await database.projectSessionIDs(from: snapshot.interval.start, through: end)
        return CodexAnalyticsNames.applying(threads, to: snapshot, projectSessions: projectSessions)
    }

    func refresh(now: Date = Date(), calendar: Calendar = .current, weekStart: WeekStart) async throws -> CollectorRefreshResult {
        var sources = try discovery.discover(in: roots)
        if let nextSourcePath, let start = sources.firstIndex(where: { $0.url.path == nextSourcePath }) {
            sources = Array(sources[start...]) + Array(sources[..<start])
        }
        let activeCheckpointKeys = Set(
            sources.map { storageIdentifier($0.url.standardizedFileURL.path) }
        )
        fingerprintVerificationStates = fingerprintVerificationStates.filter {
            activeCheckpointKeys.contains($0.key)
        }
        readSnapshots = readSnapshots.filter { activeCheckpointKeys.contains($0.key) }
        let importPolicy = try await database.importPolicy()
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: maximumRefreshDuration)
        var processedBytes: Int64 = 0
        var fingerprintBytesRead: Int64 = 0
        var hasMoreWork = false
        var skippedSource = false
        for (index, source) in sources.enumerated() {
            try Task.checkCancellation()
            let remainingBytes = maximumBytesPerRefresh - processedBytes - fingerprintBytesRead
            guard remainingBytes > 0, clock.now < deadline else {
                hasMoreWork = true
                break
            }
            nextSourcePath = sources[(index + 1) % sources.count].url.path
            do {
                let result = try await process(
                    source,
                    importCutoff: importPolicy.cutoff,
                    expectedEpoch: importPolicy.dataEpoch,
                    maximumBytes: remainingBytes,
                    deadline: deadline
                )
                processedBytes += result.processedBytes
                fingerprintBytesRead += result.fingerprintBytesRead
                if result.hasMore {
                    hasMoreWork = true
                    break
                }
            } catch is CodexUsageCollectorError {
                skippedSource = true
            }
        }

        var snapshot = try await database.usageSnapshot(now: now, calendar: calendar, weekStart: weekStart)
        if skippedSource {
            snapshot.quality = .partial
        }
        let statistics = try await database.dataStatistics()
        return CollectorRefreshResult(
            snapshot: snapshot,
            sourceCount: sources.count,
            processedBytes: processedBytes,
            fingerprintBytesRead: fingerprintBytesRead,
            statistics: statistics,
            hasMoreWork: hasMoreWork,
            maintenanceWarning: nil
        )
    }

    func rebuild(now: Date = Date(), calendar: Calendar = .current, weekStart: WeekStart) async throws -> CollectorRefreshResult {
        try await database.rebuildStatistics()
        return try await refresh(now: now, calendar: calendar, weekStart: weekStart)
    }

    func clearLocalHistory(at cutoff: Date = Date(), calendar: Calendar = .current, weekStart: WeekStart) async throws -> CollectorRefreshResult {
        readSnapshots.removeAll()
        fingerprintVerificationStates.removeAll()
        let compactionStatus = try await database.clearLocalHistory(
            at: cutoff, preservesMessageExclusions: provider == .claude
        )
        let result = try await refresh(now: cutoff, calendar: calendar, weekStart: weekStart)
        return CollectorRefreshResult(
            snapshot: result.snapshot,
            sourceCount: result.sourceCount,
            processedBytes: result.processedBytes,
            fingerprintBytesRead: result.fingerprintBytesRead,
            statistics: result.statistics,
            hasMoreWork: result.hasMoreWork,
            maintenanceWarning: compactionStatus == .deferred
                ? "History cleared; secure compaction will need another attempt"
                : nil
        )
    }

    private func process(
        _ source: CodexSessionSource,
        importCutoff: Date?,
        expectedEpoch: Int64,
        maximumBytes: Int64,
        deadline: ContinuousClock.Instant
    ) async throws -> SourceProcessResult {
        let checkpointKey = storageIdentifier(source.url.standardizedFileURL.path)
        var checkpoint = try await database.checkpoint(for: checkpointKey)
            ?? SourceCheckpoint.fresh(sourcePath: checkpointKey, fileIdentity: source.identity)
        let previousCheckpoint = checkpoint
        let fingerprintKey = try await sourceFingerprintKey()
        var fingerprintBytesRead: Int64 = 0

        let fileDescriptor = Darwin.open(
            source.url.path,
            O_RDONLY | O_CLOEXEC | O_NOFOLLOW
        )
        guard fileDescriptor >= 0 else {
            throw CodexUsageCollectorError.sourceUnavailable
        }
        let liveHandle = FileHandle(fileDescriptor: fileDescriptor, closeOnDealloc: true)
        defer { try? liveHandle.close() }
        var handle = liveHandle
        var openedStat = stat()
        guard fstat(handle.fileDescriptor, &openedStat) == 0,
              (openedStat.st_mode & S_IFMT) == S_IFREG,
              "\(UInt64(openedStat.st_dev)):\(UInt64(openedStat.st_ino))" == source.identity
        else { throw CodexUsageCollectorError.sourceChangedDuringRead }
        if let snapshot = readSnapshots[checkpointKey], !snapshot.accepts(openedStat) {
            readSnapshots.removeValue(forKey: checkpointKey)
            fingerprintVerificationStates.removeValue(forKey: checkpointKey)
        }
        let liveWasUnchanged = checkpoint.fileIdentity == source.identity
            && checkpoint.observedSize == Int64(openedStat.st_size)
            && checkpoint.modificationTimeNanoseconds == modificationTimeNanoseconds(openedStat)
            && checkpoint.committedOffset >= Int64(openedStat.st_size) && !checkpoint.hasPendingImport
        if readSnapshots[checkpointKey] == nil, !liveWasUnchanged,
           Int64(openedStat.st_size) > maximumBytesPerRefresh / 2 {
            // Bound open descriptors; pending large files get their turn as
            // active snapshots finish. Small sources continue to be processed.
            if readSnapshots.count >= 4 {
                return SourceProcessResult(processedBytes: 0, fingerprintBytesRead: 0, hasMore: true)
            }
            readSnapshots[checkpointKey] = SourceReadSnapshot.capture(descriptor: fileDescriptor, metadata: openedStat)
        }
        var snapshotCompleted = false
        if let snapshot = readSnapshots[checkpointKey] {
            handle = snapshot.handle
            openedStat = snapshot.metadata
        }
        defer {
            if snapshotCompleted { readSnapshots.removeValue(forKey: checkpointKey) }
        }
        func liveHasBytes(after offset: Int64) -> Bool {
            var current = stat()
            return fstat(liveHandle.fileDescriptor, &current) == 0 && Int64(current.st_size) > offset
        }
        let openedSize = Int64(openedStat.st_size)
        let openedModificationTime = modificationTimeNanoseconds(openedStat)
        let openedStatusChangeTime = statusChangeTimeNanoseconds(openedStat)

        let sameSizeMutation = checkpoint.observedSize > 0
            && checkpoint.observedSize == openedSize
            && checkpoint.modificationTimeNanoseconds > 0
            && checkpoint.modificationTimeNanoseconds != openedModificationTime
        let unchangedSource = checkpoint.contentFingerprint.isEmpty == false
            && checkpoint.observedSize == openedSize
            && checkpoint.modificationTimeNanoseconds == openedModificationTime
            && checkpoint.committedOffset >= openedSize
            && checkpoint.hasPendingImport == false
        if checkpoint.fileIdentity != source.identity
            || openedSize < checkpoint.committedOffset
            || sameSizeMutation {
            checkpoint = SourceCheckpoint.fresh(
                sourcePath: checkpointKey,
                fileIdentity: source.identity,
                generation: checkpoint.generation + 1
            )
        } else if unchangedSource {
            snapshotCompleted = true
            return SourceProcessResult(
                processedBytes: 0,
                fingerprintBytesRead: 0,
                hasMore: liveHasBytes(after: checkpoint.committedOffset)
            )
        }

        let needsLegacyVerification = checkpoint.contentFingerprint.isEmpty == false
            && checkpoint.contentFingerprint.hasPrefix(SourceFingerprintAccumulator.versionPrefix) == false
        var fingerprintAccumulator: SourceFingerprintAccumulator
        if let savedState = fingerprintVerificationStates[checkpointKey],
           savedState.matches(
               checkpoint,
               openedSize: openedSize,
               openedModificationTime: openedModificationTime,
               openedStatusChangeTime: openedStatusChangeTime
           ) {
            fingerprintAccumulator = savedState.accumulator
        } else {
            fingerprintVerificationStates.removeValue(forKey: checkpointKey)
            fingerprintAccumulator = SourceFingerprintAccumulator(
                key: fingerprintKey,
                legacyOffset: needsLegacyVerification ? checkpoint.committedOffset : nil
            )
        }
        if checkpoint.committedOffset > 0,
           openedSize >= checkpoint.committedOffset {
            let advance = try fingerprintAccumulator.advance(
                fileDescriptor: handle.fileDescriptor,
                to: checkpoint.committedOffset,
                maximumBytes: maximumBytes
            )
            fingerprintBytesRead += advance.bytesRead
            guard advance.reachedTarget else {
                cacheFingerprintState(
                    checkpointKey: checkpointKey,
                    checkpoint: checkpoint,
                    observedSize: openedSize,
                    modificationTimeNanoseconds: openedModificationTime,
                    fileDescriptor: handle.fileDescriptor,
                    accumulator: fingerprintAccumulator
                )
                return SourceProcessResult(
                    processedBytes: 0,
                    fingerprintBytesRead: fingerprintBytesRead,
                    hasMore: true
                )
            }
            try verifyOpenedSourceState(
                fileDescriptor: handle.fileDescriptor,
                expectedIdentity: source.identity,
                expectedSize: openedSize,
                expectedModificationTime: openedModificationTime,
                expectedStatusChangeTime: openedStatusChangeTime
            )
            fingerprintVerificationStates.removeValue(forKey: checkpointKey)
            let fingerprintMatches: Bool
            if checkpoint.contentFingerprint.isEmpty {
                fingerprintMatches = true
            } else if checkpoint.contentFingerprint.hasPrefix(SourceFingerprintAccumulator.versionPrefix) {
                fingerprintMatches = checkpoint.contentFingerprint == fingerprintAccumulator.fingerprint
            } else {
                fingerprintMatches = checkpoint.contentFingerprint == fingerprintAccumulator.legacyFingerprint
            }
            if fingerprintMatches {
                checkpoint.contentFingerprint = fingerprintAccumulator.fingerprint
                fingerprintAccumulator.discardLegacyFingerprint()
            } else {
                fingerprintVerificationStates.removeValue(forKey: checkpointKey)
                checkpoint = SourceCheckpoint.fresh(
                    sourcePath: checkpointKey,
                    fileIdentity: source.identity,
                    generation: checkpoint.generation + 1
                )
                fingerprintAccumulator = SourceFingerprintAccumulator(key: fingerprintKey)
            }
        }
        let remainingIOBudget = maximumBytes - fingerprintBytesRead
        let minimumScanBytes = Int64(CodexJSONLParser.maximumLineBytes + 1)
        guard remainingIOBudget >= minimumScanBytes * 2 else {
            snapshotCompleted = checkpoint.committedOffset >= openedSize
            // A changed prefix may have reset the generation using the last
            // bytes of this pass. Persist that reset before yielding, otherwise
            // the next pass reloads the old checkpoint and verifies forever.
            if checkpoint != previousCheckpoint {
                checkpoint.hasPendingImport = openedSize > checkpoint.committedOffset
                    || liveHasBytes(after: checkpoint.committedOffset)
                _ = try await database.commit(events: [], checkpoint: checkpoint,
                    normalizationState: nil, expectedEpoch: expectedEpoch)
            }
            cacheFingerprintState(
                checkpointKey: checkpointKey,
                checkpoint: checkpoint,
                observedSize: openedSize,
                modificationTimeNanoseconds: openedModificationTime,
                fileDescriptor: handle.fileDescriptor,
                accumulator: fingerprintAccumulator
            )
            return SourceProcessResult(
                processedBytes: 0,
                fingerprintBytesRead: fingerprintBytesRead,
                hasMore: openedSize > checkpoint.committedOffset || liveHasBytes(after: checkpoint.committedOffset)
            )
        }
        let scanByteBudget = remainingIOBudget / 2
        checkpoint.observedSize = openedSize
        checkpoint.modificationTimeNanoseconds = openedModificationTime
        guard openedSize > checkpoint.committedOffset else {
            snapshotCompleted = true
            checkpoint.hasPendingImport = liveHasBytes(after: checkpoint.committedOffset)
            if checkpoint.contentFingerprint.isEmpty {
                fingerprintBytesRead += try updateCheckpointFingerprint(
                    &checkpoint,
                    accumulator: &fingerprintAccumulator,
                    fileDescriptor: handle.fileDescriptor
                )
            }
            if checkpoint != previousCheckpoint {
                _ = try await database.commit(
                    events: [],
                    checkpoint: checkpoint,
                    normalizationState: nil,
                    expectedEpoch: expectedEpoch
                )
            }
            cacheFingerprintState(
                checkpointKey: checkpointKey,
                checkpoint: checkpoint,
                observedSize: checkpoint.observedSize,
                modificationTimeNanoseconds: checkpoint.modificationTimeNanoseconds,
                fileDescriptor: handle.fileDescriptor,
                accumulator: fingerprintAccumulator
            )
            return SourceProcessResult(
                processedBytes: 0,
                fingerprintBytesRead: fingerprintBytesRead,
                hasMore: checkpoint.hasPendingImport
            )
        }

        if checkpoint.sessionID == nil {
            checkpoint.sessionID = sessionIdentifierFromFilename(source.url)
        }

        var metadata = SessionMetadata(
            id: checkpoint.sessionID,
            model: checkpoint.model,
            workingDirectory: nil,
            forkedFromID: checkpoint.inheritsHistory ? "inherited" : nil,
            parentThreadID: checkpoint.parentSessionID,
            subagentHistoryStartOrdinal: checkpoint.inheritedHistoryEndOrdinal,
            occurredAt: checkpoint.sessionStartedAt
        )
        var normalizationState = if let sessionID = checkpoint.sessionID {
            try await database.normalizationState(for: sessionID)
        } else {
            UsageNormalizationState.empty
        }
        var persistedNormalizationState = normalizationState
        var loadedStateSessionID = checkpoint.sessionID
        var pendingEvents: [UsageEvent] = []
        var excludedEventKeys: Set<String> = []
        pendingEvents.reserveCapacity(256)
        var completedLineCount = 0
        let startingOffset = checkpoint.committedOffset

        guard Darwin.lseek(
            handle.fileDescriptor,
            off_t(checkpoint.committedOffset),
            SEEK_SET
        ) == off_t(checkpoint.committedOffset) else {
            throw CodexUsageCollectorError.sourceUnavailable
        }

        var lineBuffer = Data()
        lineBuffer.reserveCapacity(64 * 1024)
        var lineStartOffset = checkpoint.committedOffset
        var readOffset = checkpoint.committedOffset
        var skippingOversizedLine = checkpoint.isSkippingOversizedLine
        var stoppedForBudget = false
        let clock = ContinuousClock()

        while true {
            let scannedBytes = readOffset - startingOffset
            if scannedBytes >= scanByteBudget || (scannedBytes > 0 && clock.now >= deadline) {
                stoppedForBudget = true
                break
            }
            let readLimit = Int(min(Int64(256 * 1_024), scanByteBudget - scannedBytes))
            let chunk: Data
            do {
                chunk = try handle.read(upToCount: readLimit) ?? Data()
            } catch {
                throw CodexUsageCollectorError.sourceUnavailable
            }
            guard !chunk.isEmpty else { break }
            try Task.checkCancellation()
            let chunkStartOffset = readOffset
            readOffset += Int64(chunk.count)

            if skippingOversizedLine {
                if let newline = chunk.firstIndex(of: 0x0A) {
                    let afterNewline = chunk.index(after: newline)
                    let consumed = chunk.distance(from: chunk.startIndex, to: afterNewline)
                    checkpoint.committedOffset = chunkStartOffset + Int64(consumed)
                    checkpoint.isSkippingOversizedLine = false
                    lineStartOffset = checkpoint.committedOffset
                    skippingOversizedLine = false
                    lineBuffer.append(contentsOf: chunk[afterNewline...])
                } else {
                    continue
                }
            } else {
                lineBuffer.append(chunk)
            }

            var consumedThrough = lineBuffer.startIndex
            while let newline = lineBuffer[consumedThrough...].firstIndex(of: 0x0A) {
                let line = Data(lineBuffer[consumedThrough..<newline])
                let linePosition = lineStartOffset + Int64(lineBuffer.distance(from: lineBuffer.startIndex, to: consumedThrough))
                try await processLine(
                    line,
                    position: linePosition,
                    source: source,
                    checkpoint: &checkpoint,
                    metadata: &metadata,
                    normalizationState: &normalizationState,
                    persistedNormalizationState: &persistedNormalizationState,
                    loadedStateSessionID: &loadedStateSessionID,
                    pendingEvents: &pendingEvents,
                    excludedEventKeys: &excludedEventKeys,
                    importCutoff: importCutoff
                )
                completedLineCount += 1
                consumedThrough = lineBuffer.index(after: newline)
                checkpoint.committedOffset = lineStartOffset
                    + Int64(lineBuffer.distance(from: lineBuffer.startIndex, to: consumedThrough))

                if completedLineCount >= 1_000 || pendingEvents.count >= 256 {
                    fingerprintBytesRead += try updateCheckpointFingerprint(
                        &checkpoint,
                        accumulator: &fingerprintAccumulator,
                        fileDescriptor: handle.fileDescriptor
                    )
                    let committedNormalizationState = try await database.commit(
                        events: pendingEvents,
                        checkpoint: checkpoint,
                        normalizationState: checkpoint.sessionID == nil ? nil : normalizationState,
                        expectedEpoch: expectedEpoch,
                        expectedNormalizationState: checkpoint.sessionID == nil ? nil : persistedNormalizationState,
                        mergesMessageUsage: provider == .claude,
                        excludedEventKeys: excludedEventKeys
                    )
                    if let committedNormalizationState {
                        persistedNormalizationState = committedNormalizationState
                        if normalizationState.cumulativeHighWaterMark != nil {
                            normalizationState = committedNormalizationState
                        }
                    }
                    pendingEvents.removeAll(keepingCapacity: true)
                    excludedEventKeys.removeAll(keepingCapacity: true)
                    completedLineCount = 0
                }
            }

            if consumedThrough != lineBuffer.startIndex {
                let consumedCount = lineBuffer.distance(from: lineBuffer.startIndex, to: consumedThrough)
                lineBuffer.removeSubrange(lineBuffer.startIndex..<consumedThrough)
                lineStartOffset += Int64(consumedCount)
            }

            if lineBuffer.count > CodexJSONLParser.maximumLineBytes {
                lineBuffer.removeAll(keepingCapacity: false)
                skippingOversizedLine = true
                checkpoint.isSkippingOversizedLine = true
            }
        }

        if skippingOversizedLine {
            checkpoint.committedOffset = readOffset
        }
        var finalStat = stat()
        snapshotCompleted = !stoppedForBudget
        let sourceGrewWhileReading = fstat(liveHandle.fileDescriptor, &finalStat) == 0
            && Int64(finalStat.st_size) > readOffset
        checkpoint.hasPendingImport = stoppedForBudget || sourceGrewWhileReading

        if checkpoint.committedOffset > startingOffset || checkpoint != previousCheckpoint {
            fingerprintBytesRead += try updateCheckpointFingerprint(
                &checkpoint,
                accumulator: &fingerprintAccumulator,
                fileDescriptor: handle.fileDescriptor
            )
            let committedNormalizationState = try await database.commit(
                events: pendingEvents,
                checkpoint: checkpoint,
                normalizationState: checkpoint.sessionID == nil ? nil : normalizationState,
                expectedEpoch: expectedEpoch,
                expectedNormalizationState: checkpoint.sessionID == nil ? nil : persistedNormalizationState,
                mergesMessageUsage: provider == .claude,
                excludedEventKeys: excludedEventKeys
            )
            if let committedNormalizationState,
               normalizationState.cumulativeHighWaterMark != nil {
                normalizationState = committedNormalizationState
            }
        }
        cacheFingerprintState(
            checkpointKey: checkpointKey,
            checkpoint: checkpoint,
            observedSize: checkpoint.observedSize,
            modificationTimeNanoseconds: checkpoint.modificationTimeNanoseconds,
            fileDescriptor: handle.fileDescriptor,
            accumulator: fingerprintAccumulator
        )
        return SourceProcessResult(
            processedBytes: readOffset - startingOffset,
            fingerprintBytesRead: fingerprintBytesRead,
            hasMore: stoppedForBudget || sourceGrewWhileReading
        )
    }

    /// Fingerprints and checkpoints describe the original identity at capture
    /// time, while every byte in this pass comes from the same frozen clone.
    private func readSourceStat(_ descriptor: Int32, _ value: inout stat) -> Int32 {
        let result = fstat(descriptor, &value)
        guard result == 0 else { return result }
        if let snapshot = readSnapshots.values.first(where: { $0.handle.fileDescriptor == descriptor }) {
            guard value.st_size == snapshot.metadata.st_size else { return -1 }
            value = snapshot.metadata
        }
        return 0
    }

    private func updateCheckpointFingerprint(
        _ checkpoint: inout SourceCheckpoint,
        accumulator: inout SourceFingerprintAccumulator,
        fileDescriptor: Int32
    ) throws -> Int64 {
        var currentStat = stat()
        guard readSourceStat(fileDescriptor, &currentStat) == 0,
              (currentStat.st_mode & S_IFMT) == S_IFREG,
              Int64(currentStat.st_size) >= checkpoint.committedOffset
        else {
            throw CodexUsageCollectorError.sourceChangedDuringRead
        }
        checkpoint.observedSize = Int64(currentStat.st_size)
        checkpoint.modificationTimeNanoseconds = modificationTimeNanoseconds(currentStat)
        let advance = try accumulator.advance(
            fileDescriptor: fileDescriptor,
            to: checkpoint.committedOffset,
            maximumBytes: checkpoint.committedOffset - accumulator.authenticatedOffset
        )
        guard advance.reachedTarget else {
            throw CodexUsageCollectorError.sourceChangedDuringRead
        }
        checkpoint.contentFingerprint = accumulator.fingerprint
        accumulator.discardLegacyFingerprint()
        return advance.bytesRead
    }

    private func cacheFingerprintState(
        checkpointKey: String,
        checkpoint: SourceCheckpoint,
        observedSize: Int64,
        modificationTimeNanoseconds expectedModificationTimeNanoseconds: Int64,
        fileDescriptor: Int32,
        accumulator: SourceFingerprintAccumulator
    ) {
        var currentStat = stat()
        guard accumulator.authenticatedOffset <= checkpoint.committedOffset,
              readSourceStat(fileDescriptor, &currentStat) == 0,
              (currentStat.st_mode & S_IFMT) == S_IFREG,
              "\(UInt64(currentStat.st_dev)):\(UInt64(currentStat.st_ino))" == checkpoint.fileIdentity,
              Int64(currentStat.st_size) == observedSize,
              modificationTimeNanoseconds(currentStat) == expectedModificationTimeNanoseconds
        else {
            fingerprintVerificationStates.removeValue(forKey: checkpointKey)
            return
        }
        fingerprintVerificationStates[checkpointKey] = FingerprintVerificationState(
            fileIdentity: checkpoint.fileIdentity,
            generation: checkpoint.generation,
            committedOffset: checkpoint.committedOffset,
            contentFingerprint: checkpoint.contentFingerprint,
            observedSize: observedSize,
            modificationTimeNanoseconds: expectedModificationTimeNanoseconds,
            statusChangeTimeNanoseconds: statusChangeTimeNanoseconds(currentStat),
            accumulator: accumulator
        )
    }

    private func sourceFingerprintKey() async throws -> Data {
        if let sourceFingerprintKeyData { return sourceFingerprintKeyData }
        let key = await database.sourceFingerprintKey()
        sourceFingerprintKeyData = key
        return key
    }

    private func modificationTimeNanoseconds(_ value: stat) -> Int64 {
        Int64(value.st_mtimespec.tv_sec) * 1_000_000_000
            + Int64(value.st_mtimespec.tv_nsec)
    }

    private func statusChangeTimeNanoseconds(_ value: stat) -> Int64 {
        Int64(value.st_ctimespec.tv_sec) * 1_000_000_000
            + Int64(value.st_ctimespec.tv_nsec)
    }

    private func verifyOpenedSourceState(
        fileDescriptor: Int32,
        expectedIdentity: String,
        expectedSize: Int64,
        expectedModificationTime: Int64,
        expectedStatusChangeTime: Int64
    ) throws {
        var currentStat = stat()
        guard readSourceStat(fileDescriptor, &currentStat) == 0,
              (currentStat.st_mode & S_IFMT) == S_IFREG,
              "\(UInt64(currentStat.st_dev)):\(UInt64(currentStat.st_ino))" == expectedIdentity,
              Int64(currentStat.st_size) == expectedSize,
              modificationTimeNanoseconds(currentStat) == expectedModificationTime,
              statusChangeTimeNanoseconds(currentStat) == expectedStatusChangeTime
        else {
            throw CodexUsageCollectorError.sourceChangedDuringRead
        }
    }

    private func processLine(
        _ line: Data,
        position: Int64,
        source: CodexSessionSource,
        checkpoint: inout SourceCheckpoint,
        metadata: inout SessionMetadata,
        normalizationState: inout UsageNormalizationState,
        persistedNormalizationState: inout UsageNormalizationState,
        loadedStateSessionID: inout String?,
        pendingEvents: inout [UsageEvent],
        excludedEventKeys: inout Set<String>,
        importCutoff: Date?
    ) async throws {
        if provider == .claude {
            try await processClaudeLine(line, position: position, source: source,
                                        checkpoint: &checkpoint, pendingEvents: &pendingEvents,
                                        excludedEventKeys: &excludedEventKeys,
                                        importCutoff: importCutoff)
            return
        }
        guard line.containsASCII("\"token_count\"")
                || line.containsASCII("\"session_meta\"")
                || line.containsASCII("\"task_started\"")
                || line.containsASCII("\"turn_context\"")
                || (line.containsASCII("\"response_item\"") && line.containsASCII("\"input_image\""))
        else { return }

        switch parser.parse(line) {
        case let .sessionMetadata(parsed):
            guard checkpoint.committedOffset == 0 else {
                if checkpoint.inheritsHistory {
                    checkpoint.historyReplayComplete = false
                }
                return
            }
            let resolvedID = parsed.id.map(storageIdentifier) ?? checkpoint.sessionID
            let resolvedModel = canonicalModelID(parsed.model)
            let project = try await projectProjection(parsed.workingDirectory)
            let parentSessionID = parsed.parentThreadID.map(storageIdentifier)
            metadata = SessionMetadata(
                id: resolvedID,
                model: resolvedModel,
                workingDirectory: nil,
                forkedFromID: parsed.forkedFromID,
                parentThreadID: parentSessionID,
                subagentHistoryStartOrdinal: parsed.subagentHistoryStartOrdinal,
                occurredAt: parsed.occurredAt
            )
            checkpoint.sessionID = resolvedID
            checkpoint.inheritsHistory = parsed.inheritsHistory
            checkpoint.sessionStartedAt = parsed.occurredAt
            checkpoint.inheritedHistoryEndOrdinal = parsed.subagentHistoryStartOrdinal
            checkpoint.historyReplayComplete = !parsed.inheritsHistory
            checkpoint.model = resolvedModel
            checkpoint.projectPath = project?.id
            checkpoint.projectName = project?.name
            checkpoint.parentSessionID = parentSessionID
            if resolvedID != loadedStateSessionID, let resolvedID {
                normalizationState = try await database.normalizationState(for: resolvedID)
                persistedNormalizationState = normalizationState
                loadedStateSessionID = resolvedID
            }

        case let .taskStarted(task):
            guard checkpoint.inheritsHistory, !checkpoint.historyReplayComplete else { return }
            if let inheritedHistoryEndOrdinal = checkpoint.inheritedHistoryEndOrdinal,
               let ordinal = task.ordinal,
               ordinal > inheritedHistoryEndOrdinal {
                checkpoint.historyReplayComplete = true
                return
            }
            if checkpoint.inheritedHistoryEndOrdinal == nil,
               let sessionStartedAt = checkpoint.sessionStartedAt,
               let taskStartedAt = task.startedAt,
               taskStartedAt.timeIntervalSince1970.rounded(.down)
                   >= sessionStartedAt.timeIntervalSince1970.rounded(.down) {
                checkpoint.historyReplayComplete = true
            }

        case let .turnContext(context):
            if let model = canonicalModelID(context.model) {
                checkpoint.model = model
                metadata = SessionMetadata(
                    id: metadata.id,
                    model: model,
                    workingDirectory: nil,
                    forkedFromID: metadata.forkedFromID,
                    parentThreadID: metadata.parentThreadID,
                    subagentHistoryStartOrdinal: metadata.subagentHistoryStartOrdinal,
                    occurredAt: metadata.occurredAt
                )
            }
            if let project = try await projectProjection(context.workingDirectory) {
                checkpoint.projectPath = project.id
                checkpoint.projectName = project.name
            }

        case let .imageAttachments(attachment):
            guard !checkpoint.inheritsHistory || checkpoint.historyReplayComplete else { return }
            if let importCutoff, attachment.occurredAt <= importCutoff { return }
            let result = checkpoint.imageAttachmentCount.addingReportingOverflow(Int64(attachment.count))
            checkpoint.imageAttachmentCount = result.overflow ? Int64.max : result.partialValue

        case let .token(observation):
            let isInheritedReplay: Bool
            if checkpoint.inheritsHistory, !checkpoint.historyReplayComplete {
                if let inheritedHistoryEndOrdinal = checkpoint.inheritedHistoryEndOrdinal,
                   let ordinal = observation.ordinal,
                   ordinal > inheritedHistoryEndOrdinal {
                    checkpoint.historyReplayComplete = true
                    isInheritedReplay = false
                } else {
                    isInheritedReplay = true
                }
            } else {
                isInheritedReplay = false
            }
            let result = normalizer.normalize(observation, metadata: metadata, state: normalizationState)
            normalizationState = result.state
            guard !isInheritedReplay else { return }
            guard let delta = result.delta, !delta.isZero else { return }
            if let importCutoff, observation.occurredAt <= importCutoff { return }
            let sessionID = checkpoint.sessionID
            pendingEvents.append(
                UsageEvent(
                    eventKey: eventKey(
                        sessionID: sessionID,
                        observation: observation
                    ),
                    occurredAt: observation.occurredAt,
                    sessionID: sessionID,
                    model: checkpoint.model,
                    projectPath: checkpoint.projectPath,
                    usage: delta,
                    sourcePath: storageIdentifier(source.url.standardizedFileURL.path),
                    sourcePosition: position,
                    pricingContext: pricingContext(observation: observation, delta: delta)
                )
            )

        case .malformed:
            normalizationState.quality = .partial

        case .ignored:
            break
        }
    }

    private func eventKey(
        sessionID: String?,
        observation: CodexTokenObservation
    ) -> String {
        let material = [
            "event-v2",
            "session:\(sessionID ?? "unknown")",
            "time:\(observation.occurredAt.timeIntervalSinceReferenceDate.bitPattern)",
            "ordinal:\(observation.ordinal.map(String.init) ?? "none")",
            "last:\(usageIdentity(observation.lastUsage))",
            "cumulative:\(usageIdentity(observation.cumulativeUsage))"
        ].joined(separator: "|")
        return storageIdentifier(Data(material.utf8))
    }

    private func processClaudeLine(
        _ line: Data,
        position: Int64,
        source: CodexSessionSource,
        checkpoint: inout SourceCheckpoint,
        pendingEvents: inout [UsageEvent],
        excludedEventKeys: inout Set<String>,
        importCutoff: Date?
    ) async throws {
        guard let observation = ClaudeJSONLParser().parse(line) else { return }
        let eventKey = storageIdentifier("claude-message|\(observation.messageID)")
        if let importCutoff, observation.occurredAt <= importCutoff {
            excludedEventKeys.insert(eventKey)
            return
        }
        let parentID = storageIdentifier("claude-session|\(observation.sessionID)")
        // Subagent transcripts share the parent sessionId but own their messages.
        let filename = source.url.deletingPathExtension().lastPathComponent
        let agentID = observation.agentID.map { $0.hasPrefix("agent-") ? $0 : "agent-\($0)" }
            ?? (filename.hasPrefix("agent-") ? filename : nil)
        let sessionID = agentID.map {
            storageIdentifier("claude-agent|\(observation.sessionID)|\($0)")
        } ?? parentID
        let project = try await projectProjection(observation.workingDirectory)
        checkpoint.sessionID = sessionID
        checkpoint.parentSessionID = agentID == nil ? nil : parentID
        checkpoint.sessionStartedAt = min(checkpoint.sessionStartedAt ?? observation.occurredAt,
                                          observation.occurredAt)
        checkpoint.model = observation.model
        checkpoint.projectPath = project?.id ?? checkpoint.projectPath
        checkpoint.projectName = project?.name ?? checkpoint.projectName
        pendingEvents.append(UsageEvent(
            // A response may be repeated for text/tool blocks and in copied history.
            // Its message ID, not a line UUID, file path or timestamp, is the identity.
            eventKey: eventKey,
            occurredAt: observation.occurredAt,
            sessionID: sessionID,
            model: observation.model,
            projectPath: checkpoint.projectPath,
            usage: observation.usage,
            sourcePath: storageIdentifier(source.url.standardizedFileURL.path),
            sourcePosition: position
        ))
    }

    private func usageIdentity(_ usage: TokenUsage?) -> String {
        guard let usage else { return "none" }
        // Keep the v2 identity compatible with Phase 1 databases. Cache-write
        // tokens are enrichment metadata for the same source observation, not a
        // reason to create a second accounting event during the v15 backfill.
        return "\(usage.inputTokens),\(usage.cachedInputTokens),\(usage.outputTokens)"
    }

    private func pricingContext(
        observation: CodexTokenObservation,
        delta: TokenUsage
    ) -> PricingContext? {
        guard delta.inputTokens > PricingContext.highContextInputThreshold else { return .standard }
        guard observation.lastUsage == delta else { return nil }
        return .highContext
    }

    private func sessionIdentifierFromFilename(_ url: URL) -> String? {
        let stem = url.deletingPathExtension().lastPathComponent
        guard stem.count >= 36 else { return nil }
        let suffix = String(stem.suffix(36))
        guard let identifier = UUID(uuidString: suffix)?.uuidString.lowercased() else { return nil }
        return storageIdentifier(identifier)
    }

    private func storageIdentifier(_ value: String) -> String {
        storageIdentifier(Data(value.utf8))
    }

    private func storageIdentifier(_ value: Data) -> String {
        SHA256.hash(data: value).map { String(format: "%02x", $0) }.joined()
    }

    private func canonicalModelID(_ value: String?) -> String? {
        guard let value else { return nil }
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !normalized.isEmpty,
              normalized.utf8.count <= 128,
              normalized.unicodeScalars.allSatisfy({
                  CharacterSet.alphanumerics.contains($0) || ".-_".unicodeScalars.contains($0)
              })
        else { return nil }
        return normalized
    }

    private func projectProjection(_ workingDirectory: String?) async throws -> (id: String, name: String)? {
        guard let workingDirectory,
              !workingDirectory.isEmpty,
              workingDirectory.utf8.count <= 4_096,
              !workingDirectory.contains("\0")
        else { return nil }

        let standardizedPath = URL(fileURLWithPath: workingDirectory).standardizedFileURL.path
        let name = URL(fileURLWithPath: standardizedPath).lastPathComponent
        guard !name.isEmpty,
              name != ".",
              name != "..",
              name.utf8.count <= 128,
              !name.contains("\0")
        else { return nil }

        let key = SymmetricKey(data: try await sourceFingerprintKey())
        let material = Data("CodexMeter.project.v1|\(standardizedPath)".utf8)
        let digest = HMAC<SHA256>.authenticationCode(for: material, using: key)
            .map { String(format: "%02x", $0) }
            .joined()
        return (digest, name)
    }

#if DEBUG
    func projectProjectionForTesting(_ workingDirectory: String) async throws -> (id: String, name: String)? {
        try await projectProjection(workingDirectory)
    }
#endif
}

private extension Data {
    func containsASCII(_ string: String) -> Bool {
        range(of: Data(string.utf8)) != nil
    }
}
