import Foundation
import XCTest
@testable import CodeRim

final class UsagePeriodContractTests: XCTestCase {
    func testSharedPeriodsAndPersistedRepairEvidence() async throws {
        let fixtureURL = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Fixtures/usage-periods-v1.json")
        let fixtures = try JSONDecoder().decode([Fixture].self, from: Data(contentsOf: fixtureURL))
        XCTAssertFalse(fixtures.isEmpty)
        for fixture in fixtures {
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = try XCTUnwrap(TimeZone(identifier: fixture.timeZone))
            let now = Date(timeIntervalSince1970: fixture.now)
            let weekStart: WeekStart = fixture.weekStart == "monday" ? .monday : .sunday
            let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: root) }
            let url = root.appendingPathComponent("usage.sqlite")
            let events = fixture.events.enumerated().map { index, row in
                UsageEvent(eventKey: "\(index)", occurredAt: Date(timeIntervalSince1970: row.at),
                    sessionID: nil, model: nil, projectPath: nil,
                    usage: TokenUsage(inputTokens: row.tokens, cachedInputTokens: 0, cacheWriteInputTokens: 0, outputTokens: 0),
                    sourcePath: "fixture", sourcePosition: Int64(index))
            }
            let database = try SQLiteDatabase(url: url)
            let evidence = fixture.events.enumerated().compactMap { index, row -> PartialEvidence? in
                row.repaired ? PartialEvidence(source: "fixture", position: Int64(index),
                    observedAt: Date(timeIntervalSince1970: row.at)) : nil
            }
            try await database.commit(events: events, checkpoint: .fresh(sourcePath: "fixture", fileIdentity: "1:1"),
                normalizationState: nil, partialEvidence: evidence)
            // Reopen with no source files: quality must come from the durable evidence.
            let reopened = try SQLiteDatabase(url: url)
            let saved = try await reopened.usageSnapshot(now: now, calendar: calendar, weekStart: weekStart)
            let calculated = AggregationService().snapshot(from: events, now: now, calendar: calendar, weekStart: weekStart)
            for snapshot in [calculated, saved] {
                XCTAssertEqual([snapshot.today.totalTokens, snapshot.week.totalTokens,
                    snapshot.month.totalTokens, snapshot.allTime.totalTokens], fixture.expected.totals, fixture.name)
                XCTAssertEqual(snapshot.updatedAt?.timeIntervalSince1970, fixture.expected.updatedAt, fixture.name)
            }
            XCTAssertEqual(saved.quality, fixture.expected.quality, fixture.name)
            XCTAssertEqual(saved.historyIncomplete, fixture.expected.hasRepairEvidence, fixture.name)
        }
    }

    private struct Fixture: Decodable {
        let name: String
        let now: TimeInterval
        let timeZone: String
        let weekStart: String
        let events: [Event]
        let expected: Expected
    }
    private struct Event: Decodable {
        let at: TimeInterval
        let tokens: Int64
        let repaired: Bool
    }
    private struct Expected: Decodable {
        let totals: [Int64]
        let quality: DataQuality
        let updatedAt: TimeInterval?
        let hasRepairEvidence: Bool
    }
}
