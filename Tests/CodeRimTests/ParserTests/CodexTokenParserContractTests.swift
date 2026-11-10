import Foundation
import XCTest
@testable import CodeRim

final class CodexTokenParserContractTests: XCTestCase {
    func testSharedTokenParserContract() throws {
        let path = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Fixtures/codex-token-parser-v1.json")
        let fixtures = try JSONDecoder().decode([Fixture].self, from: Data(contentsOf: path))
        XCTAssertFalse(fixtures.isEmpty)
        XCTAssertEqual(Set(fixtures.map(\.name)).count, fixtures.count)
        let parser = CodexJSONLParser()
        for fixture in fixtures {
            let expected = fixture.expected
            switch parser.parse(Data(fixture.line.utf8)) {
            case let .token(token):
                XCTAssertEqual(expected.kind, "token", fixture.name)
                let observation = try XCTUnwrap(expected.token, fixture.name)
                XCTAssertEqual(token, CodexTokenObservation(
                    occurredAt: Date(timeIntervalSince1970: observation.occurredAt),
                    ordinal: observation.ordinal, lastUsage: observation.lastUsage,
                    cumulativeUsage: observation.cumulativeUsage), fixture.name)
            case let .malformed(diagnostic):
                XCTAssertEqual(expected.kind, "malformed", fixture.name)
                XCTAssertEqual(diagnostic, expected.diagnostic, fixture.name)
            case .ignored:
                XCTAssertEqual(expected.kind, "ignored", fixture.name)
            default:
                XCTFail("Unexpected non-token metadata: \(fixture.name)")
            }
        }
    }

    private struct Fixture: Decodable {
        let name: String
        let line: String
        let expected: Expected
    }

    private struct Expected: Decodable {
        let kind: String
        let diagnostic: String?
        let token: Observation?
    }

    private struct Observation: Decodable {
        let occurredAt: TimeInterval
        let ordinal: Int64?
        let lastUsage: TokenUsage?
        let cumulativeUsage: TokenUsage?
    }
}
