import Foundation
import XCTest
@testable import CodeRim

final class SQLiteDatabaseTests: XCTestCase {
    func testMigrationReopenDuplicateInsertAndSnapshot() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("CodexMeter.sqlite")
        let database = try SQLiteDatabase(url: url)
        let checkpoint = SourceCheckpoint(
            sourcePath: "/fixture.jsonl",
            fileIdentity: "1:2",
            generation: 0,
            committedOffset: 100,
            sessionID: "session",
            inheritsHistory: false,
            model: "model",
            projectPath: "/project"
        )
        let date = Date(timeIntervalSince1970: 1_800_000_000)
        let event = UsageEvent(
            eventKey: "stable-event",
            occurredAt: date,
            sessionID: "session",
            model: "model",
            projectPath: "/project",
            usage: TokenUsage(inputTokens: 100, cachedInputTokens: 60, outputTokens: 20),
            sourcePath: checkpoint.sourcePath,
            sourcePosition: 10
        )
        let normalization = UsageNormalizationState(
            cumulativeHighWaterMark: event.usage,
            quality: .exact
        )

        try await database.commit(events: [event], checkpoint: checkpoint, normalizationState: normalization)
        try await database.commit(events: [event], checkpoint: checkpoint, normalizationState: normalization)
        let eventCount = try await database.eventCount()
        let savedCheckpoint = try await database.checkpoint(for: checkpoint.sourcePath)
        XCTAssertEqual(eventCount, 1)
        XCTAssertEqual(savedCheckpoint, checkpoint)

        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let snapshot = try await database.usageSnapshot(
            now: date.addingTimeInterval(60),
            calendar: calendar,
            weekStart: .monday
        )
        XCTAssertEqual(snapshot.allTime, event.usage)
        XCTAssertEqual(snapshot.quality, .exact)

        let reopened = try SQLiteDatabase(url: url)
        let reopenedEventCount = try await reopened.eventCount()
        let reopenedCutoff = try await reopened.importCutoff()
        XCTAssertEqual(reopenedEventCount, 1)
        XCTAssertNil(reopenedCutoff)
    }

    func testTransactionRollsBackInvalidEventAndCheckpoint() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let database = try SQLiteDatabase(url: directory.appendingPathComponent("db.sqlite"))
        let checkpoint = SourceCheckpoint.fresh(sourcePath: "/invalid", fileIdentity: "1:1")
        let invalid = UsageEvent(
            eventKey: "invalid",
            occurredAt: Date(),
            sessionID: nil,
            model: nil,
            projectPath: nil,
            usage: TokenUsage(inputTokens: 1, cachedInputTokens: 2, outputTokens: 0),
            sourcePath: checkpoint.sourcePath,
            sourcePosition: 0
        )

        do {
            try await database.commit(events: [invalid], checkpoint: checkpoint, normalizationState: nil)
            XCTFail("Expected constraint failure")
        } catch {
            let eventCount = try await database.eventCount()
            let savedCheckpoint = try await database.checkpoint(for: checkpoint.sourcePath)
            XCTAssertEqual(eventCount, 0)
            XCTAssertNil(savedCheckpoint)
        }
    }


    func testClearHistoryPersistsCutoffAndRebuildPreservesIt() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let database = try SQLiteDatabase(url: directory.appendingPathComponent("db.sqlite"))
        let cutoff = Date(timeIntervalSince1970: 1_800_000_000)

        let compactionStatus = try await database.clearLocalHistory(at: cutoff)
        XCTAssertEqual(compactionStatus, .complete)
        let savedCutoff = try await database.importCutoff()
        XCTAssertEqual(savedCutoff, cutoff)
        try await database.rebuildStatistics()
        let cutoffAfterRebuild = try await database.importCutoff()
        XCTAssertEqual(cutoffAfterRebuild, cutoff)
    }

    func testDuplicateEventCannotRegressNormalizationCursor() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let database = try SQLiteDatabase(url: directory.appendingPathComponent("db.sqlite"))
        let checkpoint = SourceCheckpoint(
            sourcePath: "/fixture.jsonl",
            fileIdentity: "1:2",
            generation: 0,
            committedOffset: 100,
            sessionID: "session",
            inheritsHistory: false,
            model: nil,
            projectPath: nil
        )
        let event = UsageEvent(
            eventKey: "same-event",
            occurredAt: Date(timeIntervalSince1970: 1_800_000_100),
            sessionID: "session",
            model: nil,
            projectPath: nil,
            usage: TokenUsage(inputTokens: 150, cachedInputTokens: 90, outputTokens: 30),
            sourcePath: "source",
            sourcePosition: 10
        )
        let latest = UsageNormalizationState(
            cumulativeHighWaterMark: event.usage,
            lastObservedAt: event.occurredAt,
            quality: .exact
        )
        let stale = UsageNormalizationState(
            cumulativeHighWaterMark: TokenUsage(inputTokens: 100, cachedInputTokens: 60, outputTokens: 20),
            lastObservedAt: event.occurredAt.addingTimeInterval(-60),
            quality: .partial
        )

        try await database.commit(events: [event], checkpoint: checkpoint, normalizationState: latest)
        try await database.commit(events: [event], checkpoint: checkpoint, normalizationState: stale)

        let saved = try await database.normalizationState(for: "session")
        XCTAssertEqual(saved, latest)
    }

    func testMaintenanceEpochRejectsAnOlderInFlightScan() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let database = try SQLiteDatabase(url: directory.appendingPathComponent("db.sqlite"))
        let oldEpoch = try await database.dataEpoch()
        _ = try await database.clearLocalHistory(at: Date(timeIntervalSince1970: 1_800_000_000))
        let checkpoint = SourceCheckpoint.fresh(sourcePath: "/stale.jsonl", fileIdentity: "1:1")

        do {
            try await database.commit(
                events: [],
                checkpoint: checkpoint,
                normalizationState: nil,
                expectedEpoch: oldEpoch
            )
            XCTFail("Expected stale scan rejection")
        } catch SQLiteDatabaseError.staleScan {
            let savedCheckpoint = try await database.checkpoint(for: checkpoint.sourcePath)
            XCTAssertNil(savedCheckpoint)
        }
    }

    func testVersion2MigrationRebuildsDerivedAccountingAndPreservesCutoff() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("db.sqlite")
        let database = try SQLiteDatabase(url: url)
        let cutoff = Date(timeIntervalSince1970: 1_799_999_000)
        _ = try await database.clearLocalHistory(at: cutoff)

        let rawSessionID = "raw-session-id"
        let checkpoint = SourceCheckpoint(
            sourcePath: "/Users/example/.codex/sessions/fixture.jsonl",
            fileIdentity: "1:2",
            generation: 0,
            committedOffset: 100,
            sessionID: rawSessionID,
            inheritsHistory: false,
            model: "private-model",
            projectPath: "/private/project"
        )
        let usage = TokenUsage(
            inputTokens: 100,
            cachedInputTokens: 60,
            cacheWriteInputTokens: 0,
            outputTokens: 20
        )
        let eventDate = Date(timeIntervalSince1970: 1_800_000_000)
        let event = UsageEvent(
            eventKey: "stable-event",
            occurredAt: eventDate,
            sessionID: rawSessionID,
            model: "private-model",
            projectPath: "/private/project",
            usage: usage,
            sourcePath: checkpoint.sourcePath,
            sourcePosition: 10
        )
        try await database.commit(
            events: [event],
            checkpoint: checkpoint,
            normalizationState: UsageNormalizationState(
                cumulativeHighWaterMark: usage,
                lastObservedAt: eventDate,
                quality: .exact
            )
        )
        try await database.prepareVersion2FixtureForTesting()

        let migrated = try SQLiteDatabase(url: url)
        let migratedCount = try await migrated.eventCount()
        let migratedCutoff = try await migrated.importCutoff()
        XCTAssertEqual(migratedCount, 0)
        XCTAssertEqual(migratedCutoff, cutoff)
        let snapshot = try await migrated.usageSnapshot(
            now: eventDate.addingTimeInterval(60),
            calendar: Calendar(identifier: .gregorian),
            weekStart: .monday
        )
        XCTAssertEqual(snapshot.allTime, .zero)

        let rawState = try await migrated.normalizationState(for: rawSessionID)
        XCTAssertEqual(rawState, .empty)
        let migratedCheckpoint = try await migrated.checkpoint(for: checkpoint.sourcePath)
        XCTAssertNil(migratedCheckpoint)
    }

    func testVersion11MigrationRebuildsDerivedAccountingAndPreservesCutoff() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("db.sqlite")
        let rootUsage = TokenUsage(inputTokens: 100, cachedInputTokens: 60, outputTokens: 20)
        let inheritedUsage = TokenUsage(inputTokens: 5_000, cachedInputTokens: 4_000, outputTokens: 500)
        let occurredAt = Date(timeIntervalSince1970: 1_800_000_000)
        let cutoff = occurredAt.addingTimeInterval(-60)
        var previousEpoch: Int64 = 0

        do {
            let database = try SQLiteDatabase(url: url)
            _ = try await database.clearLocalHistory(at: cutoff)
            for (path, sessionID, inheritsHistory, usage) in [
                ("root-source", "root-session", false, rootUsage),
                ("child-source", "child-session", true, inheritedUsage)
            ] {
                let checkpoint = SourceCheckpoint(
                    sourcePath: path,
                    fileIdentity: path,
                    generation: 0,
                    committedOffset: 100,
                    sessionID: sessionID,
                    inheritsHistory: inheritsHistory,
                    model: nil,
                    projectPath: nil
                )
                let event = UsageEvent(
                    eventKey: path,
                    occurredAt: occurredAt,
                    sessionID: sessionID,
                    model: nil,
                    projectPath: nil,
                    usage: usage,
                    sourcePath: path,
                    sourcePosition: 10
                )
                try await database.commit(
                    events: [event],
                    checkpoint: checkpoint,
                    normalizationState: UsageNormalizationState(
                        cumulativeHighWaterMark: usage,
                        lastObservedAt: occurredAt,
                        quality: .exact
                    )
                )
            }
            previousEpoch = try await database.importPolicy().dataEpoch
            try await database.prepareVersion10FixtureForTesting()
        }

        let migrated = try SQLiteDatabase(url: url)
        let eventCount = try await migrated.eventCount()
        let rootCheckpoint = try await migrated.checkpoint(for: "root-source")
        let childCheckpoint = try await migrated.checkpoint(for: "child-source")
        let rootState = try await migrated.normalizationState(for: "root-session")
        let childState = try await migrated.normalizationState(for: "child-session")
        let policy = try await migrated.importPolicy()
        XCTAssertEqual(eventCount, 0)
        XCTAssertNil(rootCheckpoint)
        XCTAssertNil(childCheckpoint)
        XCTAssertEqual(rootState, .empty)
        XCTAssertEqual(childState, .empty)
        XCTAssertEqual(policy.cutoff, cutoff)
        XCTAssertGreaterThan(policy.dataEpoch, previousEpoch)
    }

    func testVersion15MigrationPreservesAccountingAndResetsDerivedCursors() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("db.sqlite")
        let occurredAt = Date(timeIntervalSince1970: 1_800_000_000)
        let usage = TokenUsage(
            inputTokens: 100,
            cachedInputTokens: 60,
            cacheWriteInputTokens: 0,
            outputTokens: 20
        )
        let checkpoint = SourceCheckpoint(
            sourcePath: "/fixture.jsonl",
            fileIdentity: "1:2",
            generation: 0,
            committedOffset: 100,
            sessionID: "session",
            inheritsHistory: false,
            model: "gpt-5.6-sol",
            projectPath: "project-id"
        )
        var previousEpoch: Int64 = 0

        do {
            let database = try SQLiteDatabase(url: url)
            let event = UsageEvent(
                eventKey: "stable-event",
                occurredAt: occurredAt,
                sessionID: "session",
                model: "gpt-5.6-sol",
                projectPath: "project-id",
                usage: usage,
                sourcePath: checkpoint.sourcePath,
                sourcePosition: 10
            )
            try await database.commit(
                events: [event],
                checkpoint: checkpoint,
                normalizationState: UsageNormalizationState(
                    cumulativeHighWaterMark: usage,
                    lastObservedAt: occurredAt,
                    quality: .exact
                )
            )
            previousEpoch = try await database.dataEpoch()
            try await database.prepareVersion13FixtureForTesting()
        }

        let migrated = try SQLiteDatabase(url: url)
        let migratedEventCount = try await migrated.eventCount()
        let migratedEpoch = try await migrated.dataEpoch()
        let migratedCheckpoint = try await migrated.checkpoint(for: checkpoint.sourcePath)
        let migratedNormalizationState = try await migrated.normalizationState(for: "session")
        XCTAssertEqual(migratedEventCount, 1)
        XCTAssertGreaterThan(migratedEpoch, previousEpoch)
        XCTAssertEqual(migratedCheckpoint?.generation, 1)
        XCTAssertEqual(migratedCheckpoint?.committedOffset, 0)
        XCTAssertNil(migratedCheckpoint?.sessionID)
        XCTAssertEqual(migratedNormalizationState, .empty)
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let snapshot = try await migrated.usageSnapshot(
            now: occurredAt.addingTimeInterval(60),
            calendar: calendar,
            weekStart: .monday
        )
        XCTAssertEqual(snapshot.allTime, usage)
        let analytics = try await migrated.analyticsSnapshot(
            range: .today,
            through: occurredAt.addingTimeInterval(60),
            calendar: calendar
        )
        XCTAssertFalse(try XCTUnwrap(analytics.models.first).hasUnknownPricingContext)
        XCTAssertNotNil(
            CostEstimator().estimate(
                analytics.models.map {
                    ModelTokenUsageSample(
                        modelID: $0.modelID ?? "unknown",
                        usage: $0.usage,
                        highContextUsage: $0.highContextUsage,
                        hasUnknownPricingContext: $0.hasUnknownPricingContext,
                        occurredAt: occurredAt
                    )
                }
            ).amountUSD
        )
    }

    func testVersion15MigrationKeepsMissingLegacyPartialHistoryConservative() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("db.sqlite")
        let occurredAt = Date(timeIntervalSince1970: 1_800_000_000)
        let usage = TokenUsage(inputTokens: 100, cachedInputTokens: 50, outputTokens: 20)

        do {
            let database = try SQLiteDatabase(url: url)
            let checkpoint = SourceCheckpoint(
                sourcePath: "missing-source",
                fileIdentity: "missing-identity",
                generation: 0,
                committedOffset: 100,
                sessionID: "partial-session",
                inheritsHistory: false,
                model: nil,
                projectPath: nil
            )
            try await database.commit(
                events: [
                    UsageEvent(
                        eventKey: "partial-event",
                        occurredAt: occurredAt,
                        sessionID: "partial-session",
                        model: nil,
                        projectPath: nil,
                        usage: usage,
                        sourcePath: checkpoint.sourcePath,
                        sourcePosition: 10
                    )
                ],
                checkpoint: checkpoint,
                normalizationState: UsageNormalizationState(
                    cumulativeHighWaterMark: usage,
                    lastObservedAt: occurredAt,
                    quality: .partial
                )
            )
            try await database.prepareVersion14FixtureForTesting()
        }

        let migrated = try SQLiteDatabase(url: url)
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let snapshot = try await migrated.usageSnapshot(
            now: occurredAt.addingTimeInterval(60),
            calendar: calendar,
            weekStart: .monday
        )
        XCTAssertEqual(snapshot.allTime, usage)
        XCTAssertEqual(snapshot.quality, .partial)
        let analytics = try await migrated.analyticsSnapshot(
            range: .today,
            through: occurredAt.addingTimeInterval(60),
            calendar: calendar
        )
        XCTAssertEqual(analytics.usage, usage)
        XCTAssertEqual(analytics.quality, .partial)
        let migratedEventCount = try await migrated.eventCount()
        XCTAssertEqual(migratedEventCount, 1)
    }

    func testDatabaseFilesUseOwnerOnlyPermissions() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("db.sqlite")
        _ = try SQLiteDatabase(url: url)

        let directoryMode = try XCTUnwrap(
            FileManager.default.attributesOfItem(atPath: directory.path)[.posixPermissions] as? NSNumber
        ).intValue & 0o777
        let databaseMode = try XCTUnwrap(
            FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber
        ).intValue & 0o777
        let startupLockMode = try XCTUnwrap(
            FileManager.default.attributesOfItem(atPath: url.path + ".startup.lock")[.posixPermissions] as? NSNumber
        ).intValue & 0o777
        let fingerprintKeyMode = try XCTUnwrap(
            FileManager.default.attributesOfItem(atPath: url.path + ".fingerprint-key")[.posixPermissions] as? NSNumber
        ).intValue & 0o777
        XCTAssertEqual(directoryMode, 0o700)
        XCTAssertEqual(databaseMode, 0o600)
        XCTAssertEqual(startupLockMode, 0o600)
        XCTAssertEqual(fingerprintKeyMode, 0o600)
    }

    func testFutureOnlyEventsDoNotMakeTheCurrentSnapshotLookAvailable() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let database = try SQLiteDatabase(url: directory.appendingPathComponent("db.sqlite"))
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let future = now.addingTimeInterval(3_600)
        let checkpoint = SourceCheckpoint(
            sourcePath: "/future.jsonl",
            fileIdentity: "1:1",
            generation: 0,
            committedOffset: 1,
            sessionID: "future-session",
            inheritsHistory: false,
            model: nil,
            projectPath: nil
        )
        let event = UsageEvent(
            eventKey: "future-event",
            occurredAt: future,
            sessionID: "future-session",
            model: nil,
            projectPath: nil,
            usage: TokenUsage(inputTokens: 100, cachedInputTokens: 50, outputTokens: 20),
            sourcePath: "future-source",
            sourcePosition: 0
        )
        try await database.commit(
            events: [event],
            checkpoint: checkpoint,
            normalizationState: UsageNormalizationState(
                cumulativeHighWaterMark: event.usage,
                lastObservedAt: future,
                quality: .exact
            )
        )

        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let snapshot = try await database.usageSnapshot(now: now, calendar: calendar, weekStart: .monday)
        XCTAssertEqual(snapshot.today, .zero)
        XCTAssertEqual(snapshot.week, .zero)
        XCTAssertEqual(snapshot.month, .zero)
        XCTAssertEqual(snapshot.allTime, .zero)
        XCTAssertNil(snapshot.updatedAt)
        XCTAssertEqual(snapshot.quality, .unavailable)
    }

    func testStaleNormalizationComputationRollsBackBeforeInsert() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let database = try SQLiteDatabase(url: directory.appendingPathComponent("db.sqlite"))
        let sessionID = "session"
        let initialCursor = UsageNormalizationState.empty
        let newerDate = Date(timeIntervalSince1970: 1_800_000_200)
        let newer = UsageNormalizationState(
            cumulativeHighWaterMark: TokenUsage(inputTokens: 200, cachedInputTokens: 100, outputTokens: 40),
            lastObservedAt: newerDate,
            quality: .exact
        )
        let stale = UsageNormalizationState(
            cumulativeHighWaterMark: TokenUsage(inputTokens: 150, cachedInputTokens: 75, outputTokens: 30),
            lastObservedAt: newerDate.addingTimeInterval(-50),
            quality: .exact
        )
        let newerCheckpoint = SourceCheckpoint(
            sourcePath: "/newer.jsonl", fileIdentity: "1:1", generation: 0, committedOffset: 10,
            sessionID: sessionID, inheritsHistory: false, model: nil, projectPath: nil
        )
        let staleCheckpoint = SourceCheckpoint(
            sourcePath: "/stale.jsonl", fileIdentity: "1:2", generation: 0, committedOffset: 10,
            sessionID: sessionID, inheritsHistory: false, model: nil, projectPath: nil
        )
        let newerEvent = UsageEvent(
            eventKey: "newer", occurredAt: newerDate, sessionID: sessionID, model: nil, projectPath: nil,
            usage: newer.cumulativeHighWaterMark!, sourcePath: "newer", sourcePosition: 0
        )
        let staleEvent = UsageEvent(
            eventKey: "stale", occurredAt: newerDate.addingTimeInterval(-50), sessionID: sessionID, model: nil, projectPath: nil,
            usage: TokenUsage(inputTokens: 150, cachedInputTokens: 75, outputTokens: 30), sourcePath: "stale", sourcePosition: 0
        )

        try await database.commit(
            events: [newerEvent], checkpoint: newerCheckpoint, normalizationState: newer,
            expectedNormalizationState: initialCursor
        )
        do {
            try await database.commit(
                events: [staleEvent], checkpoint: staleCheckpoint, normalizationState: stale,
                expectedNormalizationState: initialCursor
            )
            XCTFail("Expected stale cursor rejection")
        } catch SQLiteDatabaseError.staleScan {
            let eventCount = try await database.eventCount()
            let savedState = try await database.normalizationState(for: sessionID)
            let staleCheckpointState = try await database.checkpoint(for: staleCheckpoint.sourcePath)
            XCTAssertEqual(eventCount, 1)
            XCTAssertEqual(savedState, newer)
            XCTAssertNil(staleCheckpointState)
        }
    }

    func testDatabaseSafetyLimitRejectsAnEventWithoutAdvancingCheckpoint() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let database = try SQLiteDatabase(
            url: directory.appendingPathComponent("db.sqlite"),
            maximumDatabaseBytes: 1
        )
        let checkpoint = SourceCheckpoint.fresh(sourcePath: "source", fileIdentity: "1:1")
        let event = UsageEvent(
            eventKey: "event",
            occurredAt: Date(),
            sessionID: nil,
            model: nil,
            projectPath: nil,
            usage: TokenUsage(inputTokens: 1, cachedInputTokens: 0, outputTokens: 0),
            sourcePath: "source",
            sourcePosition: 0
        )

        do {
            try await database.commit(events: [event], checkpoint: checkpoint, normalizationState: nil)
            XCTFail("Expected the database safety limit to reject the event")
        } catch SQLiteDatabaseError.resourceLimit {
            let count = try await database.eventCount()
            let savedCheckpoint = try await database.checkpoint(for: checkpoint.sourcePath)
            XCTAssertEqual(count, 0)
            XCTAssertNil(savedCheckpoint)
        }
    }

    func testConcurrentColdStartInitializationIsSerialized() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("db.sqlite")

        try await withThrowingTaskGroup(of: Int64.self) { group in
            for _ in 0..<8 {
                group.addTask {
                    let database = try SQLiteDatabase(url: url)
                    return try await database.eventCount()
                }
            }
            for try await count in group {
                XCTAssertEqual(count, 0)
            }
        }
    }

    func testSnapshotPeriodsComeFromOneReadTransaction() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("db.sqlite")
        let reader = try SQLiteDatabase(url: url)
        let writer = try SQLiteDatabase(url: url)
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!

        let writerTask = Task {
            for index in 0..<200 {
                let checkpoint = SourceCheckpoint(
                    sourcePath: "/writer-\(index).jsonl", fileIdentity: "1:\(index)", generation: 0,
                    committedOffset: 1, sessionID: nil, inheritsHistory: false, model: nil, projectPath: nil
                )
                let event = UsageEvent(
                    eventKey: "event-\(index)", occurredAt: now.addingTimeInterval(-1), sessionID: nil,
                    model: nil, projectPath: nil,
                    usage: TokenUsage(inputTokens: 1, cachedInputTokens: 0, outputTokens: 0),
                    sourcePath: "source-\(index)", sourcePosition: 0
                )
                try await writer.commit(events: [event], checkpoint: checkpoint, normalizationState: nil)
            }
        }

        for _ in 0..<50 {
            let snapshot = try await reader.usageSnapshot(now: now, calendar: calendar, weekStart: .monday)
            XCTAssertEqual(snapshot.today, snapshot.week)
            XCTAssertEqual(snapshot.week, snapshot.month)
            XCTAssertEqual(snapshot.month, snapshot.allTime)
        }
        try await writerTask.value
    }
}
