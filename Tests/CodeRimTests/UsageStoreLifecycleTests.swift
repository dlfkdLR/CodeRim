import AppKit
import CSQLite
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

    func testFailedMaintenanceKeepsCachedUsageAndReleasesMaintenanceState() async throws {
        for rebuilding in [false, true] {
            let fixture = try Fixture()
            defer { fixture.remove() }
            try fixture.write(output: 10)
            await fixture.store.refresh()
            let original = fixture.store.snapshot
            try fixture.dropUsageTable()
            if rebuilding { await fixture.store.rebuildStatistics() }
            else { await fixture.store.clearLocalHistory() }
            XCTAssertEqual(fixture.store.snapshot.allTime, original.allTime)
            XCTAssertEqual(fixture.store.snapshot.quality, .stale)
            XCTAssertTrue(fixture.store.dataOperationFailed)
            XCTAssertFalse(fixture.store.isMaintainingData)
            XCTAssertEqual(fixture.store.dataOperationMessage,
                           rebuilding ? "Unable to rebuild statistics" : "Unable to clear local history")
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

    func testManualWakeAndChangedRefreshPreferenceSurviveReopen() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.write(output: 17)
        fixture.store.startAutomaticRefresh()
        defer { fixture.store.stopAutomaticRefresh() }
        let wakeCenter = NSWorkspace.shared.notificationCenter
        let clock = ContinuousClock()
        let manualDeadline = clock.now + .seconds(3)
        repeat {
            wakeCenter.post(name: NSWorkspace.didWakeNotification, object: nil)
            try await Task.sleep(for: .milliseconds(25))
        } while !fixture.store.hasLoadedSnapshot && clock.now < manualDeadline
        XCTAssertTrue(fixture.store.hasLoadedSnapshot)
        XCTAssertNil(fixture.store.lastSourceRefreshAt)
        XCTAssertEqual(fixture.store.snapshot.allTime.totalTokens, 0)

        fixture.defaults.set(RefreshMode.oneMinute.rawValue, forKey: "refreshMode")
        NotificationCenter.default.post(name: UserDefaults.didChangeNotification, object: fixture.defaults)
        let changedDeadline = clock.now + .seconds(3)
        while fixture.store.isRefreshing || fixture.store.lastSourceRefreshAt == nil {
            if clock.now >= changedDeadline { break }
            try await Task.sleep(for: .milliseconds(25))
        }
        XCTAssertEqual(fixture.store.snapshot.allTime.totalTokens, 117)
        fixture.store.stopAutomaticRefresh()

        let reopenedDefaults = try XCTUnwrap(UserDefaults(suiteName: fixture.defaultsDomain))
        let reopened = UsageStore(provider: .claude, automaticallyRefresh: false,
                                  collector: fixture.collector, defaults: reopenedDefaults)
        try fixture.write(output: 456)
        reopened.startAutomaticRefresh()
        defer { reopened.stopAutomaticRefresh() }
        let reopenedDeadline = clock.now + .seconds(3)
        repeat {
            wakeCenter.post(name: NSWorkspace.didWakeNotification, object: nil)
            try await Task.sleep(for: .milliseconds(25))
        } while !reopened.isRefreshing && reopened.lastSourceRefreshAt == nil && clock.now < reopenedDeadline
        while reopened.isRefreshing && clock.now < reopenedDeadline {
            try await Task.sleep(for: .milliseconds(25))
        }
        XCTAssertNotNil(reopened.lastSourceRefreshAt)
        XCTAssertEqual(reopened.snapshot.allTime.totalTokens, 556)
    }

    func testWeekChangeRecalculatesPersistedUsageWithoutImportingPendingRows() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        let domain = "dev.coderim.week-start-test." + UUID().uuidString
        let defaults = try XCTUnwrap(SplitWeekDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        defaults.set("manual", forKey: "refreshMode")
        defaults.set(1, forKey: "fixtureWeekStart")
        let store = UsageStore(provider: .claude, automaticallyRefresh: false,
                               collector: fixture.collector, defaults: defaults)
        store.startAutomaticRefresh()
        defer { store.stopAutomaticRefresh() }

        // On Sunday, Saturday belongs only to the Monday-based week. On other
        // days the most recent Sunday belongs only to the Sunday-based week.
        let calendar = Calendar.current
        let now = Date()
        let weekday = calendar.component(.weekday, from: now)
        let daysAgo = weekday == 1 ? 1 : weekday - 1
        let date = try XCTUnwrap(calendar.date(byAdding: .day, value: -daysAgo, to: now))
        try fixture.write(output: 17, timestamp: date.formatted(.iso8601))
        await store.refresh()
        XCTAssertEqual(store.snapshot.allTime.totalTokens, 117)
        XCTAssertEqual(store.snapshot.week.totalTokens, weekday == 1 ? 0 : 117)
        let lastRead = try XCTUnwrap(store.lastSourceRefreshAt)

        try fixture.write(output: 456)
        defaults.set(2, forKey: "fixtureWeekStart")
        NotificationCenter.default.post(name: UserDefaults.didChangeNotification, object: defaults)
        let clock = ContinuousClock()
        let recalcDeadline = clock.now + .seconds(3)
        let mondayTotal: Int64 = weekday == 1 ? 117 : 0
        while store.snapshot.week.totalTokens != mondayTotal && clock.now < recalcDeadline {
            try await Task.sleep(for: .milliseconds(25))
        }
        XCTAssertEqual(store.snapshot.week.totalTokens, mondayTotal)
        XCTAssertEqual(store.snapshot.allTime.totalTokens, 117)
        XCTAssertEqual(store.lastSourceRefreshAt, lastRead)

        defaults.set("60", forKey: "refreshMode")
        NotificationCenter.default.post(name: UserDefaults.didChangeNotification, object: defaults)
        let scanDeadline = clock.now + .seconds(3)
        while (store.snapshot.allTime.totalTokens != 556 || store.isRefreshing) && clock.now < scanDeadline {
            try await Task.sleep(for: .milliseconds(25))
        }
        XCTAssertEqual(store.snapshot.allTime.totalTokens, 556)
        XCTAssertNotEqual(store.lastSourceRefreshAt, lastRead)
    }

    private final class SplitWeekDefaults: UserDefaults, @unchecked Sendable {
        override func object(forKey key: String) -> Any? {
            if key == "weekStart" { return 2 }
            return super.object(forKey: key)
        }

        override func integer(forKey key: String) -> Int {
            super.integer(forKey: key == "weekStart" ? "fixtureWeekStart" : key)
        }
    }

    func testConcurrentRequestsCannotRestoreClearedAnalysis() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.write(output: 10)
        await fixture.store.refresh()
        let requests = (0..<20).map { index in
            Task { await fixture.store.refreshAnalytics(range: AnalyticsRange.allCases[index % AnalyticsRange.allCases.count]) }
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

        func write(output: Int, timestamp: String? = nil) throws {
            let row = """
            {"type":"assistant","timestamp":"\(timestamp ?? self.timestamp)","sessionId":"test-session","message":{"id":"test-message","role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":100,"output_tokens":\(output)}}}
            """
            try Data((row + "\n").utf8).write(to: source)
        }

        func remove() {
            defaults.removePersistentDomain(forName: defaultsDomain)
            try? FileManager.default.removeItem(at: root)
        }

        func dropUsageTable() throws {
            var connection: OpaquePointer?
            let url = root.appendingPathComponent("Claude.sqlite")
            guard sqlite3_open_v2(url.path, &connection, SQLITE_OPEN_READWRITE, nil) == SQLITE_OK else {
                if let connection { sqlite3_close(connection) }
                throw NSError(domain: "UsageStoreLifecycleTests", code: 1)
            }
            defer { sqlite3_close(connection) }
            XCTAssertEqual(sqlite3_exec(connection, "DROP TABLE usage_events", nil, nil, nil), SQLITE_OK)
        }
    }
}
