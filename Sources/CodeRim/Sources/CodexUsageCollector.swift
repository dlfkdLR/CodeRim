import CodeRimUsageCore
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
    let analyticsRevision: UInt64
}

struct DataStatistics: Equatable, Sendable {
    let databaseBytes: Int64
    let oldestRecord: Date?
    let newestRecord: Date?

    static let empty = DataStatistics(databaseBytes: 0, oldestRecord: nil, newestRecord: nil)
}

private struct SourceProcessResult {
    let processedBytes: Int64
    let fingerprintBytesRead: Int64
    let hasMore: Bool
}

private enum CollectorResourceLimits {
    // Streaming buffers remain 256 KiB; fewer continuation passes avoid
    // repeated discovery and aggregation while authenticating a large prefix.
    static let maximumBytesPerRefresh: Int64 = 128 * 1_024 * 1_024
    static let maximumParsingBytesPerSource: Int64 = 16 * 1_024 * 1_024
    /// A work budget per pass, not a hard deadline: no new source is started after it, and a source in
    /// progress stops at its next line boundary; byte limits bound how far any one read can run past it.
    static let maximumRefreshDuration: Duration = .seconds(5)
}

actor CodexUsageCollector {
    @TaskLocal static var afterSourceReadForTesting: (@Sendable (URL, Int64) throws -> Void)?

    private let provider: UsageProvider
    private let database: SQLiteDatabase
    private let roots: [URL]
    private let discovery = CodexSourceDiscovery()
    private let maximumBytesPerRefresh: Int64
    private let maximumRefreshDuration: Duration
    private var sourceFingerprintKeyData: Data?
    private let sourceReader = SourceIntegrityReader()
    private var nextSourcePath: String?
    /// The last per-source write the database rejected; kept for diagnostics, never shown as a number.
    private(set) var lastSourceWriteFailure: String?
    private var analyticsRevision: UInt64 = 0
    private var lastImportEpoch: Int64?

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
        // The pass budget starts before discovery, so listing many sources counts against it too.
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: maximumRefreshDuration)
        var sources = try discovery.discover(in: roots)
        if let nextSourcePath, let start = sources.firstIndex(where: { $0.url.path == nextSourcePath }) {
            sources = Array(sources[start...]) + Array(sources[..<start])
        }
        let activeCheckpointKeys = Set(
            sources.map { UsageStorageIdentity.digest($0.url.standardizedFileURL.path) }
        )
        sourceReader.prune(activeKeys: activeCheckpointKeys)
        let importPolicy = try await database.importPolicy()
        if lastImportEpoch != importPolicy.dataEpoch {
            analyticsRevision &+= 1
            lastImportEpoch = importPolicy.dataEpoch
        }
        var processedBytes: Int64 = 0
        var fingerprintBytesRead: Int64 = 0
        var hasMoreWork = false
        var skippedSource = false
        var constrainedSourcePath: String?
        for (index, source) in sources.enumerated() {
            try Task.checkCancellation()
            let remainingBytes = maximumBytesPerRefresh - processedBytes - fingerprintBytesRead
            // The first source always gets a turn, so a slow discovery cannot stall progress for good.
            guard remainingBytes > 0, index == 0 || clock.now < deadline else {
                hasMoreWork = true
                break
            }
            nextSourcePath = sources[(index + 1) % sources.count].url.path
            // Keep one complete-line read available for another source. A large
            // prefix verification must not consume the entire pass before a
            // small live transcript can publish its newly appended usage.
            let minimumSourceBudget = Int64((CodexJSONLParser.maximumLineBytes + 1) * 2)
            let reservedBytes = index + 1 < sources.count && remainingBytes >= minimumSourceBudget * 2
                ? minimumSourceBudget : 0
            do {
                let result = try await process(
                    source,
                    importCutoff: importPolicy.cutoff,
                    expectedEpoch: importPolicy.dataEpoch,
                    maximumBytes: remainingBytes - reservedBytes,
                    deadline: deadline
                )
                processedBytes += result.processedBytes
                fingerprintBytesRead += result.fingerprintBytesRead
                if result.hasMore {
                    hasMoreWork = true
                    // A source that only received the remainder gets a full
                    // budget next time, even if it keeps growing between passes.
                    if remainingBytes < maximumBytesPerRefresh, constrainedSourcePath == nil {
                        constrainedSourcePath = source.url.path
                    }
                }
            } catch is SourceReadError {
                skippedSource = true
            } catch let SQLiteDatabaseError.step(message) {
                // One source's rejected write must not stop the other sources or the refresh;
                // its checkpoint stays put and the period is reported as partial.
                skippedSource = true
                lastSourceWriteFailure = message
            }
        }

        if let constrainedSourcePath { nextSourcePath = constrainedSourcePath }

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
            maintenanceWarning: nil,
            analyticsRevision: analyticsRevision
        )
    }

    func rebuild(now: Date = Date(), calendar: Calendar = .current, weekStart: WeekStart) async throws -> CollectorRefreshResult {
        try await database.rebuildStatistics()
        analyticsRevision &+= 1
        return try await refresh(now: now, calendar: calendar, weekStart: weekStart)
    }

    func clearLocalHistory(at cutoff: Date = Date(), calendar: Calendar = .current, weekStart: WeekStart) async throws -> CollectorRefreshResult {
        sourceReader.reset()
        let compactionStatus = try await database.clearLocalHistory(
            at: cutoff, preservesMessageExclusions: provider == .claude
        )
        analyticsRevision &+= 1
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
                : nil,
            analyticsRevision: result.analyticsRevision
        )
    }

    private func process(
        _ source: CodexSessionSource,
        importCutoff: Date?,
        expectedEpoch: Int64,
        maximumBytes: Int64,
        deadline: ContinuousClock.Instant
    ) async throws -> SourceProcessResult {
        let checkpointKey = UsageStorageIdentity.digest(source.url.standardizedFileURL.path)
        var checkpoint = try await database.checkpoint(for: checkpointKey)
            ?? SourceCheckpoint.fresh(sourcePath: checkpointKey, fileIdentity: source.identity)
        let previousCheckpoint = checkpoint
        let fingerprintKey = try await sourceFingerprintKey()
        var fingerprintBytesRead: Int64 = 0

        let opened = try sourceReader.open(source)
        let liveHandle = opened.handle
        defer { try? liveHandle.close() }
        var handle = liveHandle
        var openedStat = opened.metadata
        switch sourceReader.selectRead(key: checkpointKey, descriptor: liveHandle.fileDescriptor,
            metadata: openedStat, checkpoint: checkpoint, snapshotThreshold: maximumBytesPerRefresh / 2) {
        case .live: break
        case .snapshot(let snapshot):
            handle = snapshot.handle
            openedStat = snapshot.metadata
        case .deferred:
            return SourceProcessResult(processedBytes: 0, fingerprintBytesRead: 0, hasMore: true)
        }
        var snapshotCompleted = false
        defer { sourceReader.finishRead(key: checkpointKey, completed: snapshotCompleted) }
        func liveHasBytes(after offset: Int64) -> Bool {
            var current = stat()
            return fstat(liveHandle.fileDescriptor, &current) == 0 && Int64(current.st_size) > offset
        }
        let openedSize = Int64(openedStat.st_size)
        let openedModificationTime = sourceReader.modificationTimeNanoseconds(openedStat)
        let openedStatusChangeTime = sourceReader.statusChangeTimeNanoseconds(openedStat)

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

        var fingerprintAccumulator = sourceReader.accumulator(key: checkpointKey, checkpoint: checkpoint,
            keyData: fingerprintKey, metadata: openedStat)
        if checkpoint.committedOffset > 0,
           openedSize >= checkpoint.committedOffset {
            let advance = try fingerprintAccumulator.advance(
                fileDescriptor: handle.fileDescriptor,
                to: checkpoint.committedOffset,
                maximumBytes: maximumBytes
            )
            fingerprintBytesRead += advance.bytesRead
            guard advance.reachedTarget else {
                sourceReader.cacheFingerprintState(
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
            try sourceReader.verifyOpenedSourceState(
                fileDescriptor: handle.fileDescriptor,
                expectedIdentity: source.identity,
                expectedSize: openedSize,
                expectedModificationTime: openedModificationTime,
                expectedStatusChangeTime: openedStatusChangeTime
            )
            sourceReader.invalidateFingerprint(checkpointKey)
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
                sourceReader.invalidateFingerprint(checkpointKey)
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
                if analyticsMetadataChanged(checkpoint, from: previousCheckpoint) {
                    analyticsRevision &+= 1
                }
            }
            sourceReader.cacheFingerprintState(
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
        let scanByteBudget = min(remainingIOBudget / 2, CollectorResourceLimits.maximumParsingBytesPerSource)
        checkpoint.observedSize = openedSize
        checkpoint.modificationTimeNanoseconds = openedModificationTime
        guard openedSize > checkpoint.committedOffset else {
            snapshotCompleted = true
            checkpoint.hasPendingImport = liveHasBytes(after: checkpoint.committedOffset)
            if checkpoint.contentFingerprint.isEmpty {
                fingerprintBytesRead += try sourceReader.updateCheckpointFingerprint(
                    &checkpoint,
                    accumulator: &fingerprintAccumulator,
                    expectedFingerprint: fingerprintAccumulator.fingerprint,
                    expectedStatusChangeTime: openedStatusChangeTime,
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
                if analyticsMetadataChanged(checkpoint, from: previousCheckpoint) {
                    analyticsRevision &+= 1
                }
            }
            sourceReader.cacheFingerprintState(
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
            checkpoint.sessionID = UsageStorageIdentity.sessionIdentifierFromFilename(source.url)
        }

        let initialState = if let sessionID = checkpoint.sessionID {
            try await database.normalizationState(for: sessionID)
        } else {
            UsageNormalizationState.empty
        }
        var session = SourceImportSession(checkpoint: checkpoint, state: initialState, provider: provider,
            sourceURL: source.url, importCutoff: importCutoff, projectKey: fingerprintKey,
            loadState: { [database] id in try await database.normalizationState(for: id) })
        var completedLineCount = 0
        let startingOffset = session.checkpoint.committedOffset
        var parsedFingerprintAccumulator = fingerprintAccumulator
        guard Darwin.lseek(handle.fileDescriptor, off_t(startingOffset), SEEK_SET) == off_t(startingOffset) else {
            throw SourceReadError.sourceUnavailable
        }

        var lineBuffer = Data()
        lineBuffer.reserveCapacity(64 * 1024)
        var lineStartOffset = session.checkpoint.committedOffset
        var readOffset = session.checkpoint.committedOffset
        var skippingOversizedLine = session.checkpoint.isSkippingOversizedLine
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
                throw SourceReadError.sourceUnavailable
            }
            guard !chunk.isEmpty else { break }
            try Self.afterSourceReadForTesting?(source.url, readOffset)
            try Task.checkCancellation()
            let chunkStartOffset = readOffset
            readOffset += Int64(chunk.count)

            if skippingOversizedLine {
                if let newline = chunk.firstIndex(of: 0x0A) {
                    let afterNewline = chunk.index(after: newline)
                    let consumed = chunk.distance(from: chunk.startIndex, to: afterNewline)
                    parsedFingerprintAccumulator.append(chunk[..<afterNewline])
                    session.checkpoint.committedOffset = chunkStartOffset + Int64(consumed)
                    session.checkpoint.isSkippingOversizedLine = false
                    lineStartOffset = session.checkpoint.committedOffset
                    skippingOversizedLine = false
                    lineBuffer.append(contentsOf: chunk[afterNewline...])
                } else {
                    parsedFingerprintAccumulator.append(chunk)
                    continue
                }
            } else {
                lineBuffer.append(chunk)
            }

            var consumedThrough = lineBuffer.startIndex
            while let newline = lineBuffer[consumedThrough...].firstIndex(of: 0x0A) {
                let line = Data(lineBuffer[consumedThrough..<newline])
                let linePosition = lineStartOffset + Int64(lineBuffer.distance(from: lineBuffer.startIndex, to: consumedThrough))
                try await session.consume(line, position: linePosition)
                completedLineCount += 1
                let afterNewline = lineBuffer.index(after: newline)
                parsedFingerprintAccumulator.append(lineBuffer[consumedThrough..<afterNewline])
                consumedThrough = afterNewline
                session.checkpoint.committedOffset = lineStartOffset
                    + Int64(lineBuffer.distance(from: lineBuffer.startIndex, to: consumedThrough))

                if completedLineCount >= 1_000 || session.pendingEvents.count >= 256 {
                    fingerprintBytesRead += try sourceReader.updateCheckpointFingerprint(
                        &session.checkpoint,
                        accumulator: &fingerprintAccumulator,
                        expectedFingerprint: parsedFingerprintAccumulator.fingerprint,
                        expectedStatusChangeTime: openedStatusChangeTime,
                        fileDescriptor: handle.fileDescriptor
                    )
                    let committedNormalizationState = try await database.commit(
                        events: session.pendingEvents,
                        checkpoint: session.checkpoint,
                        normalizationState: session.normalizationState,
                        expectedEpoch: expectedEpoch,
                        expectedNormalizationState: session.checkpoint.sessionID == nil ? nil : session.persistedNormalizationState,
                        mergesMessageUsage: provider == .claude,
                        excludedEventKeys: session.excludedEventKeys,
                        partialEvidence: session.pendingEvidence
                    )
                    if !session.pendingEvents.isEmpty || !session.excludedEventKeys.isEmpty || !session.pendingEvidence.isEmpty
                        || analyticsMetadataChanged(session.checkpoint, from: previousCheckpoint) {
                        analyticsRevision &+= 1
                    }
                    if let committedNormalizationState {
                        session.persistedNormalizationState = committedNormalizationState
                        if session.normalizationState.cumulativeHighWaterMark != nil {
                            session.normalizationState = committedNormalizationState
                        }
                    }
                    session.pendingEvidence.removeAll(keepingCapacity: true)
                    session.pendingEvents.removeAll(keepingCapacity: true)
                    session.excludedEventKeys.removeAll(keepingCapacity: true)
                    completedLineCount = 0
                }
            }

            if consumedThrough != lineBuffer.startIndex {
                let consumedCount = lineBuffer.distance(from: lineBuffer.startIndex, to: consumedThrough)
                lineBuffer.removeSubrange(lineBuffer.startIndex..<consumedThrough)
                lineStartOffset += Int64(consumedCount)
            }

            if lineBuffer.count > CodexJSONLParser.maximumLineBytes {
                parsedFingerprintAccumulator.append(lineBuffer)
                session.normalizationState.quality = .partial
                // Too long to read. If what was read of it is an accounting row (a Codex token count, or a
                // Claude assistant message, whose usage comes after its content), its tokens are missing.
                let marker = provider == .claude ? "\"type\":\"assistant\"" : "\"token_count\""
                if lineBuffer.range(of: Data(marker.utf8)) != nil {
                    session.recordEvidence(position: lineStartOffset, occurredAt: nil)
                }
                lineBuffer.removeAll(keepingCapacity: false)
                skippingOversizedLine = true
                session.checkpoint.isSkippingOversizedLine = true
            }
        }

        if skippingOversizedLine {
            session.checkpoint.committedOffset = readOffset
        }
        var finalStat = stat()
        snapshotCompleted = !stoppedForBudget
        let sourceGrewWhileReading = fstat(liveHandle.fileDescriptor, &finalStat) == 0
            && Int64(finalStat.st_size) > readOffset
        session.checkpoint.hasPendingImport = stoppedForBudget || sourceGrewWhileReading

        if session.checkpoint.committedOffset > startingOffset || session.checkpoint != previousCheckpoint {
            fingerprintBytesRead += try sourceReader.updateCheckpointFingerprint(
                &session.checkpoint,
                accumulator: &fingerprintAccumulator,
                expectedFingerprint: parsedFingerprintAccumulator.fingerprint,
                expectedStatusChangeTime: openedStatusChangeTime,
                fileDescriptor: handle.fileDescriptor
            )
            let committedNormalizationState = try await database.commit(
                events: session.pendingEvents,
                checkpoint: session.checkpoint,
                normalizationState: session.normalizationState,
                expectedEpoch: expectedEpoch,
                expectedNormalizationState: session.checkpoint.sessionID == nil ? nil : session.persistedNormalizationState,
                mergesMessageUsage: provider == .claude,
                excludedEventKeys: session.excludedEventKeys,
                partialEvidence: session.pendingEvidence
            )
            if !session.pendingEvents.isEmpty || !session.excludedEventKeys.isEmpty || !session.pendingEvidence.isEmpty
                || analyticsMetadataChanged(session.checkpoint, from: previousCheckpoint) {
                analyticsRevision &+= 1
            }
            session.pendingEvidence.removeAll(keepingCapacity: true)
            if let committedNormalizationState,
               session.normalizationState.cumulativeHighWaterMark != nil {
                session.normalizationState = committedNormalizationState
            }
        }
        sourceReader.cacheFingerprintState(
            checkpointKey: checkpointKey,
            checkpoint: session.checkpoint,
            observedSize: session.checkpoint.observedSize,
            modificationTimeNanoseconds: session.checkpoint.modificationTimeNanoseconds,
            fileDescriptor: handle.fileDescriptor,
            accumulator: fingerprintAccumulator
        )
        return SourceProcessResult(
            processedBytes: readOffset - startingOffset,
            fingerprintBytesRead: fingerprintBytesRead,
            hasMore: stoppedForBudget || sourceGrewWhileReading
        )
    }

    private func analyticsMetadataChanged(_ current: SourceCheckpoint, from previous: SourceCheckpoint) -> Bool {
        current.sessionID != previous.sessionID || current.model != previous.model
            || current.projectPath != previous.projectPath || current.projectName != previous.projectName
            || current.parentSessionID != previous.parentSessionID
            || current.sessionStartedAt != previous.sessionStartedAt
            || current.imageAttachmentCount != previous.imageAttachmentCount
            || current.inheritsHistory != previous.inheritsHistory
    }

    private func sourceFingerprintKey() async throws -> Data {
        if let sourceFingerprintKeyData { return sourceFingerprintKeyData }
        let key = await database.sourceFingerprintKey()
        sourceFingerprintKeyData = key
        return key
    }

    private func projectProjection(_ workingDirectory: String?) async throws -> (id: String, name: String)? {
        UsageStorageIdentity.project(workingDirectory, keyData: try await sourceFingerprintKey())
    }

#if DEBUG
    func projectProjectionForTesting(_ workingDirectory: String) async throws -> (id: String, name: String)? {
        try await projectProjection(workingDirectory)
    }
#endif
}
