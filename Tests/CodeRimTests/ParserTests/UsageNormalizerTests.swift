import Foundation
import XCTest
@testable import CodeRim

final class UsageNormalizerTests: XCTestCase {
    private let normalizer = UsageNormalizer()
    private let timestamp = Date(timeIntervalSince1970: 1_800_000_000)

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
