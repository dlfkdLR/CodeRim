import CryptoKit
import Foundation
import XCTest
@testable import CodeRim

final class CodexUsageCollectorTests: XCTestCase {
    func testVersion15BackfillEnrichesLegacyRowsWithoutChangingTotals() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let sessionID = "90909090-9090-9090-9090-909090909090"
        let source = sessions.appendingPathComponent(
            "rollout-2026-08-27T00-00-00-\(sessionID).jsonl"
        )
        let metadata =
            #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"90909090-9090-9090-9090-909090909090","model":"gpt-5.5","cwd":"/tmp/legacy-project"}}"#
        let token = tokenLine(
            input: 100,
            cached: 50,
            output: 20,
            lastInput: 100,
            lastCached: 50,
            lastOutput: 20,
            ordinal: 1
        )
        let sourceData = Data((metadata + "\n" + token + "\n").utf8)
        try sourceData.write(to: source)

        let databaseURL = root.appendingPathComponent("usage.sqlite")
        let sourceIdentity = try XCTUnwrap(
            CodexSourceDiscovery().discover(in: [sessions]).first?.identity
        )
        let sourceKey = storageIdentifier(source.standardizedFileURL.path)
        let storedSessionID = storageIdentifier(sessionID)
        let occurredAt = try Date.ISO8601FormatStyle().parse("2026-08-27T00:01:00Z")
        let usage = TokenUsage(inputTokens: 100, cachedInputTokens: 50, outputTokens: 20)

        do {
            let legacyDatabase = try SQLiteDatabase(url: databaseURL)
            let checkpoint = SourceCheckpoint(
                sourcePath: sourceKey,
                fileIdentity: sourceIdentity,
                generation: 0,
                committedOffset: Int64(sourceData.count),
                sessionID: storedSessionID,
                inheritsHistory: false,
                model: nil,
                projectPath: nil
            )
            let legacyEvent = UsageEvent(
                eventKey: legacyEventKey(
                    sessionID: storedSessionID,
                    occurredAt: occurredAt,
                    ordinal: 1,
                    usage: usage
                ),
                occurredAt: occurredAt,
                sessionID: storedSessionID,
                model: nil,
                projectPath: nil,
                usage: usage,
                sourcePath: sourceKey,
                sourcePosition: Int64(Data((metadata + "\n").utf8).count)
            )
            try await legacyDatabase.commit(
                events: [legacyEvent],
                checkpoint: checkpoint,
                normalizationState: UsageNormalizationState(
                    cumulativeHighWaterMark: usage,
                    lastObservedAt: occurredAt,
                    quality: .exact
                )
            )
            try await legacyDatabase.prepareVersion14FixtureForTesting()
        }

        let migrated = try SQLiteDatabase(url: databaseURL)
        let migratedEventCount = try await migrated.eventCount()
        let migratedCheckpoint = try await migrated.checkpoint(for: sourceKey)
        let migratedState = try await migrated.normalizationState(for: storedSessionID)
        XCTAssertEqual(migratedEventCount, 1)
        XCTAssertEqual(migratedCheckpoint?.generation, 1)
        XCTAssertEqual(migratedCheckpoint?.committedOffset, 0)
        XCTAssertEqual(migratedState, .empty)

        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        let collector = CodexUsageCollector(database: migrated, roots: [sessions])
        var refresh = try await collector.refresh(
            now: now,
            calendar: utcCalendar,
            weekStart: .monday
        )
        while refresh.hasMoreWork {
            refresh = try await collector.refresh(
                now: now,
                calendar: utcCalendar,
                weekStart: .monday
            )
        }

        XCTAssertEqual(refresh.snapshot.allTime, usage)
        let backfilledEventCount = try await migrated.eventCount()
        XCTAssertEqual(backfilledEventCount, 1)
        let analytics = try await migrated.analyticsSnapshot(
            range: .today,
            through: now,
            calendar: utcCalendar
        )
        XCTAssertEqual(analytics.usage, usage)
        XCTAssertEqual(analytics.models.first?.modelID, "gpt-5.5")
        XCTAssertEqual(analytics.projects.first?.name, "legacy-project")
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

    func testVersion15BackfillUsesANewGenerationAfterHistoricalRewrites() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let sessionID = "91919191-9191-9191-9191-919191919191"
        let source = sessions.appendingPathComponent(
            "rollout-2026-08-27T00-00-00-\(sessionID).jsonl"
        )
        let metadata =
            #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"91919191-9191-9191-9191-919191919191","model":"gpt-5.5","cwd":"/tmp/rewrite-project"}}"#
        let firstToken = tokenLine(
            input: 100,
            cached: 50,
            output: 20,
            lastInput: 100,
            lastCached: 50,
            lastOutput: 20,
            ordinal: 1
        )
        let sourceData = Data((metadata + "\n" + firstToken + "\n").utf8)
        try sourceData.write(to: source)

        let databaseURL = root.appendingPathComponent("usage.sqlite")
        let sourceIdentity = try XCTUnwrap(
            CodexSourceDiscovery().discover(in: [sessions]).first?.identity
        )
        let sourceKey = storageIdentifier(source.standardizedFileURL.path)
        let storedSessionID = storageIdentifier(sessionID)
        let currentDate = try Date.ISO8601FormatStyle().parse("2026-08-27T00:01:00Z")
        let currentUsage = TokenUsage(inputTokens: 100, cachedInputTokens: 50, outputTokens: 20)
        let historicalDate = currentDate.addingTimeInterval(-120)
        let historicalUsage = TokenUsage(inputTokens: 40, cachedInputTokens: 20, outputTokens: 8)
        let sourcePosition = Int64(Data((metadata + "\n").utf8).count)

        do {
            let legacyDatabase = try SQLiteDatabase(url: databaseURL)
            let historicalCheckpoint = SourceCheckpoint(
                sourcePath: sourceKey,
                fileIdentity: sourceIdentity,
                generation: 0,
                committedOffset: Int64(sourceData.count),
                sessionID: storedSessionID,
                inheritsHistory: false,
                model: nil,
                projectPath: nil
            )
            try await legacyDatabase.commit(
                events: [
                    UsageEvent(
                        eventKey: "historical-generation-zero",
                        occurredAt: historicalDate,
                        sessionID: storedSessionID,
                        model: nil,
                        projectPath: nil,
                        usage: historicalUsage,
                        sourcePath: sourceKey,
                        sourcePosition: sourcePosition
                    )
                ],
                checkpoint: historicalCheckpoint,
                normalizationState: UsageNormalizationState(
                    cumulativeHighWaterMark: historicalUsage,
                    lastObservedAt: historicalDate,
                    quality: .exact
                )
            )

            var currentCheckpoint = historicalCheckpoint
            currentCheckpoint.generation = 1
            try await legacyDatabase.commit(
                events: [
                    UsageEvent(
                        eventKey: legacyEventKey(
                            sessionID: storedSessionID,
                            occurredAt: currentDate,
                            ordinal: 1,
                            usage: currentUsage
                        ),
                        occurredAt: currentDate,
                        sessionID: storedSessionID,
                        model: nil,
                        projectPath: nil,
                        usage: currentUsage,
                        sourcePath: sourceKey,
                        sourcePosition: sourcePosition
                    )
                ],
                checkpoint: currentCheckpoint,
                normalizationState: UsageNormalizationState(
                    cumulativeHighWaterMark: currentUsage,
                    lastObservedAt: currentDate,
                    quality: .exact
                )
            )
            try await legacyDatabase.prepareVersion14FixtureForTesting()
        }

        let migrated = try SQLiteDatabase(url: databaseURL)
        let replayCheckpoint = try await migrated.checkpoint(for: sourceKey)
        XCTAssertEqual(replayCheckpoint?.generation, 2)
        XCTAssertEqual(replayCheckpoint?.committedOffset, 0)
        let collector = CodexUsageCollector(database: migrated, roots: [sessions])
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        _ = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        let replayedEventCount = try await migrated.eventCount()
        XCTAssertEqual(replayedEventCount, 2)

        let secondToken = tokenLine(
            input: 150,
            cached: 75,
            output: 30,
            lastInput: 50,
            lastCached: 25,
            lastOutput: 10,
            ordinal: 2
        )
        let handle = try FileHandle(forWritingTo: source)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data((secondToken + "\n").utf8))
        try handle.close()
        let refreshed = try await collector.refresh(
            now: now,
            calendar: utcCalendar,
            weekStart: .monday
        )

        XCTAssertEqual(refreshed.snapshot.allTime.inputTokens, 190)
        XCTAssertEqual(refreshed.snapshot.allTime.cachedInputTokens, 95)
        XCTAssertEqual(refreshed.snapshot.allTime.outputTokens, 38)
        let appendedEventCount = try await migrated.eventCount()
        XCTAssertEqual(appendedEventCount, 3)
        let analytics = try await migrated.analyticsSnapshot(
            range: .today,
            through: now,
            calendar: utcCalendar
        )
        XCTAssertEqual(
            analytics.models.first(where: { $0.modelID == "gpt-5.5" })?.usage.inputTokens,
            150
        )
        XCTAssertEqual(
            analytics.projects.first(where: { $0.name == "rewrite-project" })?.usage.inputTokens,
            150
        )
    }

    func testEquivalentWorkingDirectorySpellingsShareOneProjectIdentifier() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [])
        let base = root.appendingPathComponent("project", isDirectory: true).path

        let plainProjection = try await collector.projectProjectionForTesting(base)
        let trailingSlashProjection = try await collector.projectProjectionForTesting(base + "/")
        let dotSegmentProjection = try await collector.projectProjectionForTesting(base + "/./")
        let plain = try XCTUnwrap(plainProjection)
        let trailingSlash = try XCTUnwrap(trailingSlashProjection)
        let dotSegment = try XCTUnwrap(dotSegmentProjection)

        XCTAssertEqual(plain.id, trailingSlash.id)
        XCTAssertEqual(plain.id, dotSegment.id)
        XCTAssertEqual(plain.name, "project")
    }

    func testClearHistoryCutoffExcludesEarlierWholeSessionImages() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let source = sessions.appendingPathComponent(
            "rollout-2026-08-27T00-00-00-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.jsonl"
        )
        let lines = [
            #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","model":"gpt-5.5","cwd":"/tmp/project"}}"#,
            #"{"timestamp":"2026-08-27T00:01:00Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_image","image_url":"private-old"}]}}"#,
            tokenLine(input: 100, cached: 50, output: 10, lastInput: 100, lastCached: 50, lastOutput: 10, ordinal: 1),
            #"{"timestamp":"2026-08-27T00:03:00Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_image","image_url":"private-new"}]}}"#,
            #"{"timestamp":"2026-08-27T00:04:00Z","type":"event_msg","ordinal":2,"payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":200,"cached_input_tokens":100,"output_tokens":20},"last_token_usage":{"input_tokens":100,"cached_input_tokens":50,"output_tokens":10}}}}"#
        ]
        try Data((lines.joined(separator: "\n") + "\n").utf8).write(to: source)

        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [sessions])
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        _ = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        _ = try await database.clearLocalHistory(
            at: try Date.ISO8601FormatStyle().parse("2026-08-27T00:02:00Z")
        )
        var refreshed = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        while refreshed.hasMoreWork {
            refreshed = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        }
        let analytics = try await database.analyticsSnapshot(
            range: .today,
            through: now,
            calendar: utcCalendar
        )

        XCTAssertEqual(analytics.usage, TokenUsage(inputTokens: 100, cachedInputTokens: 50, outputTokens: 10))
        XCTAssertEqual(try XCTUnwrap(analytics.sessions.first).imageAttachmentCount, 1)
    }


    func testIncrementalPartialDuplicateAndTruncationHandling() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let source = sessions.appendingPathComponent("rollout-2026-08-27T00-00-00-11111111-1111-1111-1111-111111111111.jsonl")
        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [sessions])

        let metadata = #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"11111111-1111-1111-1111-111111111111","cwd":"/project"}}"#
        let first = tokenLine(input: 100, cached: 60, output: 20, lastInput: 100, lastCached: 60, lastOutput: 20, ordinal: 1)
        let duplicate = tokenLine(input: 100, cached: 60, output: 20, lastInput: 100, lastCached: 60, lastOutput: 20, ordinal: 2)
        let partial = #"{"timestamp":"2026-08-27T00:03:00Z","type":"event_msg","ordinal":3,"payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":150"#
        try Data((metadata + "\n" + first + "\n" + duplicate + "\n" + partial).utf8).write(to: source)

        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        let initial = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(initial.snapshot.allTime, TokenUsage(inputTokens: 100, cachedInputTokens: 60, outputTokens: 20))
        let initialEventCount = try await database.eventCount()
        XCTAssertEqual(initialEventCount, 1)

        let completion = #", "cached_input_tokens":90,"output_tokens":30},"last_token_usage":{"input_tokens":50,"cached_input_tokens":30,"output_tokens":10}}}}"#
        let handle = try FileHandle(forWritingTo: source)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data((completion + "\n").utf8))
        try handle.close()

        let appended = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(appended.snapshot.allTime, TokenUsage(inputTokens: 150, cachedInputTokens: 90, outputTokens: 30))
        let appendedEventCount = try await database.eventCount()
        XCTAssertEqual(appendedEventCount, 2)

        try Data((metadata + "\n" + first + "\n").utf8).write(to: source)
        let truncated = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(truncated.snapshot.allTime, appended.snapshot.allTime)
        let truncatedEventCount = try await database.eventCount()
        XCTAssertEqual(truncatedEventCount, 2)
    }

    func testNoOrdinalReplayReplacementAndRenameRemainIdempotent() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let original = sessions.appendingPathComponent("rollout-2026-08-27T00-00-00-22222222-2222-2222-2222-222222222222.jsonl")
        let renamed = sessions.appendingPathComponent("renamed-rollout.jsonl")
        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [sessions])
        let metadata = #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"22222222-2222-2222-2222-222222222222"}}"#
        let first = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:01:00Z",
            input: 100,
            cached: 60,
            output: 20,
            lastInput: 100,
            lastCached: 60,
            lastOutput: 20
        )
        let second = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:02:00Z",
            input: 150,
            cached: 90,
            output: 30,
            lastInput: 50,
            lastCached: 30,
            lastOutput: 10
        )
        try Data((metadata + "\n" + first + "\n" + second + "\n").utf8).write(to: original)

        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        let initial = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(initial.snapshot.allTime, TokenUsage(inputTokens: 150, cachedInputTokens: 90, outputTokens: 30))
        let initialCount = try await database.eventCount()
        XCTAssertEqual(initialCount, 2)

        try Data((metadata + "\n" + first + "\n").utf8).write(to: original, options: .atomic)
        let replaced = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(replaced.snapshot.allTime, initial.snapshot.allTime)
        let replacedCount = try await database.eventCount()
        XCTAssertEqual(replacedCount, 2)

        try FileManager.default.moveItem(at: original, to: renamed)
        let afterRename = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(afterRename.snapshot.allTime, initial.snapshot.allTime)
        let renamedCount = try await database.eventCount()
        XCTAssertEqual(renamedCount, 2)
    }

    func testMalformedTokenEventMarksSnapshotPartial() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let source = sessions.appendingPathComponent("rollout-2026-08-27T00-00-00-33333333-3333-3333-3333-333333333333.jsonl")
        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [sessions])
        let metadata = #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"33333333-3333-3333-3333-333333333333"}}"#
        let valid = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:01:00Z",
            input: 100,
            cached: 60,
            output: 20,
            lastInput: 100,
            lastCached: 60,
            lastOutput: 20
        )
        let malformed = #"{"timestamp":"2026-08-27T00:02:00Z","type":"event_msg","payload":{"type":"token_count"}}"#
        try Data((metadata + "\n" + valid + "\n" + malformed + "\n").utf8).write(to: source)

        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        let result = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(result.snapshot.quality, .partial)
    }

    func testOversizedUnterminatedLineRemainsQuarantinedAcrossRefreshes() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let source = sessions.appendingPathComponent("rollout-2026-08-27T00-00-00-44444444-4444-4444-4444-444444444444.jsonl")
        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [sessions])
        let metadata = #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"44444444-4444-4444-4444-444444444444"}}"#
        var data = Data((metadata + "\n").utf8)
        data.append(Data(repeating: 0x78, count: CodexJSONLParser.maximumLineBytes + 1))
        try data.write(to: source)
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")

        _ = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        let quarantined = try await database.checkpoint(for: storageIdentifier(source.path))
        XCTAssertEqual(quarantined?.isSkippingOversizedLine, true)

        let swallowed = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:01:00Z",
            input: 100,
            cached: 60,
            output: 20,
            lastInput: 100,
            lastCached: 60,
            lastOutput: 20
        )
        let handle = try FileHandle(forWritingTo: source)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data((swallowed + "\n").utf8))
        try handle.close()
        let afterSwallowed = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(afterSwallowed.snapshot.allTime, .zero)

        let valid = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:02:00Z",
            input: 200,
            cached: 100,
            output: 40,
            lastInput: 200,
            lastCached: 100,
            lastOutput: 40
        )
        let validHandle = try FileHandle(forWritingTo: source)
        try validHandle.seekToEnd()
        try validHandle.write(contentsOf: Data((valid + "\n").utf8))
        try validHandle.close()
        let recovered = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(recovered.snapshot.allTime, TokenUsage(inputTokens: 200, cachedInputTokens: 100, outputTokens: 40))
        let recoveredEventCount = try await database.eventCount()
        XCTAssertEqual(recoveredEventCount, 1)
    }

    func testSameInodeSameSizeRewriteIsDetected() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let source = sessions.appendingPathComponent("rollout-2026-08-27T00-00-00-55555555-5555-5555-5555-555555555555.jsonl")
        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [sessions])
        let metadata = #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"55555555-5555-5555-5555-555555555555"}}"#
        let first = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:01:00Z",
            input: 200, cached: 0, output: 40,
            lastInput: 200, lastCached: 0, lastOutput: 40
        )
        let replacement = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:02:00Z",
            input: 100, cached: 0, output: 20,
            lastInput: 100, lastCached: 0, lastOutput: 20
        )
        let originalData = Data((metadata + "\n" + first + "\n").utf8)
        let replacementData = Data((metadata + "\n" + replacement + "\n").utf8)
        XCTAssertEqual(originalData.count, replacementData.count)
        try originalData.write(to: source)
        let initialIdentity = try XCTUnwrap(CodexSourceDiscovery().discover(in: [sessions]).first?.identity)
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        let initial = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(initial.snapshot.allTime.inputTokens, 200)

        try await Task.sleep(for: .milliseconds(10))
        let handle = try FileHandle(forWritingTo: source)
        try handle.truncate(atOffset: 0)
        try handle.write(contentsOf: replacementData)
        try handle.close()
        let replacementIdentity = try XCTUnwrap(CodexSourceDiscovery().discover(in: [sessions]).first?.identity)
        XCTAssertEqual(replacementIdentity, initialIdentity)

        let updated = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(updated.snapshot.allTime, TokenUsage(inputTokens: 300, cachedInputTokens: 0, outputTokens: 60))
        let updatedEventCount = try await database.eventCount()
        XCTAssertEqual(updatedEventCount, 2)
    }

    func testOrdinalRestartCreatesANewSemanticEvent() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let source = sessions.appendingPathComponent("rollout-2026-08-27T00-00-00-66666666-6666-6666-6666-666666666666.jsonl")
        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [sessions])
        let metadata = #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"66666666-6666-6666-6666-666666666666"}}"#
        let first = tokenLine(input: 100, cached: 60, output: 20, lastInput: 100, lastCached: 60, lastOutput: 20, ordinal: 1)
        let restarted = tokenLine(input: 50, cached: 30, output: 10, lastInput: 50, lastCached: 30, lastOutput: 10, ordinal: 1)
            .replacingOccurrences(of: "00:01:00Z", with: "00:02:00Z")
        try Data((metadata + "\n" + first + "\n" + restarted + "\n").utf8).write(to: source)
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")

        let result = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(result.snapshot.allTime, TokenUsage(inputTokens: 150, cachedInputTokens: 90, outputTokens: 30))
        XCTAssertEqual(result.snapshot.quality, .partial)
        let eventCount = try await database.eventCount()
        XCTAssertEqual(eventCount, 2)
    }

    func testVersion2MigrationDoesNotDuplicateAnArchivedSourceReplay() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        let archivedSessions = root.appendingPathComponent("archived_sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: archivedSessions, withIntermediateDirectories: true)

        let sessionID = "77777777-7777-7777-7777-777777777777"
        let original = sessions.appendingPathComponent(
            "rollout-2026-08-27T00-00-00-\(sessionID).jsonl"
        )
        let archived = archivedSessions.appendingPathComponent(original.lastPathComponent)
        let metadata =
            #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"77777777-7777-7777-7777-777777777777"}}"#
        let observation = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:01:00Z",
            input: 100,
            cached: 60,
            output: 20,
            lastInput: 100,
            lastCached: 60,
            lastOutput: 20
        )
        try Data((metadata + "\n" + observation + "\n").utf8).write(to: original)

        let databaseURL = root.appendingPathComponent("usage.sqlite")
        let eventDate = try Date.ISO8601FormatStyle().parse("2026-08-27T00:01:00Z")
        do {
            let legacyDatabase = try SQLiteDatabase(url: databaseURL)
            let checkpoint = SourceCheckpoint(
                sourcePath: original.path,
                fileIdentity: "1:2",
                generation: 0,
                committedOffset: Int64(try Data(contentsOf: original).count),
                sessionID: sessionID,
                inheritsHistory: false,
                model: "legacy-model",
                projectPath: "/legacy/project"
            )
            let usage = TokenUsage(inputTokens: 100, cachedInputTokens: 60, outputTokens: 20)
            let legacyEvent = UsageEvent(
                eventKey: "legacy-line-hash-event-key",
                occurredAt: eventDate,
                sessionID: sessionID,
                model: "legacy-model",
                projectPath: "/legacy/project",
                usage: usage,
                sourcePath: original.path,
                sourcePosition: 0
            )
            try await legacyDatabase.commit(
                events: [legacyEvent],
                checkpoint: checkpoint,
                normalizationState: UsageNormalizationState(
                    cumulativeHighWaterMark: usage,
                    lastObservedAt: eventDate,
                    quality: .exact
                )
            )
            try await legacyDatabase.prepareVersion2FixtureForTesting()
        }

        try FileManager.default.moveItem(at: original, to: archived)
        let migratedDatabase = try SQLiteDatabase(url: databaseURL)
        let collector = CodexUsageCollector(database: migratedDatabase, roots: [archivedSessions])
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")

        let replayed = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(
            replayed.snapshot.allTime,
            TokenUsage(inputTokens: 100, cachedInputTokens: 60, outputTokens: 20)
        )
        let replayedEventCount = try await migratedDatabase.eventCount()
        XCTAssertEqual(replayedEventCount, 1)
    }

    func testMigratedCheckpointBackfillsFingerprintAndDetectsLargerSameInodeRewrite() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let sessionID = "88888888-8888-8888-8888-888888888888"
        let source = sessions.appendingPathComponent(
            "rollout-2026-08-27T00-00-00-\(sessionID).jsonl"
        )
        let metadata =
            #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"88888888-8888-8888-8888-888888888888"}}"#
        let first = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:01:00Z",
            input: 200,
            cached: 100,
            output: 40,
            lastInput: 200,
            lastCached: 100,
            lastOutput: 40
        )
        let originalData = Data((metadata + "\n" + first + "\n").utf8)
        try originalData.write(to: source)
        let originalIdentity = try XCTUnwrap(
            CodexSourceDiscovery().discover(in: [sessions]).first?.identity
        )
        let databaseURL = root.appendingPathComponent("usage.sqlite")
        let firstDate = try Date.ISO8601FormatStyle().parse("2026-08-27T00:01:00Z")

        do {
            let legacyDatabase = try SQLiteDatabase(url: databaseURL)
            let usage = TokenUsage(inputTokens: 200, cachedInputTokens: 100, outputTokens: 40)
            let checkpoint = SourceCheckpoint(
                sourcePath: source.path,
                fileIdentity: originalIdentity,
                generation: 0,
                committedOffset: Int64(originalData.count),
                sessionID: sessionID,
                inheritsHistory: false,
                model: nil,
                projectPath: nil
            )
            let event = UsageEvent(
                eventKey: "legacy-event",
                occurredAt: firstDate,
                sessionID: sessionID,
                model: nil,
                projectPath: nil,
                usage: usage,
                sourcePath: source.path,
                sourcePosition: 0
            )
            try await legacyDatabase.commit(
                events: [event],
                checkpoint: checkpoint,
                normalizationState: UsageNormalizationState(
                    cumulativeHighWaterMark: usage,
                    lastObservedAt: firstDate,
                    quality: .exact
                )
            )
            try await legacyDatabase.prepareVersion2FixtureForTesting()
        }

        let migratedDatabase = try SQLiteDatabase(url: databaseURL)
        let collector = CodexUsageCollector(database: migratedDatabase, roots: [sessions])
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        let baseline = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(baseline.snapshot.allTime.inputTokens, 200)
        let savedCheckpoint = try await migratedDatabase.checkpoint(for: storageIdentifier(source.path))
        let backfilledCheckpoint = try XCTUnwrap(savedCheckpoint)
        XCTAssertEqual(backfilledCheckpoint.observedSize, Int64(originalData.count))
        XCTAssertFalse(backfilledCheckpoint.contentFingerprint.isEmpty)

        let replacement = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:02:00Z",
            input: 100,
            cached: 50,
            output: 20,
            lastInput: 100,
            lastCached: 50,
            lastOutput: 20
        )
        let replacementData = Data(
            (metadata + "\n" + replacement + String(repeating: " ", count: 512) + "\n").utf8
        )
        XCTAssertGreaterThan(replacementData.count, originalData.count)
        let handle = try FileHandle(forWritingTo: source)
        try handle.truncate(atOffset: 0)
        try handle.write(contentsOf: replacementData)
        try handle.close()
        let replacementIdentity = try XCTUnwrap(
            CodexSourceDiscovery().discover(in: [sessions]).first?.identity
        )
        XCTAssertEqual(replacementIdentity, originalIdentity)

        let updated = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(
            updated.snapshot.allTime,
            TokenUsage(inputTokens: 300, cachedInputTokens: 150, outputTokens: 60)
        )
        let updatedEventCount = try await migratedDatabase.eventCount()
        XCTAssertEqual(updatedEventCount, 2)
    }

    func testGrowingSameInodeRewriteDetectsAnUnsampledMiddleChange() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let sessionID = "99999999-9999-9999-9999-999999999999"
        let source = sessions.appendingPathComponent(
            "rollout-2026-08-27T00-00-00-\(sessionID).jsonl"
        )
        let metadata =
            #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"99999999-9999-9999-9999-999999999999"}}"#
        let prefix = String(repeating: "p", count: 90_000) + "\n"
        let suffix = String(repeating: "s", count: 90_000) + "\n"
        let first = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:01:00Z",
            input: 200,
            cached: 100,
            output: 40,
            lastInput: 200,
            lastCached: 100,
            lastOutput: 40
        )
        let replacement = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:02:00Z",
            input: 100,
            cached: 50,
            output: 20,
            lastInput: 100,
            lastCached: 50,
            lastOutput: 20
        )
        let originalData = Data((metadata + "\n" + prefix + first + "\n" + suffix).utf8)
        let replacementData = Data(
            (metadata + "\n" + prefix + replacement + "\n" + suffix
                + String(repeating: "a", count: 128) + "\n").utf8
        )
        try originalData.write(to: source)
        let originalIdentity = try XCTUnwrap(
            CodexSourceDiscovery().discover(in: [sessions]).first?.identity
        )
        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [sessions])
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")

        let initial = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(initial.snapshot.allTime.inputTokens, 200)

        let handle = try FileHandle(forWritingTo: source)
        try handle.truncate(atOffset: 0)
        try handle.write(contentsOf: replacementData)
        try handle.close()
        XCTAssertGreaterThan(replacementData.count, originalData.count)
        let replacementIdentity = try XCTUnwrap(
            CodexSourceDiscovery().discover(in: [sessions]).first?.identity
        )
        XCTAssertEqual(replacementIdentity, originalIdentity)

        let updated = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertEqual(
            updated.snapshot.allTime,
            TokenUsage(inputTokens: 300, cachedInputTokens: 150, outputTokens: 60)
        )
    }

    func testByteBudgetCommitsAndResumesUntilTheSourceIsComplete() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let sessionID = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        let source = sessions.appendingPathComponent(
            "rollout-2026-08-27T00-00-00-\(sessionID).jsonl"
        )
        let metadata =
            #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}}"#
        let event = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:01:00Z",
            input: 100,
            cached: 50,
            output: 20,
            lastInput: 100,
            lastCached: 50,
            lastOutput: 20
        )
        let fillerLine = String(repeating: "x", count: 1_000) + "\n"
        var sourceData = Data((metadata + "\n" + event + "\n").utf8)
        for _ in 0..<1_500 {
            sourceData.append(contentsOf: fillerLine.utf8)
        }
        try sourceData.write(to: source)

        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(
            database: database,
            roots: [sessions],
            maximumBytesPerRefresh: Int64(CodexJSONLParser.maximumLineBytes + 1),
            maximumRefreshDuration: .seconds(30)
        )
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")

        var result = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertTrue(result.hasMoreWork)
        XCTAssertEqual(result.snapshot.allTime.inputTokens, 100)
        let savedFirstCheckpoint = try await database.checkpoint(for: storageIdentifier(source.path))
        var firstCheckpoint = try XCTUnwrap(savedFirstCheckpoint)
        XCTAssertTrue(firstCheckpoint.hasPendingImport)

        // Simulate interruption immediately after an intermediate batch commit, before
        // the final pass can persist hasPendingImport. Offset, not that hint, must govern resumption.
        firstCheckpoint.hasPendingImport = false
        _ = try await database.commit(
            events: [],
            checkpoint: firstCheckpoint,
            normalizationState: nil
        )
        result = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        XCTAssertGreaterThan(result.processedBytes, 0)

        var continuationCount = 0
        while result.hasMoreWork {
            continuationCount += 1
            XCTAssertLessThan(continuationCount, 10)
            result = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)
        }
        XCTAssertEqual(result.snapshot.allTime, TokenUsage(inputTokens: 100, cachedInputTokens: 50, outputTokens: 20))
        let savedCheckpoint = try await database.checkpoint(for: storageIdentifier(source.path))
        XCTAssertEqual(savedCheckpoint?.committedOffset, Int64(sourceData.count))
        XCTAssertEqual(savedCheckpoint?.hasPendingImport, false)
        let eventCount = try await database.eventCount()
        XCTAssertEqual(eventCount, 1)
    }

    func testInheritedParentReplayIsSeededButNotCounted() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let childID = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
        let source = sessions.appendingPathComponent("rollout-2026-08-27T00-00-00-\(childID).jsonl")
        let lines = [
            #"{"timestamp":"2026-08-27T00:00:10.000Z","type":"session_meta","payload":{"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","forked_from_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","parent_thread_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}}"#,
            #"{"timestamp":"2026-08-27T00:00:10.000Z","type":"session_meta","payload":{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}}"#,
            #"{"timestamp":"2026-08-27T00:00:10.001Z","type":"event_msg","payload":{"type":"task_started","started_at":1}}"#,
            tokenLineWithoutOrdinal(
                timestamp: "2026-08-27T00:00:10.002Z",
                input: 5_000, cached: 4_000, output: 500,
                lastInput: 5_000, lastCached: 4_000, lastOutput: 500
            ),
            #"{"timestamp":"2026-08-27T00:00:10.100Z","type":"event_msg","payload":{"type":"task_started","started_at":4000000000}}"#,
            tokenLineWithoutOrdinal(
                timestamp: "2026-08-27T00:00:20Z",
                input: 5_100, cached: 4_080, output: 520,
                lastInput: 100, lastCached: 80, lastOutput: 20
            ),
            tokenLineWithoutOrdinal(
                timestamp: "2026-08-27T00:00:30Z",
                input: 5_150, cached: 4_120, output: 530,
                lastInput: 50, lastCached: 40, lastOutput: 10
            )
        ]
        try Data((lines.joined(separator: "\n") + "\n").utf8).write(to: source)

        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [sessions])
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        let result = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)

        XCTAssertEqual(
            result.snapshot.allTime,
            TokenUsage(inputTokens: 150, cachedInputTokens: 120, outputTokens: 30)
        )
        let eventCount = try await database.eventCount()
        XCTAssertEqual(eventCount, 2)
        let checkpoint = try await database.checkpoint(for: storageIdentifier(source.path))
        XCTAssertEqual(checkpoint?.historyReplayComplete, true)
    }

    func testInheritedReplayWithoutCopiedSessionMetadataIsNotCounted() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let childID = "abababab-abab-abab-abab-abababababab"
        let source = sessions.appendingPathComponent("rollout-2026-08-27T00-00-10-\(childID).jsonl")
        let lines = [
            #"{"timestamp":"2026-08-27T00:00:10Z","type":"session_meta","payload":{"id":"abababab-abab-abab-abab-abababababab","parent_thread_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}}"#,
            tokenLineWithoutOrdinal(
                timestamp: "2026-08-27T00:00:10.100Z",
                input: 5_000, cached: 4_000, output: 500,
                lastInput: 5_000, lastCached: 4_000, lastOutput: 500
            ),
            #"{"timestamp":"2026-08-27T00:00:11Z","type":"event_msg","payload":{"type":"task_started","started_at":4000000000}}"#,
            tokenLineWithoutOrdinal(
                timestamp: "2026-08-27T00:00:20Z",
                input: 5_100, cached: 4_080, output: 520,
                lastInput: 100, lastCached: 80, lastOutput: 20
            )
        ]
        try Data((lines.joined(separator: "\n") + "\n").utf8).write(to: source)

        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [sessions])
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        let result = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)

        XCTAssertEqual(
            result.snapshot.allTime,
            TokenUsage(inputTokens: 100, cachedInputTokens: 80, outputTokens: 20)
        )
        let eventCount = try await database.eventCount()
        XCTAssertEqual(eventCount, 1)
        let checkpoint = try await database.checkpoint(for: storageIdentifier(source.path))
        XCTAssertNotNil(checkpoint?.sessionStartedAt)
        XCTAssertEqual(checkpoint?.historyReplayComplete, true)
    }

    func testHistoryStartOrdinalExcludesCopiedPrefix() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let childID = "dddddddd-dddd-dddd-dddd-dddddddddddd"
        let source = sessions.appendingPathComponent("rollout-2026-08-27T00-00-00-\(childID).jsonl")
        let lines = [
            #"{"timestamp":"2026-08-27T00:00:10Z","type":"session_meta","ordinal":0,"payload":{"id":"dddddddd-dddd-dddd-dddd-dddddddddddd","parent_thread_id":"cccccccc-cccc-cccc-cccc-cccccccccccc","subagent_history_start_ordinal":4}}"#,
            #"{"timestamp":"2026-08-27T00:00:10Z","type":"session_meta","ordinal":1,"payload":{"id":"cccccccc-cccc-cccc-cccc-cccccccccccc"}}"#,
            #"{"timestamp":"2026-08-27T00:00:10Z","type":"event_msg","ordinal":3,"payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":5000,"cached_input_tokens":4000,"output_tokens":500},"last_token_usage":{"input_tokens":5000,"cached_input_tokens":4000,"output_tokens":500}}}}"#,
            #"{"timestamp":"2026-08-27T00:00:11Z","type":"event_msg","ordinal":5,"payload":{"type":"task_started","started_at":4000000000}}"#,
            #"{"timestamp":"2026-08-27T00:00:20Z","type":"event_msg","ordinal":6,"payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":5100,"cached_input_tokens":4080,"output_tokens":520},"last_token_usage":{"input_tokens":100,"cached_input_tokens":80,"output_tokens":20}}}}"#
        ]
        try Data((lines.joined(separator: "\n") + "\n").utf8).write(to: source)

        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [sessions])
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        let result = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)

        XCTAssertEqual(
            result.snapshot.allTime,
            TokenUsage(inputTokens: 100, cachedInputTokens: 80, outputTokens: 20)
        )
        let eventCount = try await database.eventCount()
        XCTAssertEqual(eventCount, 1)
    }

    func testUnreadableSourceDoesNotAbortOtherSourcesAndMarksSnapshotPartial() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)

        let unreadable = sessions.appendingPathComponent("000-unreadable.jsonl")
        try Data("unreadable\n".utf8).write(to: unreadable)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o000],
            ofItemAtPath: unreadable.path
        )
        defer {
            try? FileManager.default.setAttributes(
                [.posixPermissions: 0o600],
                ofItemAtPath: unreadable.path
            )
        }

        let sessionID = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"
        let valid = sessions.appendingPathComponent(
            "zzz-rollout-2026-08-27T00-00-00-\(sessionID).jsonl"
        )
        let metadata =
            #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"}}"#
        let event = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:01:00Z",
            input: 100,
            cached: 50,
            output: 20,
            lastInput: 100,
            lastCached: 50,
            lastOutput: 20
        )
        try Data((metadata + "\n" + event + "\n").utf8).write(to: valid)

        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: database, roots: [sessions])
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        let result = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)

        XCTAssertEqual(result.sourceCount, 2)
        XCTAssertEqual(result.snapshot.quality, .partial)
        XCTAssertEqual(
            result.snapshot.allTime,
            TokenUsage(inputTokens: 100, cachedInputTokens: 50, outputTokens: 20)
        )
        let eventCount = try await database.eventCount()
        XCTAssertEqual(eventCount, 1)
    }

    func testCheckpointFingerprintsReadEachFreshSourceByteOnce() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)

        let sessionID = "ffffffff-ffff-ffff-ffff-ffffffffffff"
        let source = sessions.appendingPathComponent(
            "rollout-2026-08-27T00-00-00-\(sessionID).jsonl"
        )
        let metadata =
            #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"ffffffff-ffff-ffff-ffff-ffffffffffff"}}"#
        let event = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:01:00Z",
            input: 100,
            cached: 50,
            output: 20,
            lastInput: 100,
            lastCached: 50,
            lastOutput: 20
        )
        let filler = String(repeating: "x", count: 96) + "\n"
        var sourceData = Data((metadata + "\n" + event + "\n").utf8)
        for _ in 0..<3_500 {
            sourceData.append(contentsOf: filler.utf8)
        }
        try sourceData.write(to: source)

        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(
            database: database,
            roots: [sessions],
            maximumRefreshDuration: .seconds(30)
        )
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        let result = try await collector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)

        XCTAssertFalse(result.hasMoreWork)
        XCTAssertEqual(result.processedBytes, Int64(sourceData.count))
        XCTAssertEqual(result.fingerprintBytesRead, Int64(sourceData.count))
        let checkpoint = try await database.checkpoint(for: storageIdentifier(source.path))
        XCTAssertTrue(checkpoint?.contentFingerprint.hasPrefix("v2:") == true)
    }

    func testLargeCommittedFingerprintVerificationResumesAndConverges() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)

        let sessionID = "12121212-1212-1212-1212-121212121212"
        let source = sessions.appendingPathComponent(
            "rollout-2026-08-27T00-00-00-\(sessionID).jsonl"
        )
        let metadata =
            #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"12121212-1212-1212-1212-121212121212"}}"#
        let first = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:01:00Z",
            input: 100,
            cached: 50,
            output: 20,
            lastInput: 100,
            lastCached: 50,
            lastOutput: 20
        )
        let filler = String(repeating: "x", count: 1_000) + "\n"
        var sourceData = Data((metadata + "\n" + first + "\n").utf8)
        for _ in 0..<3_500 {
            sourceData.append(contentsOf: filler.utf8)
        }
        try sourceData.write(to: source)

        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let initialCollector = CodexUsageCollector(
            database: database,
            roots: [sessions],
            maximumRefreshDuration: .seconds(30)
        )
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        let initial = try await initialCollector.refresh(
            now: now,
            calendar: utcCalendar,
            weekStart: .monday
        )
        XCTAssertFalse(initial.hasMoreWork)

        let second = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:02:00Z",
            input: 150,
            cached: 75,
            output: 30,
            lastInput: 50,
            lastCached: 25,
            lastOutput: 10
        )
        let handle = try FileHandle(forWritingTo: source)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data((second + "\n").utf8))
        try handle.close()

        let verificationBudget = Int64((CodexJSONLParser.maximumLineBytes + 1) * 2)
        let resumedCollector = CodexUsageCollector(
            database: database,
            roots: [sessions],
            maximumBytesPerRefresh: verificationBudget,
            maximumRefreshDuration: .seconds(30)
        )

        let firstContinuation = try await resumedCollector.refresh(
            now: now,
            calendar: utcCalendar,
            weekStart: .monday
        )
        XCTAssertTrue(firstContinuation.hasMoreWork)
        XCTAssertEqual(firstContinuation.processedBytes, 0)
        XCTAssertEqual(firstContinuation.fingerprintBytesRead, verificationBudget)

        let secondContinuation = try await resumedCollector.refresh(
            now: now,
            calendar: utcCalendar,
            weekStart: .monday
        )
        XCTAssertTrue(secondContinuation.hasMoreWork)
        XCTAssertEqual(secondContinuation.processedBytes, 0)
        XCTAssertGreaterThan(secondContinuation.fingerprintBytesRead, 0)
        XCTAssertLessThan(secondContinuation.fingerprintBytesRead, verificationBudget)

        let completed = try await resumedCollector.refresh(
            now: now,
            calendar: utcCalendar,
            weekStart: .monday
        )
        XCTAssertFalse(completed.hasMoreWork)
        XCTAssertGreaterThan(completed.processedBytes, 0)
        XCTAssertEqual(
            completed.snapshot.allTime,
            TokenUsage(inputTokens: 150, cachedInputTokens: 75, outputTokens: 30)
        )
    }

    func testFingerprintContinuationRejectsSameSizeRewriteWithRestoredModificationTime() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)

        let sessionID = "34343434-3434-3434-3434-343434343434"
        let source = sessions.appendingPathComponent(
            "rollout-2026-08-27T00-00-00-\(sessionID).jsonl"
        )
        let metadata =
            #"{"timestamp":"2026-08-27T00:00:00Z","type":"session_meta","payload":{"id":"34343434-3434-3434-3434-343434343434"}}"#
        let first = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:01:00Z",
            input: 100,
            cached: 50,
            output: 20,
            lastInput: 100,
            lastCached: 50,
            lastOutput: 20
        )
        let filler = String(repeating: "x", count: 1_000) + "\n"
        var sourceData = Data((metadata + "\n" + first + "\n").utf8)
        for _ in 0..<3_500 {
            sourceData.append(contentsOf: filler.utf8)
        }
        try sourceData.write(to: source)

        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let initialCollector = CodexUsageCollector(
            database: database,
            roots: [sessions],
            maximumRefreshDuration: .seconds(30)
        )
        let now = try Date.ISO8601FormatStyle().parse("2026-08-27T01:00:00Z")
        _ = try await initialCollector.refresh(now: now, calendar: utcCalendar, weekStart: .monday)

        let second = tokenLineWithoutOrdinal(
            timestamp: "2026-08-27T00:02:00Z",
            input: 150,
            cached: 75,
            output: 30,
            lastInput: 50,
            lastCached: 25,
            lastOutput: 10
        )
        let appendHandle = try FileHandle(forWritingTo: source)
        try appendHandle.seekToEnd()
        try appendHandle.write(contentsOf: Data((second + "\n").utf8))
        try appendHandle.close()

        let verificationBudget = Int64((CodexJSONLParser.maximumLineBytes + 1) * 2)
        let resumedCollector = CodexUsageCollector(
            database: database,
            roots: [sessions],
            maximumBytesPerRefresh: verificationBudget,
            maximumRefreshDuration: .seconds(30)
        )
        let firstContinuation = try await resumedCollector.refresh(
            now: now,
            calendar: utcCalendar,
            weekStart: .monday
        )
        XCTAssertEqual(firstContinuation.fingerprintBytesRead, verificationBudget)

        let originalModificationDate = try XCTUnwrap(
            FileManager.default.attributesOfItem(atPath: source.path)[.modificationDate] as? Date
        )
        try await Task.sleep(for: .milliseconds(20))
        let rewriteHandle = try FileHandle(forWritingTo: source)
        try rewriteHandle.seek(toOffset: UInt64(sourceData.count - 2))
        try rewriteHandle.write(contentsOf: Data("y".utf8))
        try rewriteHandle.close()
        try FileManager.default.setAttributes(
            [.modificationDate: originalModificationDate],
            ofItemAtPath: source.path
        )

        let afterRewrite = try await resumedCollector.refresh(
            now: now,
            calendar: utcCalendar,
            weekStart: .monday
        )
        XCTAssertTrue(afterRewrite.hasMoreWork)
        XCTAssertEqual(afterRewrite.processedBytes, 0)
        XCTAssertEqual(afterRewrite.fingerprintBytesRead, verificationBudget)
    }

    private var utcCalendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        return calendar
    }

    private func storageIdentifier(_ value: String) -> String {
        SHA256.hash(data: Data(value.utf8))
            .map { String(format: "%02x", $0) }
            .joined()
    }

    private func legacyEventKey(
        sessionID: String,
        occurredAt: Date,
        ordinal: Int64,
        usage: TokenUsage
    ) -> String {
        let identity = "\(usage.inputTokens),\(usage.cachedInputTokens),\(usage.outputTokens)"
        let material = [
            "event-v2",
            "session:\(sessionID)",
            "time:\(occurredAt.timeIntervalSinceReferenceDate.bitPattern)",
            "ordinal:\(ordinal)",
            "last:\(identity)",
            "cumulative:\(identity)"
        ].joined(separator: "|")
        return storageIdentifier(material)
    }

    private func tokenLine(
        input: Int64,
        cached: Int64,
        output: Int64,
        lastInput: Int64,
        lastCached: Int64,
        lastOutput: Int64,
        ordinal: Int64
    ) -> String {
        """
        {"timestamp":"2026-08-27T00:01:00Z","type":"event_msg","ordinal":\(ordinal),"payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":\(input),"cached_input_tokens":\(cached),"output_tokens":\(output)},"last_token_usage":{"input_tokens":\(lastInput),"cached_input_tokens":\(lastCached),"output_tokens":\(lastOutput)}}}}
        """
    }

    private func tokenLineWithoutOrdinal(
        timestamp: String,
        input: Int64,
        cached: Int64,
        output: Int64,
        lastInput: Int64,
        lastCached: Int64,
        lastOutput: Int64
    ) -> String {
        """
        {"timestamp":"\(timestamp)","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":\(input),"cached_input_tokens":\(cached),"output_tokens":\(output)},"last_token_usage":{"input_tokens":\(lastInput),"cached_input_tokens":\(lastCached),"output_tokens":\(lastOutput)}}}}
        """
    }
}
