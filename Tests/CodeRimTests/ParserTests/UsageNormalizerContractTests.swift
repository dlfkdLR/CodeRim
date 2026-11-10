import Foundation
import XCTest
@testable import CodeRim

final class UsageNormalizerContractTests: XCTestCase {
    func testSharedNormalizationContract() throws {
        let path = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Fixtures/usage-normalization-v1.json")
        let fixtures = try JSONDecoder().decode([Fixture].self, from: Data(contentsOf: path))
        XCTAssertFalse(fixtures.isEmpty)
        XCTAssertEqual(Set(fixtures.map(\.name)).count, fixtures.count)
        let normalizer = UsageNormalizer()
        for fixture in fixtures {
            XCTAssertFalse(fixture.steps.isEmpty, fixture.name)
            var state = UsageNormalizationState.empty
            for (index, step) in fixture.steps.enumerated() {
                let context = "\(fixture.name), step \(index + 1)"
                let result = normalizer.normalize(
                    CodexTokenObservation(occurredAt: Date(timeIntervalSince1970: step.occurredAt),
                                          ordinal: nil, lastUsage: step.lastUsage,
                                          cumulativeUsage: step.cumulativeUsage),
                    state: state)
                XCTAssertEqual(result.delta, step.expected.delta, context)
                XCTAssertEqual(result.state, UsageNormalizationState(
                    cumulativeHighWaterMark: step.expected.cumulativeHighWaterMark,
                    lastObservedAt: step.expected.lastObservedAt.map { Date(timeIntervalSince1970: $0) },
                    quality: step.expected.quality), context)
                XCTAssertEqual(result.diagnostic, step.expected.diagnostic, context)
                state = result.state
            }
        }
    }

    private struct Fixture: Decodable {
        let name: String
        let steps: [Step]
    }

    private struct Step: Decodable {
        let occurredAt: TimeInterval
        let lastUsage: TokenUsage?
        let cumulativeUsage: TokenUsage?
        let expected: Expected
    }

    private struct Expected: Decodable {
        let delta: TokenUsage?
        let cumulativeHighWaterMark: TokenUsage?
        let lastObservedAt: TimeInterval?
        let quality: DataQuality
        let diagnostic: String?
    }
}
