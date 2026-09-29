import Foundation
import XCTest
@testable import CodeRim

final class AppPreferencesTests: XCTestCase {
    func testFreshInstallIncludesServerHistoryWithoutEnablingClaudeIntegration() throws {
        let suiteName = "CodeRimTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }

        AppPreferences.registerDefaults(in: defaults)

        // History includes the signed-in account; this never edits Claude config.
        XCTAssertTrue(defaults.bool(forKey: "profileSyncEnabled"))
        XCTAssertFalse(defaults.bool(forKey: "claudeEnabled"))
        // The notch is not an integration — it is what the app looks like.
        XCTAssertTrue(defaults.bool(forKey: "showEdgeNotch"))
    }

    func testServerHistoryDefaultPreservesExplicitOffChoice() throws {
        let suite = "CodeRimTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(false, forKey: "profileSyncEnabled")
        AppPreferences.registerDefaults(in: defaults)
        XCTAssertFalse(defaults.bool(forKey: "profileSyncEnabled"))
    }

    func testPreparingDataDirectoryEnforcesOwnerOnlyPermissions() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o755],
            ofItemAtPath: root.path
        )

        try AppPaths.prepareOwnerOnlyDirectory(at: root)

        let attributes = try FileManager.default.attributesOfItem(atPath: root.path)
        let permissions = try XCTUnwrap(attributes[.posixPermissions] as? NSNumber)
        XCTAssertEqual(permissions.intValue & 0o777, 0o700)
    }

    func testNotchDefaultDoesNotOverrideAnExplicitOffChoice() throws {
        let suiteName = "CodeRimTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        defaults.set(false, forKey: "showEdgeNotch")

        AppPreferences.registerDefaults(in: defaults)

        XCTAssertFalse(defaults.bool(forKey: "showEdgeNotch"))
        defaults.removeObject(forKey: "showEdgeNotch")
        XCTAssertTrue(defaults.bool(forKey: "showEdgeNotch"))
    }

    func testNotchAppearanceDefaultsRegisterAndParseBack() throws {
        let suiteName = "CodeRimTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }

        AppPreferences.registerDefaults(in: defaults)

        XCTAssertEqual(NotchVisibility(rawValue: defaults.string(forKey: "notchVisibility") ?? ""), .onHover)
        XCTAssertEqual(NotchSize(rawValue: defaults.string(forKey: "notchSize") ?? ""), .medium)
        XCTAssertEqual(NotchAccentChoice(rawValue: defaults.string(forKey: "notchAccent") ?? ""), .system)
        XCTAssertEqual(ResetTimeFormat(rawValue: defaults.string(forKey: "notchResetTimeFormat") ?? ""), .automatic)
        XCTAssertTrue(defaults.bool(forKey: "notchThresholdAlerts"))
        XCTAssertFalse(defaults.bool(forKey: "notchShowUsagePace"))
    }

    func testRefreshModesExposePredictablePollingChoices() {
        XCTAssertEqual(RefreshMode.thirtySeconds.pollingInterval, 30)
        XCTAssertEqual(RefreshMode.oneMinute.pollingInterval, 60)
        XCTAssertEqual(RefreshMode.twoMinutes.pollingInterval, 120)
        XCTAssertEqual(RefreshMode.fiveMinutes.pollingInterval, 300)
        XCTAssertEqual(RefreshMode.fifteenMinutes.pollingInterval, 900)
        XCTAssertEqual(RefreshMode.thirtyMinutes.pollingInterval, 1_800)
        XCTAssertNil(RefreshMode.manual.pollingInterval)
        XCTAssertTrue(RefreshMode.automatic.usesFileEvents)
    }

    func testDevelopmentBundlesCannotMigrateProductionUsageDatabase() {
        let base = URL(fileURLWithPath: "/tmp/Application Support", isDirectory: true)

        XCTAssertEqual(
            AppPaths.applicationSupportDirectory(
                baseDirectory: base,
                bundleIdentifier: "dev.codexmeter.CodexMeter"
            ).lastPathComponent,
            "CodexMeter"
        )
        XCTAssertEqual(
            AppPaths.applicationSupportDirectory(
                baseDirectory: base,
                bundleIdentifier: "dev.coderim.CodeRimPreview"
            ).lastPathComponent,
            "CodexMeter-Development"
        )
        XCTAssertEqual(
            AppPaths.applicationSupportDirectory(
                baseDirectory: base,
                bundleIdentifier: nil
            ).lastPathComponent,
            "CodexMeter-Development"
        )
    }
}
