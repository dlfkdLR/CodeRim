import Darwin
import Foundation
import XCTest
@testable import CodeRim

final class AppIdentityMigrationTests: XCTestCase {
    private var root: URL!
    private let info: [String: Any] = ["CFBundleIdentifier": "dev.codexmeter.CodexMeter",
                                       "CFBundleExecutable": "CodeRim", "CFBundleName": "CodeRim"]

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("identity-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws { try FileManager.default.removeItem(at: root) }

    private func makeApp(_ name: String = "CodexMeter.app", parent: URL? = nil) throws -> URL {
        let app = (parent ?? root).appendingPathComponent(name, isDirectory: true)
        try FileManager.default.createDirectory(at: app.appendingPathComponent("Contents"), withIntermediateDirectories: true)
        try Data("preserve bundle bytes".utf8).write(to: app.appendingPathComponent("Contents/payload"))
        return app
    }

    func testLegacyRenamePreservesContentsAndDirectoryIdentity() throws {
        let source = try makeApp()
        let before = try FileManager.default.attributesOfItem(atPath: source.path)[.systemFileNumber] as? NSNumber
        let destination = try XCTUnwrap(AppIdentityMigration.migrationDestination(for: source, info: info, applicationDirectories: [root]))
        try AppIdentityMigration.moveWithoutReplacing(from: source, to: destination)
        XCTAssertFalse(FileManager.default.fileExists(atPath: source.path))
        XCTAssertEqual(try String(contentsOf: destination.appendingPathComponent("Contents/payload"), encoding: .utf8), "preserve bundle bytes")
        XCTAssertEqual(try FileManager.default.attributesOfItem(atPath: destination.path)[.systemFileNumber] as? NSNumber, before)
        XCTAssertNil(AppIdentityMigration.migrationDestination(for: destination, info: info, applicationDirectories: [root]))
    }

    func testCustomNameAndOtherDirectoryAreUntouched() throws {
        let custom = try makeApp("My CodeRim.app")
        let outside = try makeApp(parent: root.appendingPathComponent("Downloads"))
        for app in [custom, outside] {
            XCTAssertNil(AppIdentityMigration.migrationDestination(for: app, info: info, applicationDirectories: [root]))
            XCTAssertTrue(FileManager.default.fileExists(atPath: app.path))
        }
    }

    func testForeignIdentityAndLegacyExecutableAreUntouched() throws {
        let source = try makeApp()
        for key in ["CFBundleIdentifier", "CFBundleExecutable", "CFBundleName"] {
            var foreign = info
            foreign[key] = "different"
            XCTAssertNil(AppIdentityMigration.migrationDestination(for: source, info: foreign, applicationDirectories: [root]))
        }
    }

    func testExistingDestinationIsNotReplacedEvenWhenItAppearsAfterPlanning() throws {
        let source = try makeApp()
        let destination = try XCTUnwrap(AppIdentityMigration.migrationDestination(for: source, info: info, applicationDirectories: [root]))
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
        try Data("other installation".utf8).write(to: destination.appendingPathComponent("marker"))
        XCTAssertNil(AppIdentityMigration.migrationDestination(for: source, info: info, applicationDirectories: [root]))
        XCTAssertThrowsError(try AppIdentityMigration.moveWithoutReplacing(from: source, to: destination))
        XCTAssertTrue(FileManager.default.fileExists(atPath: source.path))
        XCTAssertEqual(try String(contentsOf: destination.appendingPathComponent("marker"), encoding: .utf8), "other installation")
    }

    func testSymlinkSourceAndDanglingDestinationAreUntouched() throws {
        let real = try makeApp("Managed.app")
        let link = root.appendingPathComponent("CodexMeter.app")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: real)
        XCTAssertNil(AppIdentityMigration.migrationDestination(for: link, info: info, applicationDirectories: [root]))
        try FileManager.default.removeItem(at: link)
        let source = try makeApp()
        let destination = root.appendingPathComponent("CodeRim.app")
        try FileManager.default.createSymbolicLink(at: destination, withDestinationURL: root.appendingPathComponent("missing"))
        XCTAssertNil(AppIdentityMigration.migrationDestination(for: source, info: info, applicationDirectories: [root]))
        XCTAssertThrowsError(try AppIdentityMigration.moveWithoutReplacing(from: source, to: destination))
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: destination.path), root.appendingPathComponent("missing").path)
    }

    func testHomebrewReceiptDefersRenameToPackageManager() throws {
        let source = try makeApp()
        let caskroom = root.appendingPathComponent("Caskroom")
        let version = caskroom.appendingPathComponent("codexmeter/2.0.12")
        try FileManager.default.createDirectory(at: version, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: version.appendingPathComponent("CodexMeter.app"), withDestinationURL: source)
        XCTAssertTrue(AppIdentityMigration.isHomebrewManaged(source, caskrooms: [caskroom]))
        XCTAssertNil(AppIdentityMigration.migrationDestination(for: source, info: info, applicationDirectories: [root], caskrooms: [caskroom]))
        XCTAssertTrue(FileManager.default.fileExists(atPath: source.path))
    }

    func testUnrelatedHomebrewReceiptDoesNotBlockManualInstallation() throws {
        let source = try makeApp()
        let caskroom = root.appendingPathComponent("Caskroom")
        let version = caskroom.appendingPathComponent("coderim/2.1.0")
        try FileManager.default.createDirectory(at: version, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: version.appendingPathComponent("CodeRim.app"), withDestinationURL: root.appendingPathComponent("elsewhere/CodeRim.app"))
        XCTAssertFalse(AppIdentityMigration.isHomebrewManaged(source, caskrooms: [caskroom]))
        XCTAssertNotNil(AppIdentityMigration.migrationDestination(for: source, info: info, applicationDirectories: [root], caskrooms: [caskroom]))
    }

    func testFailedMoveLeavesOriginalBundleIntact() throws {
        let source = try makeApp()
        XCTAssertThrowsError(try AppIdentityMigration.moveWithoutReplacing(from: source, to: root.appendingPathComponent("missing/CodeRim.app")))
        XCTAssertTrue(FileManager.default.fileExists(atPath: source.appendingPathComponent("Contents/payload").path))
    }

    func testExistingCLILinksFollowRenameWithoutInstallingNewCommands() throws {
        let source = try makeApp()
        let helper = source.appendingPathComponent("Contents/Helpers/CodeRimCLI")
        try FileManager.default.createDirectory(at: helper.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: helper)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: helper.path)
        let bin = root.appendingPathComponent("bin")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: bin.appendingPathComponent("codexmeter"), withDestinationURL: helper)
        let destination = root.appendingPathComponent("CodeRim.app")
        try AppIdentityMigration.moveWithoutReplacing(from: source, to: destination)
        XCTAssertEqual(try CLIInstaller.repairExistingInstallation(appURL: destination, binDirectory: bin), 1)
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: bin.appendingPathComponent("codexmeter").path), destination.resolvingSymlinksInPath().appendingPathComponent("Contents/Helpers/CodeRimCLI").path)
        XCTAssertFalse(FileManager.default.fileExists(atPath: bin.appendingPathComponent("coderim").path))
    }

    func testRelaunchWaitsForOldProcessAndPassesPathLiterally() throws {
        let parent = Process()
        parent.executableURL = URL(fileURLWithPath: "/bin/sleep")
        parent.arguments = ["10"]
        try parent.run()
        defer { if parent.isRunning { parent.terminate(); parent.waitUntilExit() } }
        let launcher = root.appendingPathComponent("fake-open")
        try Data("#!/bin/sh\nprintf '%s\\n' \"$@\"\n".utf8).write(to: launcher)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: launcher.path)
        let app = root.appendingPathComponent("CodeRim ' $(touch SHOULD_NOT_EXIST).app")
        let gate = Pipe(), output = Pipe()
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        task.arguments = ["-c", AppIdentityMigration.relaunchScript, "rename-test", String(parent.processIdentifier), app.path, launcher.path]
        task.standardInput = gate
        task.standardOutput = output
        try task.run()
        gate.fileHandleForWriting.write(Data("relaunch\n".utf8))
        try gate.fileHandleForWriting.close()
        Thread.sleep(forTimeInterval: 0.25)
        XCTAssertTrue(task.isRunning, "Must not launch before the old process exits")
        parent.terminate(); parent.waitUntilExit()
        task.waitUntilExit()
        XCTAssertEqual(task.terminationStatus, 0)
        XCTAssertEqual(String(data: output.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8), "-n\n\(app.path)\n")
    }

    func testCancelledMigrationDoesNotRelaunch() throws {
        let gate = Pipe()
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        task.arguments = ["-c", AppIdentityMigration.relaunchScript, "rename-test", "999999999", root.path, "/usr/bin/false"]
        task.standardInput = gate
        try task.run()
        try gate.fileHandleForWriting.close()
        task.waitUntilExit()
        XCTAssertEqual(task.terminationStatus, 0, "EOF must cancel without calling the launcher")
    }
}
