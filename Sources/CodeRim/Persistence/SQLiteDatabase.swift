import CSQLite
import CryptoKit
import Darwin
import Foundation

private let sqliteProcessStartupLock = NSLock()

struct SourceCheckpoint: Equatable, Sendable {
    let sourcePath: String
    var fileIdentity: String
    var generation: Int64
    var committedOffset: Int64
    var isSkippingOversizedLine = false
    var observedSize: Int64 = 0
    var modificationTimeNanoseconds: Int64 = 0
    var contentFingerprint = ""
    var hasPendingImport = false
    var sessionID: String?
    var inheritsHistory: Bool
    var sessionStartedAt: Date?
    var inheritedHistoryEndOrdinal: Int64?
    var historyReplayComplete: Bool = true
    var model: String?
    var projectPath: String?
    var parentSessionID: String?
    var projectName: String?
    var imageAttachmentCount: Int64 = 0

    static func fresh(sourcePath: String, fileIdentity: String, generation: Int64 = 0) -> SourceCheckpoint {
        SourceCheckpoint(
            sourcePath: sourcePath,
            fileIdentity: fileIdentity,
            generation: generation,
            committedOffset: 0,
            isSkippingOversizedLine: false,
            observedSize: 0,
            modificationTimeNanoseconds: 0,
            contentFingerprint: "",
            hasPendingImport: false,
            sessionID: nil,
            inheritsHistory: false,
            sessionStartedAt: nil,
            inheritedHistoryEndOrdinal: nil,
            historyReplayComplete: true,
            model: nil,
            projectPath: nil,
            parentSessionID: nil,
            projectName: nil,
            imageAttachmentCount: 0
        )
    }
}

private struct ModelUsageAccumulator {
    var usage = TokenUsage.zero
    var highContextUsage = TokenUsage.zero
    var hasUnknownPricingContext = false

    mutating func add(_ value: TokenUsage, pricingContext: PricingContext?) {
        usage = usage.adding(value)
        switch pricingContext {
        case .highContext:
            highContextUsage = highContextUsage.adding(value)
        case .standard:
            break
        case nil:
            if value.inputTokens > PricingContext.highContextInputThreshold {
                hasUnknownPricingContext = true
            }
        }
    }

    func summary(modelID: String?) -> ModelUsageSummary {
        ModelUsageSummary(
            modelID: modelID,
            usage: usage,
            highContextUsage: highContextUsage,
            hasUnknownPricingContext: hasUnknownPricingContext
        )
    }
}

struct ImportPolicy: Equatable, Sendable {
    let cutoff: Date?
    let dataEpoch: Int64
}

enum ClearHistoryCompactionStatus: Equatable, Sendable {
    case complete
    case deferred
}

enum SQLiteDatabaseError: Error, LocalizedError {
    case open(String)
    case statement(String)
    case step(String)
    case migration(String)
    case resourceLimit(String)
    case staleScan

    var errorDescription: String? {
        switch self {
        case let .open(message): "Unable to open usage database: \(message)"
        case let .statement(message): "Database statement failed: \(message)"
        case let .step(message): "Database operation failed: \(message)"
        case let .migration(message): "Database migration failed: \(message)"
        case let .resourceLimit(message): message
        case .staleScan: "The source scan was superseded by a data maintenance operation"
        }
    }
}

actor SQLiteDatabase {
    static let maximumDatabaseBytes: Int64 = 1_073_741_824

    private let databaseURL: URL
    private let databaseByteLimit: Int64
    private let sourceFingerprintKeyData: Data
    private var connection: SQLiteConnection?

    init(url: URL, maximumDatabaseBytes: Int64 = SQLiteDatabase.maximumDatabaseBytes) throws {
        sqliteProcessStartupLock.lock()
        defer { sqliteProcessStartupLock.unlock() }

        databaseURL = url
        databaseByteLimit = max(1, maximumDatabaseBytes)
        let directory = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true
        )
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o700],
            ofItemAtPath: directory.path
        )

        let lockPath = url.path + ".startup.lock"
        let lockDescriptor = Darwin.open(
            lockPath,
            O_CREAT | O_RDWR | O_CLOEXEC | O_NOFOLLOW,
            S_IRUSR | S_IWUSR
        )
        guard lockDescriptor >= 0 else {
            throw SQLiteDatabaseError.open("unable to create the database startup lock")
        }
        defer {
            flock(lockDescriptor, LOCK_UN)
            Darwin.close(lockDescriptor)
        }
        guard flock(lockDescriptor, LOCK_EX) == 0 else {
            throw SQLiteDatabaseError.open("unable to acquire the database startup lock")
        }
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o600],
            ofItemAtPath: lockPath
        )
        sourceFingerprintKeyData = try Self.loadOrCreateSourceFingerprintKey(at: url)

        var database: OpaquePointer?
        let flags = SQLITE_OPEN_CREATE | SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX
        guard sqlite3_open_v2(url.path, &database, flags, nil) == SQLITE_OK, let database else {
            let message = database.flatMap { sqlite3_errmsg($0) }.map(String.init(cString:)) ?? "unknown error"
            if let database { sqlite3_close(database) }
            throw SQLiteDatabaseError.open(message)
        }

        connection = SQLiteConnection(rawValue: database)
        do {
            try Self.configure(database)
            try Self.migrate(database)
            try Self.protectDatabaseFiles(at: url)
        } catch {
            sqlite3_close(database)
            connection = nil
            throw error
        }
    }

    deinit {
        if let connection {
            sqlite3_close(connection.rawValue)
        }
    }

    func checkpoint(for sourcePath: String) throws -> SourceCheckpoint? {
        let statement = try prepare(
            """
            SELECT file_identity, generation, committed_offset, skipping_oversized_line,
                   observed_size, modification_time_ns, content_fingerprint, has_pending_import,
                   session_id, inherits_history, session_started_at,
                   inherited_history_end_ordinal, history_replay_complete, model, project_path,
                   parent_session_id, project_name, image_attachment_count
            FROM parsing_state
            WHERE source_path = ?1
            """
        )
        defer { sqlite3_finalize(statement) }
        try bind(sourcePath, at: 1, to: statement)

        switch sqlite3_step(statement) {
        case SQLITE_ROW:
            return SourceCheckpoint(
                sourcePath: sourcePath,
                fileIdentity: text(statement, column: 0) ?? "",
                generation: sqlite3_column_int64(statement, 1),
                committedOffset: sqlite3_column_int64(statement, 2),
                isSkippingOversizedLine: sqlite3_column_int(statement, 3) != 0,
                observedSize: sqlite3_column_int64(statement, 4),
                modificationTimeNanoseconds: sqlite3_column_int64(statement, 5),
                contentFingerprint: text(statement, column: 6) ?? "",
                hasPendingImport: sqlite3_column_int(statement, 7) != 0,
                sessionID: text(statement, column: 8),
                inheritsHistory: sqlite3_column_int(statement, 9) != 0,
                sessionStartedAt: sqlite3_column_type(statement, 10) == SQLITE_NULL
                    ? nil
                    : Date(timeIntervalSince1970: sqlite3_column_double(statement, 10)),
                inheritedHistoryEndOrdinal: sqlite3_column_type(statement, 11) == SQLITE_NULL
                    ? nil
                    : sqlite3_column_int64(statement, 11),
                historyReplayComplete: sqlite3_column_int(statement, 12) != 0,
                model: text(statement, column: 13),
                projectPath: text(statement, column: 14),
                parentSessionID: text(statement, column: 15),
                projectName: text(statement, column: 16),
                imageAttachmentCount: sqlite3_column_int64(statement, 17)
            )
        case SQLITE_DONE:
            return nil
        default:
            throw SQLiteDatabaseError.step(errorMessage)
        }
    }

    func sourceFingerprintKey() -> Data {
        sourceFingerprintKeyData
    }

    func normalizationState(for sessionID: String) throws -> UsageNormalizationState {
        try normalizationStateWithinTransaction(for: sessionID)
    }

    private func normalizationStateWithinTransaction(for sessionID: String) throws -> UsageNormalizationState {
        let statement = try prepare(
            """
            SELECT input_tokens, cached_input_tokens, cache_write_input_tokens,
                   output_tokens, last_observed_at, quality
            FROM session_counters
            WHERE session_id = ?1
            """
        )
        defer { sqlite3_finalize(statement) }
        try bind(sessionID, at: 1, to: statement)

        switch sqlite3_step(statement) {
        case SQLITE_ROW:
            return UsageNormalizationState(
                cumulativeHighWaterMark: TokenUsage(
                    inputTokens: sqlite3_column_int64(statement, 0),
                    cachedInputTokens: sqlite3_column_int64(statement, 1),
                    cacheWriteInputTokens: sqlite3_column_type(statement, 2) == SQLITE_NULL
                        ? nil
                        : sqlite3_column_int64(statement, 2),
                    outputTokens: sqlite3_column_int64(statement, 3)
                ),
                lastObservedAt: sqlite3_column_type(statement, 4) == SQLITE_NULL
                    ? nil
                    : Date(timeIntervalSince1970: sqlite3_column_double(statement, 4)),
                quality: DataQuality(rawValue: text(statement, column: 5) ?? "") ?? .partial
            )
        case SQLITE_DONE:
            return .empty
        default:
            throw SQLiteDatabaseError.step(errorMessage)
        }
    }

    @discardableResult
    func commit(
        events: [UsageEvent],
        checkpoint: SourceCheckpoint,
        normalizationState: UsageNormalizationState?,
        expectedEpoch: Int64? = nil,
        expectedNormalizationState: UsageNormalizationState? = nil,
        mergesMessageUsage: Bool = false,
        excludedEventKeys: Set<String> = []
    ) throws -> UsageNormalizationState? {
        try execute("BEGIN IMMEDIATE")
        do {
            if let expectedEpoch, try dataEpochWithinTransaction() != expectedEpoch {
                throw SQLiteDatabaseError.staleScan
            }
            if let expectedNormalizationState, let sessionID = checkpoint.sessionID,
               try normalizationStateWithinTransaction(for: sessionID) != expectedNormalizationState {
                throw SQLiteDatabaseError.staleScan
            }
            if (!events.isEmpty || !excludedEventKeys.isEmpty),
               try allocatedDatabaseBytesWithinTransaction() >= databaseByteLimit {
                throw SQLiteDatabaseError.resourceLimit(
                    "The local CodeRim database reached its 1 GB safety limit"
                )
            }
            if mergesMessageUsage {
                try prepareMessageExclusions()
                try excludeMessages(excludedEventKeys)
            }
            var insertedEventCount = 0
            // Claude streaming blocks describe one response repeatedly. Preserve
            // maxima of its disjoint components, never sum repeated snapshots.
            // Codex retains its existing cumulative-delta conflict behavior.
            let messageMerge = mergesMessageUsage ? """
                input_tokens =
                    MAX(usage_events.input_tokens - usage_events.cached_input_tokens - COALESCE(usage_events.cache_write_input_tokens, 0),
                        excluded.input_tokens - excluded.cached_input_tokens - COALESCE(excluded.cache_write_input_tokens, 0))
                    + MAX(usage_events.cached_input_tokens, excluded.cached_input_tokens)
                    + MAX(COALESCE(usage_events.cache_write_input_tokens, 0), COALESCE(excluded.cache_write_input_tokens, 0)),
                cached_input_tokens = MAX(usage_events.cached_input_tokens, excluded.cached_input_tokens),
                output_tokens = MAX(usage_events.output_tokens, excluded.output_tokens),
                occurred_at = MIN(usage_events.occurred_at, excluded.occurred_at),
                """ : ""
            let cacheWriteMerge = mergesMessageUsage
                ? "MAX(COALESCE(usage_events.cache_write_input_tokens, 0), COALESCE(excluded.cache_write_input_tokens, 0))"
                : "COALESCE(excluded.cache_write_input_tokens, usage_events.cache_write_input_tokens)"
            let sessionMerge = mergesMessageUsage
                ? "CASE WHEN excluded.occurred_at < usage_events.occurred_at THEN COALESCE(excluded.session_id, usage_events.session_id) ELSE COALESCE(usage_events.session_id, excluded.session_id) END"
                : "COALESCE(excluded.session_id, usage_events.session_id)"
            let projectMerge = mergesMessageUsage
                ? "CASE WHEN excluded.occurred_at < usage_events.occurred_at THEN COALESCE(excluded.project_path, usage_events.project_path) ELSE COALESCE(usage_events.project_path, excluded.project_path) END"
                : "COALESCE(excluded.project_path, usage_events.project_path)"
            let values = "?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13"
            let insertValues = mergesMessageUsage
                ? "SELECT \(values) WHERE NOT EXISTS (SELECT 1 FROM claude_message_exclusions WHERE event_key = ?1)"
                : "VALUES (\(values))"
            let insert = try prepare(
                """
                INSERT INTO usage_events (
                    event_key, occurred_at, session_id, model, project_path,
                    input_tokens, cached_input_tokens, cache_write_input_tokens, output_tokens,
                    high_context_pricing, source_path, source_generation, source_position
                ) \(insertValues)
                ON CONFLICT DO UPDATE SET
                    \(messageMerge)
                    session_id = \(sessionMerge),
                    model = COALESCE(excluded.model, usage_events.model),
                    project_path = \(projectMerge),
                    cache_write_input_tokens = \(cacheWriteMerge),
                    high_context_pricing = COALESCE(
                        excluded.high_context_pricing,
                        usage_events.high_context_pricing
                    )
                """
            )
            defer { sqlite3_finalize(insert) }

            for event in events {
                sqlite3_reset(insert)
                sqlite3_clear_bindings(insert)
                try bind(event.eventKey, at: 1, to: insert)
                sqlite3_bind_double(insert, 2, event.occurredAt.timeIntervalSince1970)
                try bind(event.sessionID, at: 3, to: insert)
                try bind(event.model, at: 4, to: insert)
                try bind(event.projectPath, at: 5, to: insert)
                sqlite3_bind_int64(insert, 6, event.usage.inputTokens)
                sqlite3_bind_int64(insert, 7, event.usage.cachedInputTokens)
                if let cacheWrite = event.usage.cacheWriteInputTokens {
                    sqlite3_bind_int64(insert, 8, cacheWrite)
                } else {
                    sqlite3_bind_null(insert, 8)
                }
                sqlite3_bind_int64(insert, 9, event.usage.outputTokens)
                if let pricingContext = event.pricingContext {
                    sqlite3_bind_int64(insert, 10, Int64(pricingContext.rawValue))
                } else {
                    sqlite3_bind_null(insert, 10)
                }
                try bind(event.sourcePath, at: 11, to: insert)
                sqlite3_bind_int64(insert, 12, checkpoint.generation)
                sqlite3_bind_int64(insert, 13, event.sourcePosition)
                guard sqlite3_step(insert) == SQLITE_DONE else {
                    throw SQLiteDatabaseError.step(errorMessage)
                }
                if let connection {
                    insertedEventCount += Int(sqlite3_changes(connection.rawValue))
                }
            }

            if (events.isEmpty || insertedEventCount > 0),
               let sessionID = checkpoint.sessionID, let normalizationState,
               let highWaterMark = normalizationState.cumulativeHighWaterMark {
                let counter = try prepare(
                    """
                    INSERT INTO session_counters (
                        session_id, input_tokens, cached_input_tokens, output_tokens,
                        cache_write_input_tokens, last_observed_at, quality
                    ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
                    ON CONFLICT(session_id) DO UPDATE SET
                        input_tokens = excluded.input_tokens,
                        cached_input_tokens = excluded.cached_input_tokens,
                        output_tokens = excluded.output_tokens,
                        cache_write_input_tokens = excluded.cache_write_input_tokens,
                        last_observed_at = excluded.last_observed_at,
                        quality = excluded.quality
                    WHERE session_counters.last_observed_at IS NULL
                       OR excluded.last_observed_at > session_counters.last_observed_at
                       OR (
                            excluded.last_observed_at = session_counters.last_observed_at
                            AND excluded.input_tokens >= session_counters.input_tokens
                            AND excluded.cached_input_tokens >= session_counters.cached_input_tokens
                            AND excluded.output_tokens >= session_counters.output_tokens
                            AND (
                                excluded.cache_write_input_tokens IS NULL
                                OR session_counters.cache_write_input_tokens IS NULL
                                OR excluded.cache_write_input_tokens >= session_counters.cache_write_input_tokens
                            )
                       )
                    """
                )
                defer { sqlite3_finalize(counter) }
                try bind(sessionID, at: 1, to: counter)
                sqlite3_bind_int64(counter, 2, highWaterMark.inputTokens)
                sqlite3_bind_int64(counter, 3, highWaterMark.cachedInputTokens)
                sqlite3_bind_int64(counter, 4, highWaterMark.outputTokens)
                if let cacheWrite = highWaterMark.cacheWriteInputTokens {
                    sqlite3_bind_int64(counter, 5, cacheWrite)
                } else {
                    sqlite3_bind_null(counter, 5)
                }
                if let lastObservedAt = normalizationState.lastObservedAt {
                    sqlite3_bind_double(counter, 6, lastObservedAt.timeIntervalSince1970)
                } else {
                    sqlite3_bind_null(counter, 6)
                }
                try bind(normalizationState.quality.rawValue, at: 7, to: counter)
                guard sqlite3_step(counter) == SQLITE_DONE else {
                    throw SQLiteDatabaseError.step(errorMessage)
                }
            }

            let state = try prepare(
                """
                INSERT INTO parsing_state (
                    source_path, file_identity, generation, committed_offset, skipping_oversized_line,
                    observed_size, modification_time_ns, content_fingerprint, has_pending_import,
                    session_id, inherits_history, session_started_at,
                    inherited_history_end_ordinal, history_replay_complete,
                    model, project_path, parent_session_id, project_name,
                    image_attachment_count, updated_at
                ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20)
                ON CONFLICT(source_path) DO UPDATE SET
                    file_identity = excluded.file_identity,
                    generation = excluded.generation,
                    committed_offset = excluded.committed_offset,
                    skipping_oversized_line = excluded.skipping_oversized_line,
                    observed_size = excluded.observed_size,
                    modification_time_ns = excluded.modification_time_ns,
                    content_fingerprint = excluded.content_fingerprint,
                    has_pending_import = excluded.has_pending_import,
                    session_id = excluded.session_id,
                    inherits_history = excluded.inherits_history,
                    session_started_at = excluded.session_started_at,
                    inherited_history_end_ordinal = excluded.inherited_history_end_ordinal,
                    history_replay_complete = excluded.history_replay_complete,
                    model = excluded.model,
                    project_path = excluded.project_path,
                    parent_session_id = excluded.parent_session_id,
                    project_name = excluded.project_name,
                    image_attachment_count = excluded.image_attachment_count,
                    updated_at = excluded.updated_at
                """
            )
            defer { sqlite3_finalize(state) }
            try bind(checkpoint.sourcePath, at: 1, to: state)
            try bind(checkpoint.fileIdentity, at: 2, to: state)
            sqlite3_bind_int64(state, 3, checkpoint.generation)
            sqlite3_bind_int64(state, 4, checkpoint.committedOffset)
            sqlite3_bind_int(state, 5, checkpoint.isSkippingOversizedLine ? 1 : 0)
            sqlite3_bind_int64(state, 6, checkpoint.observedSize)
            sqlite3_bind_int64(state, 7, checkpoint.modificationTimeNanoseconds)
            try bind(checkpoint.contentFingerprint, at: 8, to: state)
            sqlite3_bind_int(state, 9, checkpoint.hasPendingImport ? 1 : 0)
            try bind(checkpoint.sessionID, at: 10, to: state)
            sqlite3_bind_int(state, 11, checkpoint.inheritsHistory ? 1 : 0)
            if let sessionStartedAt = checkpoint.sessionStartedAt {
                sqlite3_bind_double(state, 12, sessionStartedAt.timeIntervalSince1970)
            } else {
                sqlite3_bind_null(state, 12)
            }
            if let inheritedHistoryEndOrdinal = checkpoint.inheritedHistoryEndOrdinal {
                sqlite3_bind_int64(state, 13, inheritedHistoryEndOrdinal)
            } else {
                sqlite3_bind_null(state, 13)
            }
            sqlite3_bind_int(state, 14, checkpoint.historyReplayComplete ? 1 : 0)
            try bind(checkpoint.model, at: 15, to: state)
            try bind(checkpoint.projectPath, at: 16, to: state)
            try bind(checkpoint.parentSessionID, at: 17, to: state)
            try bind(checkpoint.projectName, at: 18, to: state)
            sqlite3_bind_int64(state, 19, checkpoint.imageAttachmentCount)
            sqlite3_bind_double(state, 20, Date().timeIntervalSince1970)
            guard sqlite3_step(state) == SQLITE_DONE else {
                throw SQLiteDatabaseError.step(errorMessage)
            }

            if checkpoint.hasPendingImport == false,
               checkpoint.committedOffset >= checkpoint.observedSize {
                let completedBackfill = try prepare(
                    "DELETE FROM analytics_backfill_sources WHERE source_path = ?1"
                )
                defer { sqlite3_finalize(completedBackfill) }
                try bind(checkpoint.sourcePath, at: 1, to: completedBackfill)
                guard sqlite3_step(completedBackfill) == SQLITE_DONE else {
                    throw SQLiteDatabaseError.step(errorMessage)
                }
            }

            if let sessionID = checkpoint.sessionID {
                let metadata = try prepare(
                    """
                    INSERT INTO session_metadata (
                        session_id, parent_session_id, project_id, project_name,
                        started_at, image_attachment_count, updated_at
                    ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
                    ON CONFLICT(session_id) DO UPDATE SET
                        parent_session_id = excluded.parent_session_id,
                        project_id = excluded.project_id,
                        project_name = excluded.project_name,
                        started_at = COALESCE(session_metadata.started_at, excluded.started_at),
                        image_attachment_count = excluded.image_attachment_count,
                        updated_at = excluded.updated_at
                    """
                )
                defer { sqlite3_finalize(metadata) }
                try bind(sessionID, at: 1, to: metadata)
                try bind(checkpoint.parentSessionID, at: 2, to: metadata)
                try bind(checkpoint.projectPath, at: 3, to: metadata)
                try bind(checkpoint.projectName, at: 4, to: metadata)
                if let sessionStartedAt = checkpoint.sessionStartedAt {
                    sqlite3_bind_double(metadata, 5, sessionStartedAt.timeIntervalSince1970)
                } else {
                    sqlite3_bind_null(metadata, 5)
                }
                sqlite3_bind_int64(metadata, 6, checkpoint.imageAttachmentCount)
                sqlite3_bind_double(metadata, 7, Date().timeIntervalSince1970)
                guard sqlite3_step(metadata) == SQLITE_DONE else {
                    throw SQLiteDatabaseError.step(errorMessage)
                }
            }

            try execute("COMMIT")
        } catch {
            try? execute("ROLLBACK")
            throw error
        }
        try Self.protectDatabaseFiles(at: databaseURL)
        guard let sessionID = checkpoint.sessionID else { return nil }
        return try normalizationStateWithinTransaction(for: sessionID)
    }

    func usageSnapshot(now: Date, calendar: Calendar, weekStart: WeekStart) throws -> UsageSnapshot {
        var reportingCalendar = calendar
        reportingCalendar.firstWeekday = weekStart.rawValue
        let today = reportingCalendar.startOfDay(for: now)
        let week = reportingCalendar.dateInterval(of: .weekOfYear, for: now)?.start ?? today
        let month = reportingCalendar.dateInterval(of: .month, for: now)?.start ?? today

        try execute("BEGIN DEFERRED")
        do {
            let todayUsage = try sum(from: today, through: now)
            let weekUsage = try sum(from: week, through: now)
            let monthUsage = try sum(from: month, through: now)
            let allTimeUsage = try sum(from: nil, through: now)
            let lastUpdated = try maximumEventDate(through: now)
            let quality = try databaseQuality(hasEvents: lastUpdated != nil)
            let snapshot = UsageSnapshot(
                today: todayUsage,
                week: weekUsage,
                month: monthUsage,
                allTime: allTimeUsage,
                quality: quality,
                updatedAt: lastUpdated
            )
            try execute("COMMIT")
            return snapshot
        } catch {
            try? execute("ROLLBACK")
            throw error
        }
    }

    func analyticsSnapshot(
        range: AnalyticsRange,
        through end: Date,
        calendar: Calendar
    ) throws -> AnalyticsSnapshot {
        let interval = range.interval(through: end, calendar: calendar)
        let bucketIntervals = range.bucketIntervals(through: end, calendar: calendar)

        try execute("BEGIN DEFERRED")
        do {
            let models = try modelUsage(from: interval.start, through: end)
            let buckets = try usageBuckets(intervals: bucketIntervals, through: end)
            let projects = try projectUsage(from: interval.start, through: end)
            let sessions = try sessionUsage(from: interval.start, through: end)
            let usage = models.reduce(TokenUsage.zero) { $0.adding($1.usage) }
            let snapshot = AnalyticsSnapshot(
                range: range,
                interval: interval,
                through: end,
                usage: usage,
                quality: try databaseQuality(hasEvents: !usage.isZero),
                buckets: buckets,
                models: models,
                projects: projects,
                sessions: sessions
            )
            try execute("COMMIT")
            return snapshot
        } catch {
            try? execute("ROLLBACK")
            throw error
        }
    }

    private func modelUsage(from start: Date, through end: Date) throws -> [ModelUsageSummary] {
        let statement = try prepare(
            """
            SELECT model,
                   CASE
                       WHEN high_context_pricing IS NULL
                        AND input_tokens <= \(PricingContext.highContextInputThreshold) THEN 0
                       ELSE high_context_pricing
                   END AS effective_high_context_pricing,
                   SUM(input_tokens), SUM(cached_input_tokens),
                   CASE WHEN COUNT(cache_write_input_tokens) = COUNT(*)
                        THEN COALESCE(SUM(cache_write_input_tokens), 0) END,
                   SUM(output_tokens)
            FROM usage_events
            WHERE occurred_at >= ?1 AND occurred_at <= ?2
            GROUP BY model, effective_high_context_pricing
            """
        )
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_double(statement, 1, start.timeIntervalSince1970)
        sqlite3_bind_double(statement, 2, end.timeIntervalSince1970)

        var rows: [String: (modelID: String?, accumulator: ModelUsageAccumulator)] = [:]
        while true {
            switch sqlite3_step(statement) {
            case SQLITE_ROW:
                let modelID = text(statement, column: 0)
                let key = modelID ?? "\0unknown-model"
                var row = rows[key] ?? (modelID, ModelUsageAccumulator())
                row.accumulator.add(
                    aggregateUsage(statement, startingAt: 2),
                    pricingContext: pricingContext(statement, column: 1)
                )
                rows[key] = row
            case SQLITE_DONE:
                return rows.values.map { $0.accumulator.summary(modelID: $0.modelID) }
                    .sorted { $0.usage.totalTokens > $1.usage.totalTokens }
            default:
                throw SQLiteDatabaseError.step(errorMessage)
            }
        }
    }

    private func usageBuckets(intervals: [DateInterval], through end: Date) throws -> [UsageBucket] {
        guard !intervals.isEmpty else { return [] }
        let placeholders = intervals.enumerated().map { index, _ in
            let first = index * 3 + 1
            return "(?\(first), ?\(first + 1), ?\(first + 2))"
        }.joined(separator: ",")
        let endIndex = intervals.count * 3 + 1
        let statement = try prepare(
            """
            WITH buckets(bucket_index, start_at, end_at) AS (VALUES \(placeholders))
            SELECT buckets.bucket_index, usage_events.model,
                   CASE
                       WHEN usage_events.high_context_pricing IS NULL
                        AND usage_events.input_tokens <= \(PricingContext.highContextInputThreshold) THEN 0
                       ELSE usage_events.high_context_pricing
                   END AS effective_high_context_pricing,
                   COALESCE(SUM(usage_events.input_tokens), 0),
                   COALESCE(SUM(usage_events.cached_input_tokens), 0),
                   CASE WHEN COUNT(usage_events.cache_write_input_tokens) = COUNT(usage_events.event_key)
                        THEN COALESCE(SUM(usage_events.cache_write_input_tokens), 0) END,
                   COALESCE(SUM(usage_events.output_tokens), 0)
            FROM buckets
            LEFT JOIN usage_events
              ON usage_events.occurred_at >= buckets.start_at
             AND usage_events.occurred_at < buckets.end_at
             AND usage_events.occurred_at <= ?\(endIndex)
            GROUP BY buckets.bucket_index, usage_events.model, effective_high_context_pricing
            ORDER BY buckets.bucket_index
            """
        )
        defer { sqlite3_finalize(statement) }
        for (index, interval) in intervals.enumerated() {
            let first = Int32(index * 3 + 1)
            sqlite3_bind_int64(statement, first, Int64(index))
            sqlite3_bind_double(statement, first + 1, interval.start.timeIntervalSince1970)
            sqlite3_bind_double(statement, first + 2, interval.end.timeIntervalSince1970)
        }
        sqlite3_bind_double(statement, Int32(endIndex), end.timeIntervalSince1970)

        var grouped = Array(
            repeating: [String: (modelID: String?, accumulator: ModelUsageAccumulator)](),
            count: intervals.count
        )
        while true {
            switch sqlite3_step(statement) {
            case SQLITE_ROW:
                let index = Int(sqlite3_column_int64(statement, 0))
                guard grouped.indices.contains(index) else {
                    throw SQLiteDatabaseError.step("usage bucket index is out of range")
                }
                let usage = aggregateUsage(statement, startingAt: 3)
                if !usage.isZero {
                    let modelID = text(statement, column: 1)
                    let key = modelID ?? "\0unknown-model"
                    var row = grouped[index][key] ?? (modelID, ModelUsageAccumulator())
                    row.accumulator.add(
                        usage,
                        pricingContext: pricingContext(statement, column: 2)
                    )
                    grouped[index][key] = row
                }
            case SQLITE_DONE:
                return intervals.enumerated().map { index, interval in
                    UsageBucket(
                        start: interval.start,
                        end: interval.end,
                        models: grouped[index].values
                            .map { $0.accumulator.summary(modelID: $0.modelID) }
                            .sorted { $0.usage.totalTokens > $1.usage.totalTokens }
                    )
                }
            default:
                throw SQLiteDatabaseError.step(errorMessage)
            }
        }
    }

    private func projectUsage(from start: Date, through end: Date) throws -> [ProjectUsageSummary] {
        let statement = try prepare(
            """
            SELECT usage_events.project_path, MAX(session_metadata.project_name), usage_events.model,
                   CASE
                       WHEN usage_events.high_context_pricing IS NULL
                        AND usage_events.input_tokens <= \(PricingContext.highContextInputThreshold) THEN 0
                       ELSE usage_events.high_context_pricing
                   END AS effective_high_context_pricing,
                   SUM(usage_events.input_tokens), SUM(usage_events.cached_input_tokens),
                   CASE WHEN COUNT(usage_events.cache_write_input_tokens) = COUNT(*)
                        THEN COALESCE(SUM(usage_events.cache_write_input_tokens), 0) END,
                   SUM(usage_events.output_tokens)
            FROM usage_events
            LEFT JOIN session_metadata ON session_metadata.session_id = usage_events.session_id
            WHERE usage_events.occurred_at >= ?1 AND usage_events.occurred_at <= ?2
            GROUP BY usage_events.project_path, usage_events.model, effective_high_context_pricing
            """
        )
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_double(statement, 1, start.timeIntervalSince1970)
        sqlite3_bind_double(statement, 2, end.timeIntervalSince1970)

        struct Accumulator {
            var name: String
            var models: [String: (modelID: String?, accumulator: ModelUsageAccumulator)]
        }
        var projects: [String: Accumulator] = [:]
        while true {
            switch sqlite3_step(statement) {
            case SQLITE_ROW:
                let projectID = text(statement, column: 0) ?? "unknown-project"
                let name = text(statement, column: 1) ?? "Unknown Project"
                let modelID = text(statement, column: 2)
                let modelKey = modelID ?? "\0unknown-model"
                var project = projects[projectID] ?? Accumulator(name: name, models: [:])
                var model = project.models[modelKey] ?? (modelID, ModelUsageAccumulator())
                model.accumulator.add(
                    aggregateUsage(statement, startingAt: 4),
                    pricingContext: pricingContext(statement, column: 3)
                )
                project.models[modelKey] = model
                projects[projectID] = project
            case SQLITE_DONE:
                let counts = try projectSessionCounts(from: start, through: end)
                return projects.map { projectID, accumulator in
                    let models = accumulator.models.values
                        .map { $0.accumulator.summary(modelID: $0.modelID) }
                        .sorted { $0.usage.totalTokens > $1.usage.totalTokens }
                    let usage = models.reduce(TokenUsage.zero) { $0.adding($1.usage) }
                    return ProjectUsageSummary(
                        id: projectID,
                        name: accumulator.name,
                        usage: usage,
                        models: models,
                        sessionCount: counts[projectID] ?? 0
                    )
                }.sorted { $0.usage.totalTokens > $1.usage.totalTokens }
            default:
                throw SQLiteDatabaseError.step(errorMessage)
            }
        }
    }

    /// Opaque associations for display-name enrichment, including a session
    /// that used more than one working directory in the selected range.
    func projectSessionIDs(from start: Date, through end: Date) throws -> [String: Set<String>] {
        let statement = try prepare("""
            SELECT DISTINCT project_path, session_id FROM usage_events
            WHERE occurred_at >= ?1 AND occurred_at <= ?2
            AND project_path IS NOT NULL AND session_id IS NOT NULL
            """)
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_double(statement, 1, start.timeIntervalSince1970)
        sqlite3_bind_double(statement, 2, end.timeIntervalSince1970)
        var result: [String: Set<String>] = [:]
        while true {
            switch sqlite3_step(statement) {
            case SQLITE_ROW:
                if let project = text(statement, column: 0), let session = text(statement, column: 1) {
                    result[project, default: []].insert(session)
                }
            case SQLITE_DONE: return result
            default: throw SQLiteDatabaseError.step(errorMessage)
            }
        }
    }

    private func projectSessionCounts(from start: Date, through end: Date) throws -> [String: Int] {
        let statement = try prepare(
            """
            SELECT project_path, COUNT(DISTINCT session_id)
            FROM usage_events
            WHERE occurred_at >= ?1 AND occurred_at <= ?2
            GROUP BY project_path
            """
        )
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_double(statement, 1, start.timeIntervalSince1970)
        sqlite3_bind_double(statement, 2, end.timeIntervalSince1970)
        var counts: [String: Int] = [:]
        while true {
            switch sqlite3_step(statement) {
            case SQLITE_ROW:
                counts[text(statement, column: 0) ?? "unknown-project"] = Int(sqlite3_column_int64(statement, 1))
            case SQLITE_DONE:
                return counts
            default:
                throw SQLiteDatabaseError.step(errorMessage)
            }
        }
    }

    private func sessionUsage(from start: Date, through end: Date) throws -> [SessionUsageSummary] {
        let statement = try prepare(
            """
            SELECT COALESCE(usage_events.session_id, 'unknown-session'),
                   MAX(usage_events.project_path), MAX(session_metadata.project_name),
                   session_metadata.started_at, MAX(usage_events.occurred_at), usage_events.model,
                   CASE
                       WHEN usage_events.high_context_pricing IS NULL
                        AND usage_events.input_tokens <= \(PricingContext.highContextInputThreshold) THEN 0
                       ELSE usage_events.high_context_pricing
                   END AS effective_high_context_pricing,
                   SUM(usage_events.input_tokens), SUM(usage_events.cached_input_tokens),
                   CASE WHEN COUNT(usage_events.cache_write_input_tokens) = COUNT(*)
                        THEN COALESCE(SUM(usage_events.cache_write_input_tokens), 0) END,
                   SUM(usage_events.output_tokens), session_metadata.parent_session_id,
                   COALESCE(session_metadata.image_attachment_count, 0)
            FROM usage_events
            LEFT JOIN session_metadata ON session_metadata.session_id = usage_events.session_id
            WHERE usage_events.occurred_at >= ?1 AND usage_events.occurred_at <= ?2
            GROUP BY usage_events.session_id, usage_events.model, effective_high_context_pricing
            """
        )
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_double(statement, 1, start.timeIntervalSince1970)
        sqlite3_bind_double(statement, 2, end.timeIntervalSince1970)

        struct Accumulator {
            var projectID: String?
            var projectName: String?
            var startedAt: Date?
            var lastActivityAt: Date
            var models: [String: (modelID: String?, accumulator: ModelUsageAccumulator)]
            var imageAttachmentCount: Int
            var parentSessionID: String?
        }
        var sessions: [String: Accumulator] = [:]
        while true {
            switch sqlite3_step(statement) {
            case SQLITE_ROW:
                guard let sessionID = text(statement, column: 0) else {
                    throw SQLiteDatabaseError.step("session identifier is unavailable")
                }
                let lastActivity = Date(timeIntervalSince1970: sqlite3_column_double(statement, 4))
                let startedAt = sqlite3_column_type(statement, 3) == SQLITE_NULL
                    ? nil
                    : Date(timeIntervalSince1970: sqlite3_column_double(statement, 3))
                var accumulator = sessions[sessionID] ?? Accumulator(
                    projectID: text(statement, column: 1),
                    projectName: text(statement, column: 2),
                    startedAt: startedAt,
                    lastActivityAt: lastActivity,
                    models: [:],
                    imageAttachmentCount: Int(sqlite3_column_int64(statement, 12)),
                    parentSessionID: text(statement, column: 11)
                )
                accumulator.lastActivityAt = max(accumulator.lastActivityAt, lastActivity)
                let modelID = text(statement, column: 5)
                let modelKey = modelID ?? "\0unknown-model"
                var model = accumulator.models[modelKey] ?? (modelID, ModelUsageAccumulator())
                model.accumulator.add(
                    aggregateUsage(statement, startingAt: 7),
                    pricingContext: pricingContext(statement, column: 6)
                )
                accumulator.models[modelKey] = model
                sessions[sessionID] = accumulator
            case SQLITE_DONE:
                var directSubagentCounts: [String: Int] = [:]
                for accumulator in sessions.values {
                    guard let parentID = accumulator.parentSessionID,
                          sessions[parentID] != nil
                    else { continue }
                    directSubagentCounts[parentID, default: 0] += 1
                }
                return sessions.map { sessionID, accumulator in
                    let models = accumulator.models.values
                        .map { $0.accumulator.summary(modelID: $0.modelID) }
                        .sorted { $0.usage.totalTokens > $1.usage.totalTokens }
                    return SessionUsageSummary(
                        id: sessionID,
                        projectID: accumulator.projectID,
                        projectName: accumulator.projectName,
                        startedAt: accumulator.startedAt,
                        lastActivityAt: accumulator.lastActivityAt,
                        usage: models.reduce(TokenUsage.zero) { $0.adding($1.usage) },
                        models: models,
                        directSubagentCount: directSubagentCounts[sessionID] ?? 0,
                        imageAttachmentCount: accumulator.imageAttachmentCount,
                        parentSessionID: accumulator.parentSessionID
                    )
                }.sorted { $0.lastActivityAt > $1.lastActivityAt }
            default:
                throw SQLiteDatabaseError.step(errorMessage)
            }
        }
    }

    private func aggregateUsage(_ statement: OpaquePointer, startingAt column: Int32) -> TokenUsage {
        TokenUsage(
            inputTokens: sqlite3_column_int64(statement, column),
            cachedInputTokens: sqlite3_column_int64(statement, column + 1),
            cacheWriteInputTokens: sqlite3_column_type(statement, column + 2) == SQLITE_NULL
                ? nil
                : sqlite3_column_int64(statement, column + 2),
            outputTokens: sqlite3_column_int64(statement, column + 3)
        )
    }

    private func pricingContext(_ statement: OpaquePointer, column: Int32) -> PricingContext? {
        guard sqlite3_column_type(statement, column) != SQLITE_NULL else { return nil }
        return PricingContext(rawValue: Int(sqlite3_column_int64(statement, column)))
    }

    func dataStatistics() throws -> DataStatistics {
        let statement = try prepare("SELECT MIN(occurred_at), MAX(occurred_at) FROM usage_events")
        defer { sqlite3_finalize(statement) }
        guard sqlite3_step(statement) == SQLITE_ROW else {
            throw SQLiteDatabaseError.step(errorMessage)
        }
        let oldest = sqlite3_column_type(statement, 0) == SQLITE_NULL
            ? nil
            : Date(timeIntervalSince1970: sqlite3_column_double(statement, 0))
        let newest = sqlite3_column_type(statement, 1) == SQLITE_NULL
            ? nil
            : Date(timeIntervalSince1970: sqlite3_column_double(statement, 1))
        let size = [databaseURL.path, databaseURL.path + "-wal", databaseURL.path + "-shm"]
            .compactMap { try? FileManager.default.attributesOfItem(atPath: $0)[.size] as? NSNumber }
            .reduce(Int64(0)) { $0 + $1.int64Value }
        return DataStatistics(databaseBytes: size, oldestRecord: oldest, newestRecord: newest)
    }

    func eventCount() throws -> Int64 {
        let statement = try prepare("SELECT COUNT(*) FROM usage_events")
        defer { sqlite3_finalize(statement) }
        guard sqlite3_step(statement) == SQLITE_ROW else {
            throw SQLiteDatabaseError.step(errorMessage)
        }
        return sqlite3_column_int64(statement, 0)
    }

#if DEBUG
    func prepareVersion14FixtureForTesting() throws {
        try execute("PRAGMA user_version = 14")
    }

    func prepareVersion13FixtureForTesting() throws {
        try removeVersion14SchemaForTesting()
        try execute("PRAGMA user_version = 13")
    }

    func prepareVersion10FixtureForTesting() throws {
        try removeVersion14SchemaForTesting()
        try removeVersion13SchemaForTesting()
        try removeVersion12SchemaForTesting()
        try execute("PRAGMA user_version = 10")
    }

    func prepareVersion9FixtureForTesting() throws {
        try removeVersion14SchemaForTesting()
        try removeVersion13SchemaForTesting()
        try removeVersion12SchemaForTesting()
        try execute("ALTER TABLE parsing_state DROP COLUMN history_replay_complete")
        try execute("ALTER TABLE parsing_state DROP COLUMN inherited_history_end_ordinal")
        try execute("ALTER TABLE parsing_state DROP COLUMN session_started_at")
        try execute("PRAGMA user_version = 9")
    }

    func prepareVersion2FixtureForTesting() throws {
        try removeVersion14SchemaForTesting()
        try removeVersion13SchemaForTesting()
        try removeVersion12SchemaForTesting()
        try execute("ALTER TABLE parsing_state DROP COLUMN history_replay_complete")
        try execute("ALTER TABLE parsing_state DROP COLUMN inherited_history_end_ordinal")
        try execute("ALTER TABLE parsing_state DROP COLUMN session_started_at")
        try execute("ALTER TABLE parsing_state DROP COLUMN has_pending_import")
        try execute("ALTER TABLE parsing_state DROP COLUMN content_fingerprint")
        try execute("ALTER TABLE parsing_state DROP COLUMN modification_time_ns")
        try execute("ALTER TABLE parsing_state DROP COLUMN observed_size")
        try execute("ALTER TABLE parsing_state DROP COLUMN skipping_oversized_line")
        try execute("ALTER TABLE session_counters DROP COLUMN last_observed_at")
        try execute("PRAGMA user_version = 2")
    }

    private func removeVersion12SchemaForTesting() throws {
        try execute("DROP TABLE IF EXISTS session_metadata")
        try execute("ALTER TABLE parsing_state DROP COLUMN image_attachment_count")
        try execute("ALTER TABLE parsing_state DROP COLUMN project_name")
        try execute("ALTER TABLE parsing_state DROP COLUMN parent_session_id")
    }

    private func removeVersion13SchemaForTesting() throws {
        try execute("DROP INDEX IF EXISTS usage_events_analytics_idx")
        try execute("ALTER TABLE usage_events DROP COLUMN cache_write_input_tokens")
        try execute("ALTER TABLE session_counters DROP COLUMN cache_write_input_tokens")
    }

    private func removeVersion14SchemaForTesting() throws {
        try execute("ALTER TABLE usage_events DROP COLUMN high_context_pricing")
    }
#endif

    func importCutoff() throws -> Date? {
        try importCutoffWithinTransaction()
    }

    func importPolicy() throws -> ImportPolicy {
        try execute("BEGIN DEFERRED")
        do {
            let policy = ImportPolicy(
                cutoff: try importCutoffWithinTransaction(),
                dataEpoch: try dataEpochWithinTransaction()
            )
            try execute("COMMIT")
            return policy
        } catch {
            try? execute("ROLLBACK")
            throw error
        }
    }

    private func importCutoffWithinTransaction() throws -> Date? {
        let statement = try prepare("SELECT value FROM app_metadata WHERE key = 'import_cutoff'")
        defer { sqlite3_finalize(statement) }
        switch sqlite3_step(statement) {
        case SQLITE_ROW:
            guard let value = text(statement, column: 0), let seconds = TimeInterval(value) else {
                return nil
            }
            return Date(timeIntervalSince1970: seconds)
        case SQLITE_DONE:
            return nil
        default:
            throw SQLiteDatabaseError.step(errorMessage)
        }
    }

    func dataEpoch() throws -> Int64 {
        try dataEpochWithinTransaction()
    }

    func rebuildStatistics() throws {
        try execute("BEGIN IMMEDIATE")
        do {
            try incrementDataEpoch()
            try execute("DELETE FROM usage_events")
            try execute("DELETE FROM parsing_state")
            try execute("DELETE FROM session_counters")
            try execute("DELETE FROM session_metadata")
            try execute("DELETE FROM analytics_backfill_sources")
            try execute("DELETE FROM app_metadata WHERE key = 'legacy_partial_quality'")
            try execute("COMMIT")
        } catch {
            try? execute("ROLLBACK")
            throw error
        }
        try Self.protectDatabaseFiles(at: databaseURL)
    }

    func clearLocalHistory(at cutoff: Date, preservesMessageExclusions: Bool = false) throws -> ClearHistoryCompactionStatus {
        try execute("BEGIN IMMEDIATE")
        do {
            try incrementDataEpoch()
            if preservesMessageExclusions {
                try prepareMessageExclusions()
                let statement = try prepare("""
                    INSERT OR IGNORE INTO claude_message_exclusions(event_key)
                    SELECT event_key FROM usage_events WHERE occurred_at <= ?1
                    """)
                defer { sqlite3_finalize(statement) }
                sqlite3_bind_double(statement, 1, cutoff.timeIntervalSince1970)
                guard sqlite3_step(statement) == SQLITE_DONE else {
                    throw SQLiteDatabaseError.step(errorMessage)
                }
            }
            try execute("DELETE FROM usage_events")
            try execute("DELETE FROM parsing_state")
            try execute("DELETE FROM session_counters")
            try execute("DELETE FROM session_metadata")
            try execute("DELETE FROM analytics_backfill_sources")
            try execute("DELETE FROM app_metadata WHERE key = 'legacy_partial_quality'")
            do {
                let statement = try prepare(
                    """
                    INSERT INTO app_metadata(key, value) VALUES('import_cutoff', ?1)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value
                    """
                )
                defer { sqlite3_finalize(statement) }
                try bind(String(cutoff.timeIntervalSince1970), at: 1, to: statement)
                guard sqlite3_step(statement) == SQLITE_DONE else {
                    throw SQLiteDatabaseError.step(errorMessage)
                }
            }
            try execute("COMMIT")
        } catch {
            try? execute("ROLLBACK")
            throw error
        }
        var compactionStatus = ClearHistoryCompactionStatus.complete
        do {
            guard let connection else {
                throw SQLiteDatabaseError.statement("database is closed")
            }
            var logFrameCount: Int32 = 0
            var checkpointedFrameCount: Int32 = 0
            let checkpointResult = sqlite3_wal_checkpoint_v2(
                connection.rawValue,
                nil,
                SQLITE_CHECKPOINT_TRUNCATE,
                &logFrameCount,
                &checkpointedFrameCount
            )
            guard checkpointResult == SQLITE_OK else {
                throw SQLiteDatabaseError.step(errorMessage)
            }
            try execute("VACUUM")
            try Self.protectDatabaseFiles(at: databaseURL)
        } catch {
            compactionStatus = .deferred
            try? Self.protectDatabaseFiles(at: databaseURL)
        }
        return compactionStatus
    }

    /// Claude-only additive metadata, created only by the message-ID importer.
    /// Keep hashes (not counts or content) across clear/rebuild so a later block
    /// of an excluded response cannot resurrect that response's old usage.
    private func prepareMessageExclusions() throws {
        try execute("""
            CREATE TABLE IF NOT EXISTS claude_message_exclusions (
                event_key TEXT PRIMARY KEY NOT NULL
            ) WITHOUT ROWID
            """)
    }

    private func excludeMessages(_ eventKeys: Set<String>) throws {
        guard !eventKeys.isEmpty else { return }
        let insert = try prepare("INSERT OR IGNORE INTO claude_message_exclusions(event_key) VALUES (?1)")
        defer { sqlite3_finalize(insert) }
        let delete = try prepare("DELETE FROM usage_events WHERE event_key = ?1")
        defer { sqlite3_finalize(delete) }
        for eventKey in eventKeys {
            for statement in [insert, delete] {
                sqlite3_reset(statement)
                sqlite3_clear_bindings(statement)
                try bind(eventKey, at: 1, to: statement)
                guard sqlite3_step(statement) == SQLITE_DONE else {
                    throw SQLiteDatabaseError.step(errorMessage)
                }
            }
        }
    }

    private func dataEpochWithinTransaction() throws -> Int64 {
        let statement = try prepare("SELECT value FROM app_metadata WHERE key = 'data_epoch'")
        defer { sqlite3_finalize(statement) }
        switch sqlite3_step(statement) {
        case SQLITE_ROW:
            return text(statement, column: 0).flatMap(Int64.init) ?? 0
        case SQLITE_DONE:
            return 0
        default:
            throw SQLiteDatabaseError.step(errorMessage)
        }
    }

    private func allocatedDatabaseBytesWithinTransaction() throws -> Int64 {
        let pageCount = try pragmaInt64("PRAGMA page_count")
        let pageSize = try pragmaInt64("PRAGMA page_size")
        let (bytes, overflow) = pageCount.multipliedReportingOverflow(by: pageSize)
        guard !overflow else {
            throw SQLiteDatabaseError.resourceLimit("The local database size exceeds the supported range")
        }
        return bytes
    }

    private func pragmaInt64(_ sql: String) throws -> Int64 {
        let statement = try prepare(sql)
        defer { sqlite3_finalize(statement) }
        guard sqlite3_step(statement) == SQLITE_ROW else {
            throw SQLiteDatabaseError.step(errorMessage)
        }
        return sqlite3_column_int64(statement, 0)
    }

    private func incrementDataEpoch() throws {
        try execute(
            """
            INSERT INTO app_metadata(key, value) VALUES('data_epoch', '1')
            ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
            """
        )
    }

    private func sum(from start: Date?, through end: Date) throws -> TokenUsage {
        let statement: OpaquePointer
        if start != nil {
            statement = try prepare(
                """
                SELECT input_tokens, cached_input_tokens, cache_write_input_tokens, output_tokens
                FROM usage_events
                WHERE occurred_at >= ?1 AND occurred_at <= ?2
                """
            )
        } else {
            statement = try prepare(
                """
                SELECT input_tokens, cached_input_tokens, cache_write_input_tokens, output_tokens
                FROM usage_events
                WHERE occurred_at <= ?1
                """
            )
        }
        defer { sqlite3_finalize(statement) }

        if let start {
            sqlite3_bind_double(statement, 1, start.timeIntervalSince1970)
            sqlite3_bind_double(statement, 2, end.timeIntervalSince1970)
        } else {
            sqlite3_bind_double(statement, 1, end.timeIntervalSince1970)
        }
        var total = TokenUsage.zero
        while true {
            switch sqlite3_step(statement) {
            case SQLITE_ROW:
                let input = sqlite3_column_int64(statement, 0)
                let cached = sqlite3_column_int64(statement, 1)
                let cacheWrite = sqlite3_column_type(statement, 2) == SQLITE_NULL
                    ? nil
                    : sqlite3_column_int64(statement, 2)
                let output = sqlite3_column_int64(statement, 3)
                let (newInput, inputOverflow) = total.inputTokens.addingReportingOverflow(input)
                let (newCached, cachedOverflow) = total.cachedInputTokens.addingReportingOverflow(cached)
                let (newOutput, outputOverflow) = total.outputTokens.addingReportingOverflow(output)
                let newCacheWrite: Int64?
                let cacheWriteOverflow: Bool
                if let current = total.cacheWriteInputTokens, let cacheWrite {
                    let result = current.addingReportingOverflow(cacheWrite)
                    newCacheWrite = result.partialValue
                    cacheWriteOverflow = result.overflow
                } else {
                    newCacheWrite = nil
                    cacheWriteOverflow = false
                }
                guard !inputOverflow, !cachedOverflow, !cacheWriteOverflow, !outputOverflow else {
                    throw SQLiteDatabaseError.step("token aggregate exceeds the supported range")
                }
                total = TokenUsage(
                    inputTokens: newInput,
                    cachedInputTokens: newCached,
                    cacheWriteInputTokens: newCacheWrite,
                    outputTokens: newOutput
                )
            case SQLITE_DONE:
                return total
            default:
                throw SQLiteDatabaseError.step(errorMessage)
            }
        }
    }

    private func maximumEventDate(through end: Date) throws -> Date? {
        let statement = try prepare("SELECT MAX(occurred_at) FROM usage_events WHERE occurred_at <= ?1")
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_double(statement, 1, end.timeIntervalSince1970)
        guard sqlite3_step(statement) == SQLITE_ROW else {
            throw SQLiteDatabaseError.step(errorMessage)
        }
        guard sqlite3_column_type(statement, 0) != SQLITE_NULL else { return nil }
        return Date(timeIntervalSince1970: sqlite3_column_double(statement, 0))
    }

    private func databaseQuality(hasEvents: Bool) throws -> DataQuality {
        guard hasEvents else { return .unavailable }
        let statement = try prepare(
            """
            SELECT 1
            WHERE EXISTS (SELECT 1 FROM analytics_backfill_sources)
               OR EXISTS (
                    SELECT 1 FROM app_metadata
                    WHERE key = 'legacy_partial_quality' AND value = '1'
               )
               OR EXISTS (SELECT 1 FROM session_counters WHERE quality != 'exact')
            LIMIT 1
            """
        )
        defer { sqlite3_finalize(statement) }
        switch sqlite3_step(statement) {
        case SQLITE_ROW: return .partial
        case SQLITE_DONE: return .exact
        default: throw SQLiteDatabaseError.step(errorMessage)
        }
    }

    private static func configure(_ database: OpaquePointer) throws {
        guard sqlite3_busy_timeout(database, 2_000) == SQLITE_OK else {
            throw SQLiteDatabaseError.statement(String(cString: sqlite3_errmsg(database)))
        }
        try execute("PRAGMA foreign_keys = ON", on: database)
        try execute("PRAGMA journal_mode = WAL", on: database)
        try execute("PRAGMA synchronous = NORMAL", on: database)
        try execute("PRAGMA secure_delete = ON", on: database)
    }

    private static func migrate(_ database: OpaquePointer) throws {
        var version = try userVersion(database)
        guard version <= 15 else {
            throw SQLiteDatabaseError.migration("database schema is newer than this app supports")
        }

        if version == 0 {
            try migrateToVersion1(database)
            version = 1
        }
        if version == 1 {
            try migrateToVersion2(database)
            version = 2
        }
        if version == 2 {
            try migrateToVersion3(database)
            version = 3
        }
        if version == 3 {
            try migrateToVersion4(database)
            version = 4
        }
        if version == 4 {
            try migrateToVersion5(database)
            version = 5
        }
        if version == 5 {
            try migrateToVersion6(database)
            version = 6
        }
        if version == 6 {
            try migrateToVersion7(database)
            version = 7
        }
        if version == 7 {
            try migrateToVersion8(database)
            version = 8
        }
        if version == 8 {
            try migrateToVersion9(database)
            version = 9
        }
        if version == 9 {
            try migrateToVersion10(database)
            version = 10
        }
        if version == 10 {
            try migrateToVersion11(database)
            version = 11
        }
        if version == 11 {
            try migrateToVersion12(database)
            version = 12
        }
        if version == 12 {
            try migrateToVersion13(database)
            version = 13
        }
        if version == 13 {
            try migrateToVersion14(database)
            version = 14
        }
        if version == 14 {
            try migrateToVersion15(database)
        }
    }

    private static func userVersion(_ database: OpaquePointer) throws -> Int32 {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, "PRAGMA user_version", -1, &statement, nil) == SQLITE_OK,
              let statement else {
            throw SQLiteDatabaseError.migration(String(cString: sqlite3_errmsg(database)))
        }
        defer { sqlite3_finalize(statement) }
        guard sqlite3_step(statement) == SQLITE_ROW else {
            throw SQLiteDatabaseError.migration(String(cString: sqlite3_errmsg(database)))
        }
        return sqlite3_column_int(statement, 0)
    }

    private static func migrateToVersion1(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 1 {
                try execute("COMMIT", on: database)
                return
            }
            try execute(
                """
                CREATE TABLE usage_events (
                    event_key TEXT PRIMARY KEY NOT NULL,
                    occurred_at REAL NOT NULL,
                    session_id TEXT,
                    model TEXT,
                    project_path TEXT,
                    input_tokens INTEGER NOT NULL CHECK(input_tokens >= 0),
                    cached_input_tokens INTEGER NOT NULL CHECK(cached_input_tokens >= 0 AND cached_input_tokens <= input_tokens),
                    output_tokens INTEGER NOT NULL CHECK(output_tokens >= 0),
                    source_path TEXT NOT NULL,
                    source_generation INTEGER NOT NULL,
                    source_position INTEGER NOT NULL,
                    UNIQUE(source_path, source_generation, source_position)
                )
                """,
                on: database
            )
            try execute(
                "CREATE INDEX usage_events_occurred_at_idx ON usage_events(occurred_at)",
                on: database
            )
            try execute(
                """
                CREATE TABLE parsing_state (
                    source_path TEXT PRIMARY KEY NOT NULL,
                    file_identity TEXT NOT NULL,
                    generation INTEGER NOT NULL,
                    committed_offset INTEGER NOT NULL CHECK(committed_offset >= 0),
                    session_id TEXT,
                    inherits_history INTEGER NOT NULL DEFAULT 0,
                    model TEXT,
                    project_path TEXT,
                    updated_at REAL NOT NULL
                )
                """,
                on: database
            )
            try execute(
                """
                CREATE TABLE session_counters (
                    session_id TEXT PRIMARY KEY NOT NULL,
                    input_tokens INTEGER NOT NULL,
                    cached_input_tokens INTEGER NOT NULL,
                    output_tokens INTEGER NOT NULL,
                    quality TEXT NOT NULL
                )
                """,
                on: database
            )
            try execute("PRAGMA user_version = 1", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion2(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 2 {
                try execute("COMMIT", on: database)
                return
            }
            try execute(
                """
                CREATE TABLE app_metadata (
                    key TEXT PRIMARY KEY NOT NULL,
                    value TEXT NOT NULL
                )
                """,
                on: database
            )
            try execute("PRAGMA user_version = 2", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion3(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 3 {
                try execute("COMMIT", on: database)
                return
            }
            let sessionIDs = try distinctTextValues(
                """
                SELECT session_id FROM usage_events WHERE session_id IS NOT NULL
                UNION SELECT session_id FROM parsing_state WHERE session_id IS NOT NULL
                UNION SELECT session_id FROM session_counters
                """,
                on: database
            )
            for sessionID in sessionIDs {
                let digest = stableDigest(sessionID)
                try updateText(
                    "UPDATE usage_events SET session_id = ?1 WHERE session_id = ?2",
                    newValue: digest,
                    oldValue: sessionID,
                    on: database
                )
                try updateText(
                    "UPDATE parsing_state SET session_id = ?1 WHERE session_id = ?2",
                    newValue: digest,
                    oldValue: sessionID,
                    on: database
                )
                try updateText(
                    "UPDATE session_counters SET session_id = ?1 WHERE session_id = ?2",
                    newValue: digest,
                    oldValue: sessionID,
                    on: database
                )
            }

            let eventSourcePaths = try distinctTextValues(
                "SELECT DISTINCT source_path FROM usage_events",
                on: database
            )
            for sourcePath in eventSourcePaths {
                try updateText(
                    "UPDATE usage_events SET source_path = ?1 WHERE source_path = ?2",
                    newValue: stableDigest(sourcePath),
                    oldValue: sourcePath,
                    on: database
                )
            }

            try execute("UPDATE usage_events SET model = NULL, project_path = NULL", on: database)
            try execute("UPDATE parsing_state SET model = NULL, project_path = NULL", on: database)
            try execute("PRAGMA user_version = 3", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion4(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 4 {
                try execute("COMMIT", on: database)
                return
            }
            try execute("ALTER TABLE session_counters ADD COLUMN last_observed_at REAL", on: database)
            try execute(
                """
                UPDATE session_counters
                SET last_observed_at = (
                    SELECT MAX(usage_events.occurred_at)
                    FROM usage_events
                    WHERE usage_events.session_id = session_counters.session_id
                )
                """,
                on: database
            )
            try execute("PRAGMA user_version = 4", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion5(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 5 {
                try execute("COMMIT", on: database)
                return
            }
            try execute(
                "ALTER TABLE parsing_state ADD COLUMN skipping_oversized_line INTEGER NOT NULL DEFAULT 0",
                on: database
            )
            try execute(
                """
                UPDATE session_counters
                SET last_observed_at = (
                    SELECT MAX(usage_events.occurred_at)
                    FROM usage_events
                    WHERE usage_events.session_id = session_counters.session_id
                )
                WHERE last_observed_at IS NULL
                """,
                on: database
            )
            try execute("PRAGMA user_version = 5", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion6(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 6 {
                try execute("COMMIT", on: database)
                return
            }
            try execute(
                "ALTER TABLE parsing_state ADD COLUMN observed_size INTEGER NOT NULL DEFAULT 0",
                on: database
            )
            try execute(
                "ALTER TABLE parsing_state ADD COLUMN modification_time_ns INTEGER NOT NULL DEFAULT 0",
                on: database
            )
            try execute("PRAGMA user_version = 6", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion7(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 7 {
                try execute("COMMIT", on: database)
                return
            }
            try execute(
                "ALTER TABLE parsing_state ADD COLUMN content_fingerprint TEXT NOT NULL DEFAULT ''",
                on: database
            )
            try execute("PRAGMA user_version = 7", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion8(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 8 {
                try execute("COMMIT", on: database)
                return
            }
            let checkpointPaths = try distinctTextValues(
                "SELECT source_path FROM parsing_state",
                on: database
            )
            for sourcePath in checkpointPaths {
                try updateText(
                    "UPDATE parsing_state SET source_path = ?1 WHERE source_path = ?2",
                    newValue: stableDigest(sourcePath),
                    oldValue: sourcePath,
                    on: database
                )
            }
            try execute("PRAGMA user_version = 8", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion9(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 9 {
                try execute("COMMIT", on: database)
                return
            }
            try execute(
                "ALTER TABLE parsing_state ADD COLUMN has_pending_import INTEGER NOT NULL DEFAULT 0",
                on: database
            )
            try execute("PRAGMA user_version = 9", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion10(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 10 {
                try execute("COMMIT", on: database)
                return
            }
            try execute("ALTER TABLE parsing_state ADD COLUMN session_started_at REAL", on: database)
            try execute(
                "ALTER TABLE parsing_state ADD COLUMN inherited_history_end_ordinal INTEGER",
                on: database
            )
            try execute(
                "ALTER TABLE parsing_state ADD COLUMN history_replay_complete INTEGER NOT NULL DEFAULT 1",
                on: database
            )

            // Older builds counted the replayed parent prefix in inherited sessions.
            // Invalidate only those derived rows so source JSONL files can be imported correctly.
            try execute(
                "DELETE FROM usage_events WHERE source_path IN (SELECT source_path FROM parsing_state WHERE inherits_history = 1)",
                on: database
            )
            try execute(
                "DELETE FROM session_counters WHERE session_id IN (SELECT session_id FROM parsing_state WHERE inherits_history = 1)",
                on: database
            )
            try execute("DELETE FROM parsing_state WHERE inherits_history = 1", on: database)
            try execute(
                """
                INSERT INTO app_metadata(key, value) VALUES('data_epoch', '1')
                ON CONFLICT(key) DO UPDATE SET value = CAST(value AS INTEGER) + 1
                """,
                on: database
            )
            try execute("PRAGMA user_version = 10", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion11(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 11 {
                try execute("COMMIT", on: database)
                return
            }

            // Version 10 could retain replayed parent history when an inherited
            // checkpoint had no persisted replay boundary. Rebuild every derived
            // row once so all existing installations use the corrected classifier.
            // Source JSONL files and user preferences in app_metadata are preserved.
            try execute("DELETE FROM usage_events", on: database)
            try execute("DELETE FROM session_counters", on: database)
            try execute("DELETE FROM parsing_state", on: database)
            try execute(
                """
                INSERT INTO app_metadata(key, value) VALUES('data_epoch', '1')
                ON CONFLICT(key) DO UPDATE SET value = CAST(value AS INTEGER) + 1
                """,
                on: database
            )
            try execute("PRAGMA user_version = 11", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion12(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 12 {
                try execute("COMMIT", on: database)
                return
            }
            try execute("ALTER TABLE parsing_state ADD COLUMN parent_session_id TEXT", on: database)
            try execute("ALTER TABLE parsing_state ADD COLUMN project_name TEXT", on: database)
            try execute(
                "ALTER TABLE parsing_state ADD COLUMN image_attachment_count INTEGER NOT NULL DEFAULT 0",
                on: database
            )
            try execute(
                """
                CREATE TABLE session_metadata (
                    session_id TEXT PRIMARY KEY NOT NULL,
                    parent_session_id TEXT,
                    project_id TEXT,
                    project_name TEXT,
                    started_at REAL,
                    image_attachment_count INTEGER NOT NULL DEFAULT 0 CHECK(image_attachment_count >= 0),
                    updated_at REAL NOT NULL
                )
                """,
                on: database
            )
            try execute(
                "CREATE INDEX session_metadata_parent_idx ON session_metadata(parent_session_id)",
                on: database
            )
            try execute("PRAGMA user_version = 12", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion13(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 13 {
                try execute("COMMIT", on: database)
                return
            }
            try execute("ALTER TABLE usage_events ADD COLUMN cache_write_input_tokens INTEGER", on: database)
            try execute(
                "ALTER TABLE session_counters ADD COLUMN cache_write_input_tokens INTEGER",
                on: database
            )
            try execute(
                """
                CREATE INDEX usage_events_analytics_idx
                ON usage_events(occurred_at, model, project_path, session_id)
                """,
                on: database
            )
            try execute("PRAGMA user_version = 13", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion14(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 14 {
                try execute("COMMIT", on: database)
                return
            }

            try execute(
                "ALTER TABLE usage_events ADD COLUMN high_context_pricing INTEGER CHECK(high_context_pricing IN (0, 1))",
                on: database
            )
            try execute("PRAGMA user_version = 14", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func migrateToVersion15(_ database: OpaquePointer) throws {
        try execute("BEGIN IMMEDIATE", on: database)
        do {
            if try userVersion(database) >= 15 {
                try execute("COMMIT", on: database)
                return
            }

            try execute(
                """
                CREATE TABLE IF NOT EXISTS analytics_backfill_sources (
                    source_path TEXT PRIMARY KEY NOT NULL
                )
                """,
                on: database
            )
            try execute(
                """
                INSERT OR IGNORE INTO analytics_backfill_sources(source_path)
                SELECT DISTINCT source_path FROM usage_events
                """,
                on: database
            )

            // Phase 1 checkpoints skipped source bytes that now carry model,
            // project, cache-write, pricing-context, and session metadata. Keep
            // every accounting event so totals remain available even when an old
            // source file has disappeared. Seed each replay above every historical
            // generation so a rewritten source cannot collide with an older row.
            // The conflict update in commit() enriches semantic duplicate events
            // without adding their token deltas a second time.
            try execute(
                """
                INSERT OR REPLACE INTO app_metadata(key, value)
                SELECT 'legacy_partial_quality', '1'
                WHERE EXISTS (SELECT 1 FROM session_counters WHERE quality != 'exact')
                """,
                on: database
            )
            try execute("DELETE FROM session_counters", on: database)
            try execute("DELETE FROM session_metadata", on: database)
            try execute(
                """
                UPDATE parsing_state
                SET generation = MAX(
                        generation + 1,
                        COALESCE(
                            (
                                SELECT MAX(usage_events.source_generation) + 1
                                FROM usage_events
                                WHERE usage_events.source_path = parsing_state.source_path
                            ),
                            generation + 1
                        )
                    ),
                    committed_offset = 0,
                    skipping_oversized_line = 0,
                    observed_size = 0,
                    modification_time_ns = 0,
                    content_fingerprint = '',
                    has_pending_import = 0,
                    session_id = NULL,
                    inherits_history = 0,
                    session_started_at = NULL,
                    inherited_history_end_ordinal = NULL,
                    history_replay_complete = 1,
                    model = NULL,
                    project_path = NULL,
                    parent_session_id = NULL,
                    project_name = NULL,
                    image_attachment_count = 0
                """,
                on: database
            )
            try execute(
                """
                INSERT INTO app_metadata(key, value) VALUES('data_epoch', '1')
                ON CONFLICT(key) DO UPDATE SET value = CAST(value AS INTEGER) + 1
                """,
                on: database
            )
            try execute("PRAGMA user_version = 15", on: database)
            try execute("COMMIT", on: database)
        } catch {
            try? execute("ROLLBACK", on: database)
            throw error
        }
    }

    private static func distinctTextValues(_ sql: String, on database: OpaquePointer) throws -> [String] {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else {
            throw SQLiteDatabaseError.migration(String(cString: sqlite3_errmsg(database)))
        }
        defer { sqlite3_finalize(statement) }

        var values: [String] = []
        while true {
            switch sqlite3_step(statement) {
            case SQLITE_ROW:
                guard let pointer = sqlite3_column_text(statement, 0) else { continue }
                let count = Int(sqlite3_column_bytes(statement, 0))
                values.append(String(decoding: UnsafeBufferPointer(start: pointer, count: count), as: UTF8.self))
            case SQLITE_DONE:
                return values
            default:
                throw SQLiteDatabaseError.migration(String(cString: sqlite3_errmsg(database)))
            }
        }
    }

    private static func updateText(
        _ sql: String,
        newValue: String,
        oldValue: String,
        on database: OpaquePointer
    ) throws {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else {
            throw SQLiteDatabaseError.migration(String(cString: sqlite3_errmsg(database)))
        }
        defer { sqlite3_finalize(statement) }

        let newResult = newValue.withCString {
            sqlite3_bind_text(statement, 1, $0, Int32(newValue.utf8.count), sqliteTransient)
        }
        let oldResult = oldValue.withCString {
            sqlite3_bind_text(statement, 2, $0, Int32(oldValue.utf8.count), sqliteTransient)
        }
        guard newResult == SQLITE_OK, oldResult == SQLITE_OK, sqlite3_step(statement) == SQLITE_DONE else {
            throw SQLiteDatabaseError.migration(String(cString: sqlite3_errmsg(database)))
        }
    }

    private static func stableDigest(_ value: String) -> String {
        SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    private func prepare(_ sql: String) throws -> OpaquePointer {
        guard let connection else { throw SQLiteDatabaseError.statement("database is closed") }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(connection.rawValue, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else {
            throw SQLiteDatabaseError.statement(errorMessage)
        }
        return statement
    }

    private func execute(_ sql: String) throws {
        guard let connection else { throw SQLiteDatabaseError.statement("database is closed") }
        try Self.execute(sql, on: connection.rawValue)
    }

    private static func execute(_ sql: String, on database: OpaquePointer) throws {
        var error: UnsafeMutablePointer<CChar>?
        guard sqlite3_exec(database, sql, nil, nil, &error) == SQLITE_OK else {
            let message = error.map { String(cString: $0) } ?? String(cString: sqlite3_errmsg(database))
            sqlite3_free(error)
            throw SQLiteDatabaseError.statement(message)
        }
    }

    private func bind(_ value: String?, at index: Int32, to statement: OpaquePointer) throws {
        let result: Int32
        if let value {
            guard !value.contains("\0"), value.utf8.count <= Int(Int32.max) else {
                throw SQLiteDatabaseError.statement("text value is not safe to store")
            }
            result = value.withCString {
                sqlite3_bind_text(statement, index, $0, Int32(value.utf8.count), sqliteTransient)
            }
        } else {
            result = sqlite3_bind_null(statement, index)
        }
        guard result == SQLITE_OK else { throw SQLiteDatabaseError.statement(errorMessage) }
    }

    private func text(_ statement: OpaquePointer, column: Int32) -> String? {
        guard let pointer = sqlite3_column_text(statement, column) else { return nil }
        let count = Int(sqlite3_column_bytes(statement, column))
        return String(decoding: UnsafeBufferPointer(start: pointer, count: count), as: UTF8.self)
    }

    private static func protectDatabaseFiles(at url: URL) throws {
        let paths = [
            url.path,
            url.path + "-wal",
            url.path + "-shm",
            url.path + ".startup.lock",
            url.path + ".fingerprint-key"
        ]
        for path in paths where FileManager.default.fileExists(atPath: path) {
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
        }
    }

    private static func loadOrCreateSourceFingerprintKey(at databaseURL: URL) throws -> Data {
        let path = databaseURL.path + ".fingerprint-key"
        let existingDescriptor = Darwin.open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW)
        if existingDescriptor >= 0 {
            defer { Darwin.close(existingDescriptor) }
            var fileStat = stat()
            guard fstat(existingDescriptor, &fileStat) == 0,
                  (fileStat.st_mode & S_IFMT) == S_IFREG,
                  fileStat.st_size == 32
            else {
                throw SQLiteDatabaseError.open("the source fingerprint key is invalid")
            }
            var key = Data(count: 32)
            let readSucceeded = key.withUnsafeMutableBytes { buffer -> Bool in
                guard let baseAddress = buffer.baseAddress else { return false }
                var total = 0
                while total < buffer.count {
                    let count = Darwin.read(
                        existingDescriptor,
                        baseAddress.advanced(by: total),
                        buffer.count - total
                    )
                    if count < 0, errno == EINTR { continue }
                    guard count > 0 else { return false }
                    total += count
                }
                return true
            }
            guard readSucceeded else {
                throw SQLiteDatabaseError.open("unable to read the source fingerprint key")
            }
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
            return key
        }
        guard errno == ENOENT else {
            throw SQLiteDatabaseError.open("unable to open the source fingerprint key")
        }

        let key = SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) }
        let createdDescriptor = Darwin.open(
            path,
            O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW,
            S_IRUSR | S_IWUSR
        )
        guard createdDescriptor >= 0 else {
            throw SQLiteDatabaseError.open("unable to create the source fingerprint key")
        }
        defer { Darwin.close(createdDescriptor) }
        let writeSucceeded = key.withUnsafeBytes { buffer -> Bool in
            guard let baseAddress = buffer.baseAddress else { return false }
            var total = 0
            while total < buffer.count {
                let count = Darwin.write(
                    createdDescriptor,
                    baseAddress.advanced(by: total),
                    buffer.count - total
                )
                if count < 0, errno == EINTR { continue }
                guard count > 0 else { return false }
                total += count
            }
            return true
        }
        guard writeSucceeded, fsync(createdDescriptor) == 0 else {
            throw SQLiteDatabaseError.open("unable to persist the source fingerprint key")
        }
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
        return key
    }

    private var errorMessage: String {
        connection.map { String(cString: sqlite3_errmsg($0.rawValue)) } ?? "database is closed"
    }
}

private struct SQLiteConnection: @unchecked Sendable {
    let rawValue: OpaquePointer
}

private var sqliteTransient: sqlite3_destructor_type {
    unsafeBitCast(-1, to: sqlite3_destructor_type.self)
}
