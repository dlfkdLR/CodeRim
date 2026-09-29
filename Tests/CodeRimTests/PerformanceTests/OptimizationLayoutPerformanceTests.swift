import AppKit
import Darwin
import Foundation
import XCTest
@testable import CodeRim

@MainActor
final class OptimizationLayoutPerformanceTests: XCTestCase {
    func testClosedSettingsCPUWithStoreUpdates() async throws {
        _ = NSApplication.shared
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("coderim-layout-perf-\(UUID())")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let suite = "dev.coderim.layout-perf.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set("manual", forKey: "refreshMode")
        let collector = CodexUsageCollector(database: try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite")), roots: [])
        let store = UsageStore(automaticallyRefresh: false, collector: collector, defaults: defaults)
        let environment = SettingsEnvironment(codexStore: store, claudeStore: store,
            limitStore: AccountLimitStore(defaults: defaults, pollingInterval: nil),
            claude: ClaudeIntegrationStore(defaults: defaults, automaticallyRefresh: false),
            profileStore: ProfileUsageStore(defaults: defaults, allowsAccountTotals: false))
        let controller = SettingsWindowController()
        controller.configure(environment: environment)
        defer { controller.closeSettingsForTesting() }
        controller.present(selecting: .category(.usage))
        try await Task.sleep(for: .milliseconds(500))
        controller.closeSettingsForTesting()
        let originalController = controller.settingsContentViewControllerForTesting
        try await Task.sleep(for: .milliseconds(200))
        let initialCPU = cpuTime()
        let started = ContinuousClock.now
        for _ in 0..<10 {
            await store.refresh()
            try await Task.sleep(for: .milliseconds(100))
        }
        try await Task.sleep(for: .seconds(1))
        let elapsed = started.duration(to: .now)
        let seconds = Double(elapsed.components.seconds) + Double(elapsed.components.attoseconds) / 1e18
        let used = cpuTime() - initialCPU
        print("OPTIMIZATION_LAYOUT_BENCHMARK closed Settings, 10 store updates: cpuSeconds=\(used), wallSeconds=\(seconds), cpuPercent=\(100 * used / seconds)")
        XCTAssertFalse(controller.isSettingsWindowVisible)
        controller.present()
        XCTAssertTrue(originalController === controller.settingsContentViewControllerForTesting)
        XCTAssertTrue(controller.isSettingsWindowVisible)
    }

    private func cpuTime() -> Double {
        var usage = rusage()
        getrusage(RUSAGE_SELF, &usage)
        return Double(usage.ru_utime.tv_sec + usage.ru_stime.tv_sec)
            + Double(usage.ru_utime.tv_usec + usage.ru_stime.tv_usec) / 1e6
    }
}
