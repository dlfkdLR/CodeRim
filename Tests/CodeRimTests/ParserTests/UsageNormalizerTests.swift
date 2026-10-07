import Foundation
import XCTest
@testable import CodeRim

final class UsageNormalizerTests: XCTestCase {
    private let normalizer = UsageNormalizer()
    private let timestamp = Date(timeIntervalSince1970: 1_800_000_000)

    func testOptionalCacheWriteLossDoesNotDropOrRepeatUsage() {
        let known = TokenUsage(inputTokens: 100, cachedInputTokens: 0, cacheWriteInputTokens: 40, outputTokens: 20)
        let missing = TokenUsage(inputTokens: 100, cachedInputTokens: 0, outputTokens: 20)
        let first = normalizer.normalize(observation(cumulative: known), metadata: nil, state: .empty)
        let repeated = normalizer.normalize(observation(cumulative: missing), metadata: nil, state: first.state)
        XCTAssertNil(repeated.delta)
        var state = first.state
        var total = first.delta!.totalTokens
        for index in 2...3 {
            let cumulative = TokenUsage(inputTokens: Int64(index * 100), cachedInputTokens: 0, outputTokens: Int64(index * 20))
            let result = normalizer.normalize(CodexTokenObservation(occurredAt: timestamp.addingTimeInterval(Double(index)),
                ordinal: Int64(index), lastUsage: missing, cumulativeUsage: cumulative), metadata: nil, state: state)
            total += result.delta?.totalTokens ?? 0
            XCTAssertNil(result.delta?.cacheWriteInputTokens)
            XCTAssertEqual(result.state.quality, .exact)
            state = result.state
        }
        XCTAssertEqual(total, 360)
    }

    func testUsesCumulativeIncreaseAndIgnoresRepeatedSnapshot() {
        let first = normalizer.normalize(
            observation(cumulative: usage(100, cached: 60, output: 20)),
            metadata: rootMetadata,
            state: .empty
        )
        XCTAssertEqual(first.delta, usage(100, cached: 60, output: 20))

        let repeated = normalizer.normalize(
            observation(cumulative: usage(100, cached: 60, output: 20)),
            metadata: rootMetadata,
            state: first.state
        )
        XCTAssertNil(repeated.delta)

        let increased = normalizer.normalize(
            observation(cumulative: usage(130, cached: 80, output: 25)),
            metadata: rootMetadata,
            state: repeated.state
        )
        XCTAssertEqual(increased.delta, usage(30, cached: 20, output: 5))
    }

    func testForkedSessionCountsFreshCounterWhenLastMatchesCumulative() {
        let metadata = SessionMetadata(
            id: "child",
            model: nil,
            workingDirectory: nil,
            forkedFromID: "parent"
        )
        let result = normalizer.normalize(
            observation(cumulative: usage(5_000, cached: 4_000, output: 500)),
            metadata: metadata,
            state: .empty
        )

        XCTAssertEqual(result.delta, usage(5_000, cached: 4_000, output: 500))
        XCTAssertEqual(result.state.quality, .exact)
        XCTAssertNil(result.diagnostic)
    }

    func testForkedSessionSkipsAmbiguousInheritedBaseline() {
        let metadata = SessionMetadata(
            id: "child",
            model: nil,
            workingDirectory: nil,
            forkedFromID: "parent"
        )
        let result = normalizer.normalize(
            CodexTokenObservation(
                occurredAt: timestamp,
                ordinal: nil,
                lastUsage: usage(100, cached: 50, output: 20),
                cumulativeUsage: usage(5_000, cached: 4_000, output: 500)
            ),
            metadata: metadata,
            state: .empty
        )

        XCTAssertNil(result.delta)
        XCTAssertEqual(result.state.quality, .partial)
        XCTAssertEqual(result.diagnostic, "initial cumulative baseline is unresolved")
    }

    func testDecreaseDoesNotBecomeFreshUsage() {
        let prior = UsageNormalizationState(
            cumulativeHighWaterMark: usage(1_000, cached: 700, output: 100),
            quality: .exact
        )
        let result = normalizer.normalize(
            CodexTokenObservation(
                occurredAt: timestamp,
                ordinal: 2,
                lastUsage: usage(100, cached: 80, output: 10),
                cumulativeUsage: usage(500, cached: 300, output: 50)
            ),
            metadata: rootMetadata,
            state: prior
        )

        XCTAssertNil(result.delta)
        XCTAssertEqual(result.state.cumulativeHighWaterMark, prior.cumulativeHighWaterMark)
        XCTAssertEqual(result.state.quality, .partial)
    }

    func testValidatedCounterRestartCountsFreshSegmentAsPartial() {
        let prior = UsageNormalizationState(
            cumulativeHighWaterMark: usage(1_000, cached: 700, output: 100),
            quality: .exact
        )
        let fresh = usage(50, cached: 20, output: 10)
        let observation = CodexTokenObservation(
            occurredAt: timestamp,
            ordinal: 2,
            lastUsage: fresh,
            cumulativeUsage: fresh
        )
        let result = normalizer.normalize(observation, metadata: rootMetadata, state: prior)

        XCTAssertEqual(result.delta, fresh)
        XCTAssertEqual(result.state.cumulativeHighWaterMark, fresh)
        XCTAssertEqual(result.state.quality, .partial)
        XCTAssertEqual(result.diagnostic, "cumulative counter restarted")
    }

    func testOlderReplayCannotRegressCurrentSegment() {
        let latestDate = timestamp.addingTimeInterval(60)
        let prior = UsageNormalizationState(
            cumulativeHighWaterMark: usage(150, cached: 90, output: 30),
            lastObservedAt: latestDate,
            quality: .exact
        )
        let replay = CodexTokenObservation(
            occurredAt: timestamp,
            ordinal: nil,
            lastUsage: usage(100, cached: 60, output: 20),
            cumulativeUsage: usage(100, cached: 60, output: 20)
        )

        let result = normalizer.normalize(replay, metadata: rootMetadata, state: prior)

        XCTAssertNil(result.delta)
        XCTAssertEqual(result.state, prior)
        XCTAssertEqual(result.diagnostic, "out-of-order token snapshot ignored")
    }

    func testUnresolvedInitialBaselineIsNotCounted() {
        let cumulative = usage(500, cached: 300, output: 50)
        let observation = CodexTokenObservation(
            occurredAt: timestamp,
            ordinal: 1,
            lastUsage: usage(100, cached: 80, output: 10),
            cumulativeUsage: cumulative
        )
        let result = normalizer.normalize(observation, metadata: rootMetadata, state: .empty)
        XCTAssertNil(result.delta)
        XCTAssertEqual(result.state.quality, .partial)
        XCTAssertEqual(result.diagnostic, "initial cumulative baseline is unresolved")
    }

    func testLastUsageAloneIsNotAcceptedAsAnAuthoritativeDelta() {
        let observation = CodexTokenObservation(
            occurredAt: timestamp,
            ordinal: 1,
            lastUsage: usage(100, cached: 80, output: 20),
            cumulativeUsage: nil
        )
        let result = normalizer.normalize(observation, metadata: rootMetadata, state: .empty)
        XCTAssertNil(result.delta)
        XCTAssertEqual(result.state.quality, .partial)
    }

    private var rootMetadata: SessionMetadata {
        SessionMetadata(id: "root", model: nil, workingDirectory: nil)
    }

    /// 100/20/10 then 110/40/15: both snapshots are valid, their difference (10 input, 20 cached) is not.
    /// It must neither reach storage (whose CHECK rejects it) nor be dropped while the period says exact.
    func testInconsistentDeltaBetweenValidCountersIsClampedAndPartial() {
        let first = normalizer.normalize(observation(cumulative: usage(100, cached: 20, output: 10)), metadata: nil, state: .empty)
        let next = normalizer.normalize(
            CodexTokenObservation(occurredAt: timestamp.addingTimeInterval(1), ordinal: 2,
                                  lastUsage: usage(10, cached: 20, output: 5), cumulativeUsage: usage(110, cached: 40, output: 15)),
            metadata: nil, state: first.state)
        XCTAssertEqual(next.delta, usage(10, cached: 10, output: 5))
        XCTAssertTrue(next.delta?.isValid == true)
        XCTAssertEqual(next.state.quality, .partial)
        XCTAssertEqual(next.state.cumulativeHighWaterMark, usage(110, cached: 40, output: 15))
    }

    func testInconsistentRowIsStoredAsItsValidPartInsteadOfFailingTheBatch() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let database = try SQLiteDatabase(url: directory.appendingPathComponent("usage.sqlite"))
        let now = Date()
        let bad = usage(10, cached: 20, output: 5)
        XCTAssertFalse(bad.isValid)
        let event = UsageEvent(eventKey: "bad", occurredAt: now.addingTimeInterval(-60), sessionID: "s", model: nil,
                               projectPath: nil, usage: bad, sourcePath: "fixture", sourcePosition: 0)
        _ = try await database.commit(events: [event], checkpoint: .fresh(sourcePath: "fixture", fileIdentity: "1:2"),
                                      normalizationState: nil)
        let snapshot = try await database.usageSnapshot(now: now, calendar: .current, weekStart: .monday)
        XCTAssertEqual(snapshot.today.totalTokens, 15)
        XCTAssertEqual(snapshot.today.cachedInputTokens, 10)
    }

    /// A session read only partly months ago does not make today partial; it is reported as old history.
    func testCurrentPeriodQualityIgnoresAnOldPartialSession() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let database = try SQLiteDatabase(url: directory.appendingPathComponent("usage.sqlite"))
        let now = Date()
        func commit(_ key: String, at date: Date, quality: DataQuality) async throws {
            var checkpoint = SourceCheckpoint.fresh(sourcePath: key, fileIdentity: "1:2")
            checkpoint.sessionID = key
            let value = usage(100, cached: 0, output: 10)
            _ = try await database.commit(
                events: [UsageEvent(eventKey: key, occurredAt: date, sessionID: key, model: nil, projectPath: nil,
                                    usage: value, sourcePath: key, sourcePosition: 0)],
                checkpoint: checkpoint,
                normalizationState: UsageNormalizationState(cumulativeHighWaterMark: value, lastObservedAt: date, quality: quality))
        }
        try await commit("old", at: now.addingTimeInterval(-120 * 86_400), quality: .partial)
        try await commit("today", at: now.addingTimeInterval(-60), quality: .exact)
        var snapshot = try await database.usageSnapshot(now: now, calendar: .current, weekStart: .monday)
        XCTAssertEqual(snapshot.quality, .exact)
        XCTAssertTrue(snapshot.historyIncomplete)
        try await commit("recent", at: now.addingTimeInterval(-30), quality: .partial)
        snapshot = try await database.usageSnapshot(now: now.addingTimeInterval(1), calendar: .current, weekStart: .monday)
        XCTAssertEqual(snapshot.quality, .partial)
    }

    private func observation(cumulative: TokenUsage) -> CodexTokenObservation {
        CodexTokenObservation(
            occurredAt: timestamp,
            ordinal: 1,
            lastUsage: cumulative,
            cumulativeUsage: cumulative
        )
    }

    private func usage(_ input: Int64, cached: Int64, output: Int64) -> TokenUsage {
        TokenUsage(inputTokens: input, cachedInputTokens: cached, outputTokens: output)
    }
}
