import Foundation
import XCTest
import CodeRimShared
@testable import CodeRim

final class RebrandCompatibilityTests: XCTestCase {
    private var repository: URL {
        URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    }

    func testPersistentIdentityAndLegacyLinksSurviveRebrand() throws {
        let info = try XCTUnwrap(try PropertyListSerialization.propertyList(
            from: Data(contentsOf: repository.appendingPathComponent("Config/Info.plist")),
            format: nil) as? [String: Any])
        XCTAssertEqual(info["CFBundleIdentifier"] as? String, "dev.codexmeter.CodexMeter")
        XCTAssertEqual(info["CFBundleDisplayName"] as? String, "CodeRim")
        XCTAssertEqual(info["CFBundleExecutable"] as? String, "CodeRim")
        let types = try XCTUnwrap(info["CFBundleURLTypes"] as? [[String: Any]])
        XCTAssertEqual(types.first?["CFBundleURLSchemes"] as? [String], ["coderim", "codexmeter"])
        let root = URL(fileURLWithPath: "/fixture")
        XCTAssertEqual(AppPaths.applicationSupportDirectory(baseDirectory: root,
            bundleIdentifier: "dev.codexmeter.CodexMeter").lastPathComponent, "CodexMeter")
        XCTAssertEqual(AppPaths.databaseURL.lastPathComponent, "CodexMeter.sqlite")
        XCTAssertTrue(CompanionSnapshotFile.cliURL.path.hasSuffix("/Library/Application Support/CodexMeter/Companion/snapshot.json"))
        XCTAssertEqual(CompanionSnapshotFile.appGroup, "group.dev.codexmeter.CodexMeter")
    }

    func testCLIUpgradeRetargetsManagedLegacyLinkAndRemainsIdempotent() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let app = root.appendingPathComponent("CodeRim.app")
        let helper = try makeHelper(app: app)
        let bin = root.appendingPathComponent("bin")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let legacy = bin.appendingPathComponent("codexmeter")
        let oldHelper = root.appendingPathComponent("CodexMeter.app/Contents/Helpers/CodexMeterCLI")
        try FileManager.default.createSymbolicLink(at: legacy, withDestinationURL: oldHelper)
        let current = try CLIInstaller.install(appURL: app, binDirectory: bin)
        XCTAssertEqual(current.lastPathComponent, "coderim")
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: legacy.path), helper.path)
        XCTAssertEqual(try CLIInstaller.install(appURL: app, binDirectory: bin), current)
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: current.path), helper.path)
    }

    func testCLIUpgradeSupportsOldAppFilenameAndLeavesForeignLegacyLinkUntouched() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let app = root.appendingPathComponent("CodexMeter.app")
        let helper = try makeHelper(app: app)
        let bin = root.appendingPathComponent("bin")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let legacy = bin.appendingPathComponent("codexmeter")
        try FileManager.default.createSymbolicLink(atPath: legacy.path, withDestinationPath: "/foreign/tool")
        let current = try CLIInstaller.install(appURL: app, binDirectory: bin)
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: current.path), helper.path)
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: legacy.path), "/foreign/tool")
        try FileManager.default.removeItem(at: current)
        try FileManager.default.createSymbolicLink(atPath: current.path, withDestinationPath: "/foreign/coderim")
        XCTAssertThrowsError(try CLIInstaller.install(appURL: app, binDirectory: bin))
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: current.path), "/foreign/coderim")
    }

    func testLaunchRepairNeedsNoInstallActionAndPreservesUnrelatedCommand() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let app = root.appendingPathComponent("CodeRim.app")
        let helper = try makeHelper(app: app)
        let bin = root.appendingPathComponent("bin")
        XCTAssertEqual(try CLIInstaller.repairExistingInstallation(appURL: app, binDirectory: bin), 0)
        XCTAssertFalse(FileManager.default.fileExists(atPath: bin.path))
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let legacy = bin.appendingPathComponent("codexmeter")
        try FileManager.default.createSymbolicLink(at: legacy,
            withDestinationURL: root.appendingPathComponent("CodexMeter.app/Contents/Helpers/CodexMeterCLI"))
        let foreign = bin.appendingPathComponent("coderim")
        try Data("foreign".utf8).write(to: foreign)
        XCTAssertEqual(try CLIInstaller.repairExistingInstallation(appURL: app, binDirectory: bin), 1)
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: legacy.path), helper.path)
        XCTAssertEqual(try String(contentsOf: foreign, encoding: .utf8), "foreign")
        XCTAssertEqual(try CLIInstaller.repairExistingInstallation(appURL: app, binDirectory: bin), 0)
    }

    private func makeHelper(app: URL) throws -> URL {
        let helper = app.appendingPathComponent("Contents/Helpers/CodeRimCLI")
        try FileManager.default.createDirectory(at: helper.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: helper)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: helper.path)
        return helper
    }
}
