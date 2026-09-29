import AppKit
import Combine
import CoreServices
import Foundation
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class OptimizationRegressionTests: XCTestCase {
    func testUnchangedBackgroundRefreshPreservesEveryLoadedAnalysis() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        await fixture.load()
        let original = fixture.store.analyticsSnapshots
        var emissions = 0
        let subscription = fixture.store.$analyticsSnapshots.dropFirst().sink { _ in emissions += 1 }
        defer { subscription.cancel() }
        for _ in 0..<5 { await fixture.store.refresh(forceAnalytics: false) }
        XCTAssertEqual(fixture.store.analyticsSnapshots, original)
        XCTAssertEqual(emissions, 0)
        XCTAssertFalse(fixture.store.isAnalyticsRefreshing)
    }

    func testNarrationDoesNotRefreshAnalysisButNewTokensDo() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        await fixture.load()
        let original = fixture.store.analyticsSnapshots
        try fixture.append("{\"timestamp\":\"\(fixture.timestamp)\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_reasoning\",\"text\":\"synthetic narration\"}}")
        await fixture.store.refresh(forceAnalytics: false)
        XCTAssertEqual(fixture.store.analyticsSnapshots, original)
        try fixture.append(fixture.tokens(input: 150, output: 30, first: false))
        await fixture.store.refresh(forceAnalytics: false)
        for range in AnalyticsRange.allCases {
            XCTAssertEqual(fixture.store.analyticsSnapshot(for: range)?.usage.totalTokens, 180)
        }
    }

    func testImageOnlyChangeRefreshesSessionsWithoutChangingTotals() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        await fixture.load()
        let totals = fixture.store.snapshot.allTime
        let previousThrough = fixture.store.analyticsSnapshot(for: .today)?.through
        try fixture.append("{\"timestamp\":\"\(fixture.timestamp)\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_image\",\"image_url\":\"synthetic-image\"}]}}")
        await fixture.store.refresh(forceAnalytics: false)
        XCTAssertEqual(fixture.store.snapshot.allTime, totals)
        for range in AnalyticsRange.allCases {
            XCTAssertEqual(fixture.store.analyticsSnapshot(for: range)?.sessions.first?.imageAttachmentCount, 1)
        }
        XCTAssertNotEqual(fixture.store.analyticsSnapshot(for: .today)?.through, previousThrough)
    }

    func testManualRefreshForcesAnalysisAndMaintenanceDoesNotRestoreCachedData() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        await fixture.load()
        let previousThrough = fixture.store.analyticsSnapshot(for: .today)?.through
        await fixture.store.refresh()
        XCTAssertNotEqual(fixture.store.analyticsSnapshot(for: .today)?.through, previousThrough)
        await fixture.store.clearLocalHistory()
        await fixture.store.refresh(forceAnalytics: false)
        XCTAssertEqual(fixture.store.snapshot.allTime.totalTokens, 0)
        for range in AnalyticsRange.allCases {
            XCTAssertEqual(fixture.store.analyticsSnapshot(for: range)?.usage.totalTokens, 0)
        }
    }

    func testCollectorRevisionIncludesMetadataAndExternalMaintenanceEpoch() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        let first = try await fixture.collector.refresh(weekStart: .monday)
        try fixture.append("{\"timestamp\":\"\(fixture.timestamp)\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"synthetic\"}}")
        let narration = try await fixture.collector.refresh(weekStart: .monday)
        XCTAssertEqual(narration.analyticsRevision, first.analyticsRevision)
        try fixture.append("{\"timestamp\":\"\(fixture.timestamp)\",\"type\":\"turn_context\",\"payload\":{\"cwd\":\"/tmp/SyntheticOtherProject\",\"model\":\"gpt-5.5\"}}")
        let metadata = try await fixture.collector.refresh(weekStart: .monday)
        XCTAssertEqual(metadata.snapshot.allTime, first.snapshot.allTime)
        XCTAssertGreaterThan(metadata.analyticsRevision, narration.analyticsRevision)
        try await fixture.database.rebuildStatistics()
        let rebuilt = try await fixture.collector.refresh(weekStart: .monday)
        XCTAssertGreaterThan(rebuilt.analyticsRevision, metadata.analyticsRevision)
    }

    func testWatcherFiltersUnrelatedPathsAndKeepsRecoveryEvents() throws {
        let root = URL(fileURLWithPath: "/private/tmp/coderim-filter-fixture")
        let source = root.appendingPathComponent("sessions")
        let watcher = CodexSessionWatcher(roots: [root], sourceRoots: [source])
        defer { watcher.stop() }
        XCTAssertFalse(watcher.shouldRefresh(path: root.appendingPathComponent("state_5.sqlite-wal").path, flags: 0))
        XCTAssertFalse(watcher.shouldRefresh(path: root.appendingPathComponent("sessions-other/x.jsonl").path, flags: 0))
        XCTAssertTrue(watcher.shouldRefresh(path: source.appendingPathComponent("2026/09/session.jsonl").path, flags: 0))
        XCTAssertTrue(watcher.shouldRefresh(path: source.path, flags: 0))
        XCTAssertTrue(watcher.shouldRefresh(path: root.path, flags: 0))
        for flag in [kFSEventStreamEventFlagMustScanSubDirs, kFSEventStreamEventFlagUserDropped,
                     kFSEventStreamEventFlagKernelDropped, kFSEventStreamEventFlagEventIdsWrapped,
                     kFSEventStreamEventFlagRootChanged, kFSEventStreamEventFlagMount,
                     kFSEventStreamEventFlagUnmount] {
            XCTAssertTrue(watcher.shouldRefresh(path: root.appendingPathComponent("unrelated").path,
                                                flags: FSEventStreamEventFlags(flag)))
        }
    }

    func testFilteredWatcherStillDeliversNativeEventsThroughSymlinkRoots() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("watch-perf-\(UUID())")
        let actual = root.appendingPathComponent("actual")
        let alias = root.appendingPathComponent("alias")
        try FileManager.default.createDirectory(at: actual, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: actual)
        defer { try? FileManager.default.removeItem(at: root) }
        let watcher = CodexSessionWatcher(roots: [root], sourceRoots: [alias])
        XCTAssertTrue(watcher.shouldRefresh(path: actual.appendingPathComponent("session.jsonl").path, flags: 0))
        let delivered = expectation(description: "filtered native source event")
        let task = Task {
            for await _ in watcher.events { delivered.fulfill(); return }
        }
        watcher.start()
        defer { watcher.stop(); task.cancel() }
        try await Task.sleep(for: .milliseconds(100))
        try Data("{}\n".utf8).write(to: actual.appendingPathComponent("session.jsonl"))
        await fulfillment(of: [delivered], timeout: 5)
    }

    func testClosingSettingsDisconnectsDisplayAndReopeningKeepsTheSameHierarchy() async throws {
        _ = NSApplication.shared
        let fixture = try Fixture()
        defer { fixture.remove() }
        let environment = SettingsEnvironment(codexStore: fixture.store, claudeStore: fixture.store,
            limitStore: AccountLimitStore(defaults: fixture.defaults, pollingInterval: nil),
            claude: ClaudeIntegrationStore(defaults: fixture.defaults, automaticallyRefresh: false),
            profileStore: ProfileUsageStore(defaults: fixture.defaults, allowsAccountTotals: false))
        let controller = SettingsWindowController()
        controller.configure(environment: environment)
        defer { controller.closeSettingsForTesting() }
        controller.present(selecting: .category(.usage))
        try await Task.sleep(for: .milliseconds(100))
        let content = try XCTUnwrap(controller.settingsContentViewControllerForTesting)
        let hosting = try XCTUnwrap(content.children.first)
        let view = hosting.view
        let window = try XCTUnwrap(view.window)
        let frame = window.frame
        let size = content.view.bounds.size
        controller.closeSettingsForTesting()
        XCTAssertNil(content.view.window)
        XCTAssertNil(view.window)
        controller.present()
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(content === controller.settingsContentViewControllerForTesting)
        XCTAssertTrue(hosting === controller.settingsContentViewControllerForTesting?.children.first)
        XCTAssertTrue(view === hosting.view)
        XCTAssertTrue(view.window === window)
        XCTAssertTrue(controller.attachedSettingsContentViewControllerForTesting === content)
        XCTAssertEqual(window.frame, frame)
        XCTAssertEqual(content.view.bounds.size, size)
        XCTAssertTrue(controller.isSettingsWindowVisible)
        if ProcessInfo.processInfo.environment["CODERIM_OPTIMIZATION_UI_HOLD"] == "1" {
            // Allows a desktop accessibility/screenshot check of this isolated fixture.
            try await Task.sleep(for: .seconds(45))
        }
    }

    @MainActor
    private struct Fixture {
        let root: URL
        let source: URL
        let database: SQLiteDatabase
        let collector: CodexUsageCollector
        let store: UsageStore
        let defaults: UserDefaults
        let suite: String
        let timestamp = Date().addingTimeInterval(-60).formatted(.iso8601)

        init() throws {
            root = FileManager.default.temporaryDirectory.appendingPathComponent("optimization-regression-\(UUID())")
            let sources = root.appendingPathComponent("sessions")
            try FileManager.default.createDirectory(at: sources, withIntermediateDirectories: true)
            source = sources.appendingPathComponent("session.jsonl")
            database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
            collector = CodexUsageCollector(database: database, roots: [sources])
            suite = "dev.coderim.optimization.\(UUID())"
            defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
            store = UsageStore(automaticallyRefresh: false, collector: collector, defaults: defaults)
            let metadata = "{\"timestamp\":\"\(timestamp)\",\"type\":\"session_meta\",\"payload\":{\"id\":\"optimization-session\",\"cwd\":\"/tmp/SyntheticProject\",\"model\":\"gpt-5.5\"}}"
            try Data((metadata + "\n" + tokens(input: 100, output: 20, first: true) + "\n").utf8).write(to: source)
        }
        func tokens(input: Int, output: Int, first: Bool) -> String {
            "{\"timestamp\":\"\(timestamp)\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":\(input),\"cached_input_tokens\":0,\"output_tokens\":\(output)},\"last_token_usage\":{\"input_tokens\":\(first ? input : 50),\"cached_input_tokens\":0,\"output_tokens\":\(first ? output : 10)}}}}"
        }
        func append(_ row: String) throws {
            let handle = try FileHandle(forWritingTo: source)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: Data((row + "\n").utf8))
        }
        func load() async {
            await store.refresh(forceAnalytics: false)
            for range in AnalyticsRange.allCases { await store.refreshAnalytics(range: range) }
        }
        func remove() {
            store.stopAutomaticRefresh()
            defaults.removePersistentDomain(forName: suite)
            try? FileManager.default.removeItem(at: root)
        }
    }
}
