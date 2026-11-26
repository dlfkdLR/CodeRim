import CSQLite
import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

/// Schema upgrades are independent of query caches and live source collection.
enum SQLiteSchema {
    static let version = 18

    static func configure(_ database: OpaquePointer) throws {
        guard sqlite3_busy_timeout(database, 2_000) == SQLITE_OK else {
            throw SQLiteDatabaseError.statement(String(cString: sqlite3_errmsg(database)))
        }
        try execute("PRAGMA foreign_keys = ON", on: database)
        try execute("PRAGMA journal_mode = WAL", on: database)
        try execute("PRAGMA synchronous = NORMAL", on: database)
        try execute("PRAGMA secure_delete = ON", on: database)
    }

    static func migrate(_ database: OpaquePointer) throws {
        try migrate(database, through: version)
    }

    private static func migrate(_ database: OpaquePointer, through limit: Int) throws {
        #if CODERIM_RUST_ACCOUNTING
        var message = "Unable to migrate usage database"
        var found: Int32 = 0
        let status = withUnsafeMutablePointer(to: &message) { context in
            cr_migrate_schema(UnsafeMutableRawPointer(database), Int32(limit), { target, index, sql in
                let stage = SQLiteSchema.stages[Int(target) - 1]
                guard index < stage.count else { return CR_SCHEMA_END }
                switch stage[index] {
                case let .sql(statement):
                    sql!.pointee = UnsafeRawPointer(statement.utf8Start).assumingMemoryBound(to: CChar.self)
                    return CR_SCHEMA_SQL
                case .sessionIDs: return CR_SCHEMA_SESSION_IDS
                case .eventSources: return CR_SCHEMA_EVENT_SOURCES
                case .checkpointSources: return CR_SCHEMA_CHECKPOINT_SOURCES
                }
            }, { context, value in
                context!.assumingMemoryBound(to: String.self).pointee = String(cString: value!)
            }, context, &found)
        }
        switch status {
        case CR_SCHEMA_OK: return
        case CR_SCHEMA_STATEMENT_ERROR: throw SQLiteDatabaseError.statement(message)
        case CR_SCHEMA_UNSUPPORTED: throw SQLiteDatabaseError.unsupportedSchema(found: Int(found), supported: limit)
        default: throw SQLiteDatabaseError.migration(message)
        }
        #else
        let current = Int(try userVersion(database))
        guard current <= limit else {
            throw SQLiteDatabaseError.unsupportedSchema(found: current, supported: limit)
        }
        guard current >= 0, current < limit else { return }
        for target in (current + 1)...limit {
            try execute("BEGIN IMMEDIATE", on: database)
            do {
                if try userVersion(database) < target {
                    for operation in stages[target - 1] {
                        try apply(operation, on: database)
                    }
                    try execute("PRAGMA user_version = \(target)", on: database)
                }
                try execute("COMMIT", on: database)
            } catch {
                try? execute("ROLLBACK", on: database)
                throw error
            }
        }
        #endif
    }

    // Each string is one sqlite3_exec call. Stage order is also used by the Swift-only build.
    private enum Operation: Sendable {
        case sql(StaticString)
        case sessionIDs, eventSources, checkpointSources
    }

    private static let stages: [[Operation]] = [
        [ // Version 1
            .sql("""
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
                """),
            .sql("CREATE INDEX usage_events_occurred_at_idx ON usage_events(occurred_at)"),
            .sql("""
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
                """),
            .sql("""
                CREATE TABLE session_counters (
                    session_id TEXT PRIMARY KEY NOT NULL,
                    input_tokens INTEGER NOT NULL,
                    cached_input_tokens INTEGER NOT NULL,
                    output_tokens INTEGER NOT NULL,
                    quality TEXT NOT NULL
                )
                """),
        ],
        [ // Version 2
            .sql("""
                CREATE TABLE app_metadata (
                    key TEXT PRIMARY KEY NOT NULL,
                    value TEXT NOT NULL
                )
                """),
        ],
        [ // Version 3
            .sessionIDs,
            .eventSources,
            .sql("UPDATE usage_events SET model = NULL, project_path = NULL"),
            .sql("UPDATE parsing_state SET model = NULL, project_path = NULL"),
        ],
        [ // Version 4
            .sql("ALTER TABLE session_counters ADD COLUMN last_observed_at REAL"),
            .sql("""
                UPDATE session_counters
                SET last_observed_at = (
                    SELECT MAX(usage_events.occurred_at)
                    FROM usage_events
                    WHERE usage_events.session_id = session_counters.session_id
                )
                """),
        ],
        [ // Version 5
            .sql("ALTER TABLE parsing_state ADD COLUMN skipping_oversized_line INTEGER NOT NULL DEFAULT 0"),
            .sql("""
                UPDATE session_counters
                SET last_observed_at = (
                    SELECT MAX(usage_events.occurred_at)
                    FROM usage_events
                    WHERE usage_events.session_id = session_counters.session_id
                )
                WHERE last_observed_at IS NULL
                """),
        ],
        [ // Version 6
            .sql("ALTER TABLE parsing_state ADD COLUMN observed_size INTEGER NOT NULL DEFAULT 0"),
            .sql("ALTER TABLE parsing_state ADD COLUMN modification_time_ns INTEGER NOT NULL DEFAULT 0"),
        ],
        [ // Version 7
            .sql("ALTER TABLE parsing_state ADD COLUMN content_fingerprint TEXT NOT NULL DEFAULT ''"),
        ],
        [ // Version 8
            .checkpointSources,
        ],
        [ // Version 9
            .sql("ALTER TABLE parsing_state ADD COLUMN has_pending_import INTEGER NOT NULL DEFAULT 0"),
        ],
        [ // Version 10
            .sql("ALTER TABLE parsing_state ADD COLUMN session_started_at REAL"),
            .sql("ALTER TABLE parsing_state ADD COLUMN inherited_history_end_ordinal INTEGER"),
            .sql("ALTER TABLE parsing_state ADD COLUMN history_replay_complete INTEGER NOT NULL DEFAULT 1"),
            // Older builds counted the replayed parent prefix in inherited sessions.
            // Invalidate only those derived rows so source JSONL files can be imported correctly.
            .sql("DELETE FROM usage_events WHERE source_path IN (SELECT source_path FROM parsing_state WHERE inherits_history = 1)"),
            .sql("DELETE FROM session_counters WHERE session_id IN (SELECT session_id FROM parsing_state WHERE inherits_history = 1)"),
            .sql("DELETE FROM parsing_state WHERE inherits_history = 1"),
            .sql("""
                INSERT INTO app_metadata(key, value) VALUES('data_epoch', '1')
                ON CONFLICT(key) DO UPDATE SET value = CAST(value AS INTEGER) + 1
                """),
        ],
        [ // Version 11
            // Version 10 could retain replayed parent history when an inherited
            // checkpoint had no persisted replay boundary. Rebuild every derived
            // row once so all existing installations use the corrected classifier.
            // Source JSONL files and user preferences in app_metadata are preserved.
            .sql("DELETE FROM usage_events"),
            .sql("DELETE FROM session_counters"),
            .sql("DELETE FROM parsing_state"),
            .sql("""
                INSERT INTO app_metadata(key, value) VALUES('data_epoch', '1')
                ON CONFLICT(key) DO UPDATE SET value = CAST(value AS INTEGER) + 1
                """),
        ],
        [ // Version 12
            .sql("ALTER TABLE parsing_state ADD COLUMN parent_session_id TEXT"),
            .sql("ALTER TABLE parsing_state ADD COLUMN project_name TEXT"),
            .sql("ALTER TABLE parsing_state ADD COLUMN image_attachment_count INTEGER NOT NULL DEFAULT 0"),
            .sql("""
                CREATE TABLE session_metadata (
                    session_id TEXT PRIMARY KEY NOT NULL,
                    parent_session_id TEXT,
                    project_id TEXT,
                    project_name TEXT,
                    started_at REAL,
                    image_attachment_count INTEGER NOT NULL DEFAULT 0 CHECK(image_attachment_count >= 0),
                    updated_at REAL NOT NULL
                )
                """),
            .sql("CREATE INDEX session_metadata_parent_idx ON session_metadata(parent_session_id)"),
        ],
        [ // Version 13
            .sql("ALTER TABLE usage_events ADD COLUMN cache_write_input_tokens INTEGER"),
            .sql("ALTER TABLE session_counters ADD COLUMN cache_write_input_tokens INTEGER"),
            .sql("""
                CREATE INDEX usage_events_analytics_idx
                ON usage_events(occurred_at, model, project_path, session_id)
                """),
        ],
        [ // Version 14
            .sql("ALTER TABLE usage_events ADD COLUMN high_context_pricing INTEGER CHECK(high_context_pricing IN (0, 1))"),
        ],
        [ // Version 15
            .sql("""
                CREATE TABLE IF NOT EXISTS analytics_backfill_sources (
                    source_path TEXT PRIMARY KEY NOT NULL
                )
                """),
            .sql("""
                INSERT OR IGNORE INTO analytics_backfill_sources(source_path)
                SELECT DISTINCT source_path FROM usage_events
                """),
            // Phase 1 checkpoints skipped source bytes that now carry model,
            // project, cache-write, pricing-context, and session metadata. Keep
            // every accounting event so totals remain available even when an old
            // source file has disappeared. Seed each replay above every historical
            // generation so a rewritten source cannot collide with an older row.
            // The conflict update in commit() enriches semantic duplicate events
            // without adding their token deltas a second time.
            .sql("""
                INSERT OR REPLACE INTO app_metadata(key, value)
                SELECT 'legacy_partial_quality', '1'
                WHERE EXISTS (SELECT 1 FROM session_counters WHERE quality != 'exact')
                """),
            .sql("DELETE FROM session_counters"),
            .sql("DELETE FROM session_metadata"),
            .sql("""
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
                """),
            .sql("""
                INSERT INTO app_metadata(key, value) VALUES('data_epoch', '1')
                ON CONFLICT(key) DO UPDATE SET value = CAST(value AS INTEGER) + 1
                """),
        ],
        [ // Version 16
            // Replay only inherited sessions whose first new image was previously
            // skipped. Retain accounting rows and absent-source metadata; semantic
            // event keys prevent replay from adding token deltas a second time.
            .sql("""
                INSERT OR IGNORE INTO analytics_backfill_sources(source_path)
                SELECT source_path FROM parsing_state WHERE inherits_history = 1
                """),
            .sql("""
                DELETE FROM session_counters WHERE session_id IN (
                    SELECT session_id FROM parsing_state WHERE inherits_history = 1
                )
                """),
            .sql("""
                UPDATE parsing_state
                SET generation = MAX(
                        generation + 1,
                        COALESCE((SELECT MAX(usage_events.source_generation) + 1
                            FROM usage_events WHERE usage_events.source_path = parsing_state.source_path),
                            generation + 1)
                    ),
                    committed_offset = 0, skipping_oversized_line = 0,
                    observed_size = 0, modification_time_ns = 0, content_fingerprint = '',
                    has_pending_import = 1, session_id = NULL, inherits_history = 0,
                    session_started_at = NULL, inherited_history_end_ordinal = NULL,
                    history_replay_complete = 1, model = NULL, project_path = NULL,
                    parent_session_id = NULL, project_name = NULL, image_attachment_count = 0
                WHERE inherits_history = 1
                """),
            .sql("""
                INSERT INTO app_metadata(key, value) VALUES('data_epoch', '1')
                ON CONFLICT(key) DO UPDATE SET value = CAST(value AS INTEGER) + 1
                """),
        ],
        [ // Version 17
            // A shorter archive copy must not replace a fuller session's image
            // count. Derive it from source checkpoints, so rewriting a single
            // source can still lower its count. Token events remain unchanged.
            .sql("CREATE INDEX IF NOT EXISTS parsing_state_session_idx ON parsing_state(session_id)"),
            .sql("""
                UPDATE session_metadata
                SET image_attachment_count = (
                    SELECT MAX(image_attachment_count) FROM parsing_state
                    WHERE parsing_state.session_id = session_metadata.session_id
                )
                WHERE EXISTS (
                    SELECT 1 FROM parsing_state
                    WHERE parsing_state.session_id = session_metadata.session_id
                )
                """),
        ],
        [ // Version 18
            .sql("""
                CREATE TABLE IF NOT EXISTS partial_evidence (
                    source TEXT NOT NULL,
                    position INTEGER NOT NULL,
                    observed_at REAL NOT NULL,
                    PRIMARY KEY (source, position)
                )
                """),
            .sql("CREATE INDEX IF NOT EXISTS partial_evidence_time_idx ON partial_evidence(observed_at)"),
        ],
    ]

    #if !CODERIM_RUST_ACCOUNTING
    private static func apply(_ operation: Operation, on database: OpaquePointer) throws {
        switch operation {
        case let .sql(statement):
            try execute(statement.description, on: database)
        case .sessionIDs:
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
        case .eventSources:
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
        case .checkpointSources:
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
        UsageStorageIdentity.digest(value)
    }

    #endif

    static func execute(_ sql: String, on database: OpaquePointer) throws {
        var error: UnsafeMutablePointer<CChar>?
        guard sqlite3_exec(database, sql, nil, nil, &error) == SQLITE_OK else {
            let message = error.map { String(cString: $0) } ?? String(cString: sqlite3_errmsg(database))
            sqlite3_free(error)
            throw SQLiteDatabaseError.statement(message)
        }
    }

}
