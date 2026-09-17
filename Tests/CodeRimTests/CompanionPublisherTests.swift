import CodeRimShared
import Foundation
import XCTest
@testable import CodeRim

@MainActor
final class CompanionPublisherTests: XCTestCase {
    func testCompanionCatalogMatchesEveryAppProvider() {
        XCTAssertEqual(CompanionProviderID.allCases.map(\.rawValue), NotchProviderCatalog.all.map(\.id))
        XCTAssertEqual(CompanionProviderID.allCases.map(\.name), NotchProviderCatalog.all.map(\.name))
    }

    func testMonetaryProviderValuesSurviveExportWithoutFakePercentage() throws {
        let source = ProviderSnapshot(id: "deepseek", displayName: "DeepSeek", glyph: .third,
            fidelity: .official, status: .ok, windows: [
                LimitWindow(id: "balance", label: "Balance", displayValue: "12.345 CNY")
            ], headlineID: "balance")
        let result = CompanionSnapshotPublisher.provider(.deepseek, snapshot: source, updatedAt: Date())
        let encoded = try JSONEncoder().encode(result)
        let decoded = try JSONDecoder().decode(CompanionProvider.self, from: encoded)
        XCTAssertEqual(decoded.limits.headline?.displayValue, "12.345 CNY")
        XCTAssertNil(decoded.limits.headline?.remainingPercent)
    }

    func testCodexPlanFilterMatchesTheApp() {
        let snapshot = AccountLimitsSnapshot(windows: [
            AccountLimitWindow(id: "session", limitID: "codex", displayName: "Codex",
                windowDurationMinutes: 300, usedPercent: 40, resetsAt: nil),
            AccountLimitWindow(id: "weekly", limitID: "codex", displayName: "Codex",
                windowDurationMinutes: 10080, usedPercent: 60, resetsAt: nil)
        ], resetCredits: nil, fetchedAt: Date())
        let pro = CompanionSnapshotPublisher.limitUsage(snapshot, state: .ready, codexPlan: "pro")
        XCTAssertEqual(pro.windows.map(\.id), ["weekly"])
        XCTAssertEqual(pro.headlineID, "weekly")
        XCTAssertEqual(CompanionSnapshotPublisher.limitUsage(snapshot, state: .ready, codexPlan: "plus").windows.count, 2)
        XCTAssertTrue(CompanionSnapshotPublisher.limitUsage(snapshot, state: .loading, codexPlan: "pro").windows.isEmpty)
    }

    func testCountOnlyReadingsKeepCountsWithoutInventingPercentages() {
        let source = ProviderSnapshot(id: "ollama-local", displayName: "Ollama", glyph: .ollamaLocal,
            fidelity: .official, status: .ok, windows: [
                LimitWindow(id: "loaded", label: "Loaded models", used: 2),
                LimitWindow(id: "model.local", group: "Loaded models", label: "Local model", used: 4096)
            ], headlineID: "loaded")
        let result = CompanionSnapshotPublisher.provider(.ollamaLocal, snapshot: source, updatedAt: Date())
        XCTAssertNil(result.limits.headline?.usedPercent)
        XCTAssertNil(result.limits.headline?.remainingPercent)
        XCTAssertEqual(result.limits.headline?.usedCount, 2)
        XCTAssertEqual(result.limits.headline?.unit, "models")
        XCTAssertEqual(result.limits.windows[1].unit, "MB")
    }

    func testProviderValuesFidelityAndDeclaredHeadlineSurviveExport() {
        let source = ProviderSnapshot(id: "cursor", displayName: "Cursor", glyph: .cursor,
            fidelity: .derived, status: .ok, windows: [
                LimitWindow(id: "requests", label: "Requests", used: 42),
                LimitWindow(id: "credits", label: "Credits", remaining: 99),
                LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.45)
            ], headlineID: "weekly")
        let result = CompanionSnapshotPublisher.provider(.cursor, snapshot: source, updatedAt: Date())
        XCTAssertEqual(result.fidelity, "derived")
        XCTAssertEqual(result.limits.headline?.remainingPercent ?? -1, 55, accuracy: 0.00001)
        XCTAssertEqual(result.limits.windows[0].usedCount, 42)
        XCTAssertEqual(result.limits.windows[1].remainingCount, 99)
    }

    func testUnavailableOrDisabledProviderDoesNotKeepAccountReadings() {
        for id in CompanionProviderID.allCases {
            let disabled = CompanionSnapshotPublisher.disabled(id)
            XCTAssertFalse(disabled.enabled)
            XCTAssertEqual(disabled.limits.state, .disabled)
            XCTAssertTrue(disabled.limits.windows.isEmpty)
            XCTAssertNil(disabled.localUsage)
            let missing = CompanionSnapshotPublisher.provider(id, snapshot: nil, updatedAt: nil)
            XCTAssertEqual(missing.limits.state, .unavailable)
            XCTAssertTrue(missing.limits.windows.isEmpty)
        }
        let source = ProviderSnapshot(id: "copilot", displayName: "Copilot", glyph: .copilot,
            fidelity: .official, status: .needsAuth,
            windows: [LimitWindow(id: "old-account", label: "Old account", usedFraction: 0.5)])
        XCTAssertTrue(CompanionSnapshotPublisher.provider(.copilot, snapshot: source, updatedAt: Date()).limits.windows.isEmpty)
    }

    func testUnsupportedProviderKeepsActionableSetupInstructions() {
        let source = ProviderSnapshot(id: "fireworks", displayName: "Fireworks", glyph: .third,
            fidelity: .official, status: .unsupported("Set FIREWORKS_ACCOUNT_SLUG in provider settings."), windows: [])
        let result = CompanionSnapshotPublisher.provider(.fireworks, snapshot: source, updatedAt: Date())
        XCTAssertEqual(result.limits.state, .unsupported)
        XCTAssertEqual(result.limits.message, "Set FIREWORKS_ACCOUNT_SLUG in provider settings.")
    }

    func testAggregationTimestampDoesNotUseLastEventDate() {
        let now = Date()
        let yesterday = now.addingTimeInterval(-86400)
        let store = UsageStore(initialSnapshot: UsageSnapshot(today: .zero, week: .zero, month: .zero,
            allTime: TokenUsage(inputTokens: 100, cachedInputTokens: 0, outputTokens: 20),
            quality: .exact, updatedAt: yesterday), automaticallyRefresh: false)
        let result = CompanionSnapshotPublisher.localUsage(store, calculatedAt: now)
        XCTAssertEqual(result?.periodsAsOf, now)
        XCTAssertEqual(result?.totals["today"]?.totalTokens, 0)
        XCTAssertEqual(result?.totals["all-time"]?.totalTokens, 120)
        XCTAssertNil(CompanionSnapshotPublisher.localUsage(store, calculatedAt: nil))
    }

    func testCLIInstallerIsIdempotentAndPreservesConflictingFiles() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let app = root.appendingPathComponent("Example App.app")
        let helper = app.appendingPathComponent("Contents/Helpers/CodeRimCLI")
        try FileManager.default.createDirectory(at: helper.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: helper)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: helper.path)
        let bin = root.appendingPathComponent("bin")
        let link = try CLIInstaller.install(appURL: app, binDirectory: bin)
        XCTAssertTrue(FileManager.default.isExecutableFile(atPath: link.path))
        XCTAssertEqual(try CLIInstaller.install(appURL: app, binDirectory: bin), link)
        try FileManager.default.removeItem(at: link)
        try Data("keep me".utf8).write(to: link)
        XCTAssertThrowsError(try CLIInstaller.install(appURL: app, binDirectory: bin))
        XCTAssertEqual(try String(contentsOf: link, encoding: .utf8), "keep me")
    }
    func testExportedHistoryKeepsUnpricedUsageWithoutInventingZeroCost() throws {
        let now = Date()
        let model = ModelUsageSummary(modelID: "unknown-private-model", usage: .init(inputTokens: 100, cachedInputTokens: 40, outputTokens: 20))
        let value = AnalyticsSnapshot(range: .thirtyDays, interval: .init(start: now.addingTimeInterval(-86400), end: now),
            through: now, usage: model.usage, quality: .exact,
            buckets: [.init(start: now.addingTimeInterval(-86400), end: now, models: [model])],
            models: [model], projects: [], sessions: [])
        let result = try XCTUnwrap(CompanionSnapshotPublisher.history(value))
        XCTAssertEqual(result.totalTokens, 120)
        XCTAssertNil(result.estimatedCostUSD)
        XCTAssertTrue(result.costIsPartial)
        XCTAssertNil(result.days.first?.estimatedCostUSD)
        let json = String(decoding: try JSONEncoder().encode(result), as: UTF8.self)
        XCTAssertFalse(json.contains("unknown-private-model"))
        XCTAssertTrue(json.contains("this-mac"))
    }

}
