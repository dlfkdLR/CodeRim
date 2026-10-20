import Foundation
import XCTest
@testable import CodeRim

@MainActor
final class OptimizationPerformanceTests: XCTestCase {
    func testBackgroundRefreshBenchmark() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("coderim-perf-\(UUID())")
        let sources = root.appendingPathComponent("projects")
        try FileManager.default.createDirectory(at: sources, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let stamp = Date().formatted(.iso8601)
        for session in 0..<100 {
            var rows = ""
            for event in 0..<100 {
                rows += "{\"type\":\"assistant\",\"timestamp\":\"\(stamp)\",\"sessionId\":\"s-\(session)\",\"message\":{\"id\":\"m-\(session)-\(event)\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":100,\"cache_read_input_tokens\":25,\"output_tokens\":25}}}\n"
            }
            try Data(rows.utf8).write(to: sources.appendingPathComponent("s-\(session).jsonl"))
        }
        let db = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: db, roots: [sources], provider: .claude)
        let suite = "dev.coderim.perf.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = UsageStore(provider: .claude, automaticallyRefresh: false, collector: collector, defaults: defaults)
        await store.refresh(forceAnalytics: false)
        while store.isImportingHistory || store.isRefreshing {
            if !store.isRefreshing { await store.refresh(forceAnalytics: false) }
            try await Task.sleep(for: .milliseconds(10))
        }
        store.stopAutomaticRefresh()
        for range in AnalyticsRange.allCases { await store.refreshAnalytics(range: range) }
        let expected = store.snapshot.allTime
        XCTAssertEqual(expected.totalTokens, 1_500_000)
        let clock = ContinuousClock()
        let unchangedStart = clock.now
        for _ in 0..<10 {
            XCTAssertFalse(store.isRefreshing)
            let previous = store.lastSourceRefreshAt
            await store.refresh(forceAnalytics: false)
            XCTAssertFalse(store.isRefreshing)
            XCTAssertNotEqual(store.lastSourceRefreshAt, previous)
        }
        let unchanged = unchangedStart.duration(to: clock.now)
        let narrationStart = clock.now
        for index in 0..<10 {
            let handle = try FileHandle(forWritingTo: sources.appendingPathComponent("s-0.jsonl"))
            try handle.seekToEnd()
            try handle.write(contentsOf: Data("{\"type\":\"progress\",\"timestamp\":\"\(stamp)\",\"index\":\(index)}\n".utf8))
            try handle.close()
            XCTAssertFalse(store.isRefreshing)
            let previous = store.lastSourceRefreshAt
            await store.refresh(forceAnalytics: false)
            XCTAssertFalse(store.isRefreshing)
            XCTAssertNotEqual(store.lastSourceRefreshAt, previous)
        }
        let narration = narrationStart.duration(to: clock.now)
        XCTAssertEqual(store.snapshot.allTime, expected)
        for range in AnalyticsRange.allCases { XCTAssertEqual(store.analyticsSnapshot(for: range)?.usage, expected) }
        print("OPTIMIZATION_BENCHMARK 10000 events, 100 files, 3 ranges, 10 warm refreshes: unchanged=\(unchanged), narration=\(narration)")
    }
}
