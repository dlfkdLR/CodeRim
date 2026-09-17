import Foundation
import XCTest
@testable import CodeRim

@MainActor
final class UsageStoreLifecycleTests: XCTestCase {
    func testRefreshUpdatesPreviouslyLoadedRanges() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.write(output: 10)
        await fixture.store.refresh()
        for range in AnalyticsRange.allCases {
            await fixture.store.refreshAnalytics(range: range)
        }
        try fixture.write(output: 25)
        await fixture.store.refresh()
        XCTAssertEqual(fixture.store.snapshot.allTime.totalTokens, 125)
        for range in AnalyticsRange.allCases {
            XCTAssertEqual(fixture.store.analyticsSnapshot(for: range)?.usage.totalTokens, 125,
                           "An open \(range.title) view must receive the new counters")
        }
    }

    func testClearAndRebuildDoNotRetainOldAnalyticsOrAffectAnotherProvider() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.write(output: 10)
        await fixture.store.refresh()
        for range in AnalyticsRange.allCases {
            await fixture.store.refreshAnalytics(range: range)
        }
        let otherDB = try SQLiteDatabase(url: fixture.root.appendingPathComponent("Codex.sqlite"))
        let codexSources = fixture.root.appendingPathComponent("codex-sessions")
        try FileManager.default.createDirectory(at: codexSources, withIntermediateDirectories: true)
        let codexRow = """
        {"timestamp":"\(fixture.timestamp)","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"cached_input_tokens":10,"output_tokens":20,"total_tokens":100},"last_token_usage":{"input_tokens":80,"cached_input_tokens":10,"output_tokens":20,"total_tokens":100}}}}
        """
        try Data((codexRow + "\n").utf8).write(to: codexSources.appendingPathComponent("session.jsonl"))
        let other = CodexUsageCollector(database: otherDB, roots: [codexSources])
        _ = try await other.refresh(weekStart: .monday)
        let otherBefore = try await other.cachedSnapshot(weekStart: .monday)
        XCTAssertEqual(otherBefore.allTime.totalTokens, 100)

        await fixture.store.clearLocalHistory()
        XCTAssertFalse(fixture.store.dataOperationFailed)
        XCTAssertEqual(fixture.store.snapshot.allTime.totalTokens, 0)
        for range in AnalyticsRange.allCases {
            XCTAssertEqual(fixture.store.analyticsSnapshot(for: range)?.usage.totalTokens, 0,
                           "Cleared usage must not remain in cached \(range.title) analysis")
        }
        await fixture.store.rebuildStatistics()
        XCTAssertFalse(fixture.store.dataOperationFailed)
        XCTAssertEqual(fixture.store.snapshot.allTime.totalTokens, 0)
        let otherAfter = try await other.cachedSnapshot(weekStart: .monday)
        XCTAssertEqual(otherBefore, otherAfter)
        XCTAssertTrue(FileManager.default.fileExists(atPath: fixture.source.path))
    }

    func testRebuildRefreshesEveryLoadedRange() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.write(output: 10)
        await fixture.store.refresh()
        for range in AnalyticsRange.allCases {
            await fixture.store.refreshAnalytics(range: range)
        }
        try fixture.write(output: 30)
        await fixture.store.rebuildStatistics()
        XCTAssertEqual(fixture.store.snapshot.allTime.totalTokens, 130)
        for range in AnalyticsRange.allCases {
            XCTAssertEqual(fixture.store.analyticsSnapshot(for: range)?.usage.totalTokens, 130)
        }
    }

    func testCalendarRecalculationUpdatesLoadedAnalyticsWithoutReadingNewSourceBytes() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.write(output: 10)
        await fixture.store.refresh()
        for range in AnalyticsRange.allCases {
            await fixture.store.refreshAnalytics(range: range)
        }
        // Simulate a refreshed DB from another window before a calendar/wake event.
        try fixture.write(output: 30)
        _ = try await fixture.collector.refresh(weekStart: .monday)
        try fixture.write(output: 60)
        await fixture.store.recalculateVisiblePeriods()
        XCTAssertEqual(fixture.store.snapshot.allTime.totalTokens, 130)
        for range in AnalyticsRange.allCases {
            XCTAssertEqual(fixture.store.analyticsSnapshot(for: range)?.usage.totalTokens, 130)
        }
    }

    func testConcurrentRequestsCannotRestoreClearedAnalysis() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.write(output: 10)
        await fixture.store.refresh()
        let requests = (0..<20).map { index in
            Task { await fixture.store.refreshAnalytics(range: AnalyticsRange.allCases[index % 3]) }
        }
        await Task.yield()
        await fixture.store.clearLocalHistory()
        for request in requests { await request.value }
        XCTAssertFalse(fixture.store.isAnalyticsRefreshing)
        XCTAssertEqual(fixture.store.snapshot.allTime.totalTokens, 0)
        for range in AnalyticsRange.allCases {
            XCTAssertEqual(fixture.store.analyticsSnapshot(for: range)?.usage.totalTokens, 0)
        }
    }

    @MainActor
    private struct Fixture {
        let root: URL
        let sources: URL
        let source: URL
        let store: UsageStore
        let collector: CodexUsageCollector
        let defaults: UserDefaults
        let defaultsDomain: String
        let timestamp: String

        init() throws {
            root = FileManager.default.temporaryDirectory.appendingPathComponent("usage-lifecycle-\(UUID())")
            sources = root.appendingPathComponent("projects")
            source = sources.appendingPathComponent("session.jsonl")
            try FileManager.default.createDirectory(at: sources, withIntermediateDirectories: true)
            let database = try SQLiteDatabase(url: root.appendingPathComponent("Claude.sqlite"))
            collector = CodexUsageCollector(database: database, roots: [sources], provider: .claude)
            defaultsDomain = "dev.coderim.lifecycle-test.\(UUID())"
            defaults = try XCTUnwrap(UserDefaults(suiteName: defaultsDomain))
            defaults.set(RefreshMode.manual.rawValue, forKey: "refreshMode")
            store = UsageStore(provider: .claude, automaticallyRefresh: false,
                               collector: collector, defaults: defaults)
            timestamp = Date().formatted(.iso8601)
        }

        func write(output: Int) throws {
            let row = """
            {"type":"assistant","timestamp":"\(timestamp)","sessionId":"test-session","message":{"id":"test-message","role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":100,"output_tokens":\(output)}}}
            """
            try Data((row + "\n").utf8).write(to: source)
        }

        func remove() {
            defaults.removePersistentDomain(forName: defaultsDomain)
            try? FileManager.default.removeItem(at: root)
        }
    }
}
