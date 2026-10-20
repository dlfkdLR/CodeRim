import Foundation
import XCTest
@testable import CodeRim

final class RealtimeCollectionTests: XCTestCase {
    func testSmallLiveSessionUpdatesWhileLargePrefixIsStillBeingVerified() async throws {
        try await exerciseCollection(largeSourceCount: 1)
    }

    func testSeveralLargeSourcesDoNotStarveLiveUsageOrDuplicateTotals() async throws {
        try await exerciseCollection(largeSourceCount: 2)
    }

    func testGrowingMediumSourceReceivesAFullBudgetAfterAConstrainedPass() async throws {
        try await exerciseCollection(largeSourceCount: 2, mediumSecond: true)
    }

    private func exerciseCollection(largeSourceCount: Int, mediumSecond: Bool = false) async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions")
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let large = sessions.appendingPathComponent("a.jsonl")
        let now = Date()
        let time = now.addingTimeInterval(-1).formatted(.iso8601)
        func token(_ session: String, _ count: Int) -> Data {
            Data(("{\"type\":\"session_meta\",\"payload\":{\"id\":\"\(session)\"}}\n" +
                "{\"timestamp\":\"\(time)\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":\(count),\"cached_input_tokens\":0,\"output_tokens\":0},\"last_token_usage\":{\"input_tokens\":\(count),\"cached_input_tokens\":0,\"output_tokens\":0}}}}\n").utf8)
        }
        var data = token("large", 100)
        let padding = Data(("{\"type\":\"ignored\",\"padding\":\"" + String(repeating: "x", count: 60_000) + "\"}\n").utf8)
        for _ in 0..<(mediumSecond ? 750 : 180) { data.append(padding) }
        try data.write(to: large)
        let second = sessions.appendingPathComponent("b.jsonl")
        if largeSourceCount == 2 {
            var other = token("second", 100)
            for _ in 0..<(mediumSecond ? 90 : 180) { other.append(padding) }
            try other.write(to: second)
        }
        let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let initial = CodexUsageCollector(database: database, roots: [sessions], maximumRefreshDuration: .seconds(30))
        let initialResult = try await initial.refresh(now: now, weekStart: .monday)
        var imported = initialResult
        for _ in 0..<10 where imported.hasMoreWork {
            imported = try await initial.refresh(now: now, weekStart: .monday)
        }
        XCTAssertFalse(imported.hasMoreWork)
        XCTAssertEqual(imported.snapshot.today.totalTokens, Int64(100 * largeSourceCount))
        let handle = try FileHandle(forWritingTo: large)
        try handle.seekToEnd(); try handle.write(contentsOf: Data("{}\n".utf8)); try handle.close()
        if largeSourceCount == 2 {
            let other = try FileHandle(forWritingTo: second)
            try other.seekToEnd(); try other.write(contentsOf: Data("{}\n".utf8)); try other.close()
        }
        try token("live", 777).write(to: sessions.appendingPathComponent("z.jsonl"))
        let budget: Int64 = (mediumSecond ? 16 : 8) * 1_024 * 1_024
        let resumed = CodexUsageCollector(database: database, roots: [sessions], maximumBytesPerRefresh: budget, maximumRefreshDuration: .seconds(30))
        var first = try await resumed.refresh(now: now, weekStart: .monday)
        XCTAssertTrue(first.hasMoreWork, "Large verification must still be in progress")
        if mediumSecond {
            let writer = try FileHandle(forWritingTo: second)
            try writer.seekToEnd(); try writer.write(contentsOf: token("second", 200)); try writer.close()
        }
        if largeSourceCount == 2 {
            first = try await resumed.refresh(now: now, weekStart: .monday)
        }
        let expected = Int64(100 * largeSourceCount + 777 + (mediumSecond ? 100 : 0))
        XCTAssertEqual(first.snapshot.today.totalTokens, expected, "A small live task must publish within a bounded fair turn")
        XCTAssertLessThanOrEqual(first.processedBytes + first.fingerprintBytesRead, budget)
        for _ in 0..<10 where first.hasMoreWork {
            first = try await resumed.refresh(now: now, weekStart: .monday)
        }
        XCTAssertFalse(first.hasMoreWork)
        XCTAssertEqual(first.snapshot.today.totalTokens, expected, "Continuations must not duplicate either task")
    }
}
