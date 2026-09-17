import Darwin
import Foundation
import XCTest
@testable import CodeRim

final class CodexProcessInspectorTests: XCTestCase {
    private let userID: uid_t = 501

    func testUnreadableBrowserHelpersAndWidgetDoNotBlockSwitching() throws {
        let inspector = fixture(commands: ["chrome-native-host", "ChatGPT for Chrome", "CodexBarWidget"])
        XCTAssertEqual(try CodexProcessGate.runningCodexPIDs(using: inspector, userID: userID), [])
        try withAuthFile { auth in
            XCTAssertNoThrow(try CodexProcessGate.requireStopped(using: inspector, userID: userID, authFile: auth))
        }
    }

    func testRootTerminalLoginIsIgnoredEvenWhenSignalPermissionSucceeds() throws {
        var inspector = fixture(commands: ["login"])
        inspector.entries[1] = CodexProcessMetadata(userID: 0, status: UInt32(SRUN), command: "login")
        inspector.onPath = { _ in XCTFail("Other users must be excluded before executable lookup"); return nil }
        XCTAssertTrue(inspector.isAlive(1))
        try withAuthFile { auth in
            XCTAssertNoThrow(try CodexProcessGate.requireStopped(using: inspector, userID: userID, authFile: auth))
        }
    }

    func testIdentifiedCodexProcessDoesNotBlockUnlessItHoldsTheLoginFile() throws {
        var inspector = fixture(commands: ["codex"])
        inspector.paths[1] = "/Applications/ChatGPT.app/Contents/Resources/codex"
        // Identity still recognises it, but an idle app-server holds no login file.
        XCTAssertEqual(try CodexProcessGate.runningCodexPIDs(using: inspector, userID: userID), [1])
        try withAuthFile { auth in
            XCTAssertNoThrow(try CodexProcessGate.requireStopped(using: inspector, userID: userID, authFile: auth))
        }
    }

    func testCodexProcessHoldingTheLoginFileBlocksSwitching() throws {
        var inspector = fixture(commands: ["codex"], holdsAuth: [1])
        inspector.paths[1] = "/Applications/ChatGPT.app/Contents/Resources/codex"
        try withAuthFile { auth in
            assertGateError(.codexRunning, inspector: inspector, authFile: auth)
        }
    }

    func testMissingLoginFileNeverBlocksEvenWithLiveCodexProcesses() throws {
        var inspector = fixture(commands: ["codex"], holdsAuth: [1])
        inspector.paths[1] = "/synthetic/codex"
        let absent = FileManager.default.temporaryDirectory
            .appendingPathComponent("coderim-absent-\(UUID().uuidString).json")
        XCTAssertNoThrow(try CodexProcessGate.requireStopped(using: inspector, userID: userID, authFile: absent))
    }

    func testKnownCodexPathIsStillIdentifiedWithDifferentCommandMetadata() throws {
        var inspector = fixture(commands: ["unrelated-name"], holdsAuth: [1])
        inspector.paths[1] = "/Applications/ChatGPT.app/Contents/Resources/codex"
        XCTAssertEqual(try CodexProcessGate.runningCodexPIDs(using: inspector, userID: userID), [1])
        try withAuthFile { auth in
            assertGateError(.codexRunning, inspector: inspector, authFile: auth)
        }
    }

    func testArchitectureSpecificCodexPathsAreIdentifiedAndBlockWhenHoldingTheFile() throws {
        for name in ["codex-aarch64-apple-darwin", "codex-x86_64-apple-darwin"] {
            var inspector = fixture(commands: [name], holdsAuth: [1])
            inspector.paths[1] = "/synthetic/\(name)"
            try withAuthFile { auth in
                assertGateError(.codexRunning, inspector: inspector, authFile: auth)
            }
        }
    }

    func testMissingCodexPathDoesNotBypassIdentityIncludingTruncatedNames() throws {
        for name in ["codex", "codex-aarch64-apple-darwin", "codex-x86_64-apple-darwin",
                     "codex-aarch64-ap", "codex-x86_64-app", "codex-unknown"] {
            let inspector = fixture(commands: [name], holdsAuth: [1])
            XCTAssertEqual(try CodexProcessGate.runningCodexPIDs(using: inspector, userID: userID), [1])
            try withAuthFile { auth in
                assertGateError(.codexRunning, inspector: inspector, authFile: auth)
            }
        }
    }

    func testSuspendedCodexHoldingTheFileStillBlocks() throws {
        var inspector = fixture(commands: ["codex"], holdsAuth: [1])
        inspector.entries[1] = CodexProcessMetadata(userID: userID, status: UInt32(SSTOP), command: "codex")
        try withAuthFile { auth in
            assertGateError(.codexRunning, inspector: inspector, authFile: auth)
        }
    }

    func testZombieCodexCannotUseTheLoginAndDoesNotBlock() throws {
        var inspector = fixture(commands: ["codex"], holdsAuth: [1])
        inspector.entries[1] = CodexProcessMetadata(userID: userID, status: UInt32(SZOMB), command: "codex")
        inspector.onPath = { _ in XCTFail("A zombie has no executable to inspect"); return nil }
        try withAuthFile { auth in
            XCTAssertNoThrow(try CodexProcessGate.requireStopped(using: inspector, userID: userID, authFile: auth))
        }
    }

    func testUnidentifiedLiveProcessFailsClosedWithAnAccurateError() {
        var inspector = fixture(commands: ["codex"])
        inspector.entries = [:]
        assertGateError(.processInspectionFailed, inspector: inspector)
    }

    func testCodexProcessWithUnreadableOpenFilesFailsClosedWhileAlive() throws {
        var inspector = fixture(commands: ["codex"])
        inspector.paths[1] = "/synthetic/codex"
        inspector.holdsFileResult = { _ in nil }
        try withAuthFile { auth in
            assertGateError(.processInspectionFailed, inspector: inspector, authFile: auth)
        }
    }

    func testCodexProcessThatExitsDuringOpenFileLookupDoesNotBlock() throws {
        var inspector = fixture(commands: ["codex"])
        inspector.paths[1] = "/synthetic/codex"
        inspector.holdsFileResult = { _ in nil }
        inspector.alive = []
        try withAuthFile { auth in
            XCTAssertNoThrow(try CodexProcessGate.requireStopped(using: inspector, userID: userID, authFile: auth))
        }
    }

    func testEmptyCommandAndMissingPathFailClosed() {
        assertGateError(.processInspectionFailed, inspector: fixture(commands: [""]))
    }

    func testAlreadyExitedProcessWithoutMetadataDoesNotBlock() throws {
        var inspector = fixture(commands: ["codex"])
        inspector.entries = [:]
        inspector.alive = []
        XCTAssertNoThrow(try CodexProcessGate.requireStopped(using: inspector, userID: userID))
    }

    func testRechecksIdentityWhenPathLookupFails() throws {
        var inspector = fixture(commands: ["chrome-native-host"], holdsAuth: [1])
        var reads = 0
        let userID = userID
        inspector.onMetadata = { _ in
            reads += 1
            return CodexProcessMetadata(userID: userID, status: UInt32(SRUN),
                                        command: reads == 1 ? "chrome-native-host" : "codex")
        }
        try withAuthFile { auth in
            assertGateError(.codexRunning, inspector: inspector, authFile: auth)
        }
        XCTAssertEqual(reads, 2)
    }

    func testExitDuringPathLookupDoesNotBecomeAnInspectionFailure() throws {
        var inspector = fixture(commands: ["codex"])
        var reads = 0
        let entry = inspector.entries[1]
        inspector.onMetadata = { _ in reads += 1; return reads == 1 ? entry : nil }
        inspector.alive = []
        XCTAssertNoThrow(try CodexProcessGate.requireStopped(using: inspector, userID: userID))
        XCTAssertEqual(reads, 2)
    }

    func testEnumerationFailureDoesNotAllowSwitching() {
        var inspector = fixture(commands: [])
        inspector.listError = .processInspectionFailed
        assertGateError(.processInspectionFailed, inspector: inspector)
    }

    func testLiveKernelMetadataFallbackIdentifiesTheTestProcess() throws {
        let inspector = SystemCodexProcessInspector()
        let kernel = try XCTUnwrap(inspector.kernelMetadata(for: getpid()))
        let primary = try XCTUnwrap(inspector.metadata(for: getpid()))
        XCTAssertEqual(kernel.userID, getuid())
        XCTAssertEqual(kernel.userID, primary.userID)
        XCTAssertFalse(kernel.command.isEmpty)
        XCTAssertNotEqual(kernel.status, UInt32(SZOMB))
        XCTAssertTrue(inspector.isAlive(getpid()))
        XCTAssertNotNil(inspector.executablePath(for: getpid()))
    }

    func testSystemInspectorDetectsAnOpenFileByDeviceAndInode() throws {
        let inspector = SystemCodexProcessInspector()
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("coderim-holds-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let held = directory.appendingPathComponent("auth.json")
        let other = directory.appendingPathComponent("other.json")
        XCTAssertTrue(FileManager.default.createFile(atPath: held.path, contents: Data("{}".utf8)))
        XCTAssertTrue(FileManager.default.createFile(atPath: other.path, contents: Data("{}".utf8)))

        let descriptor = open(held.path, O_RDONLY)
        try XCTUnwrap(descriptor >= 0 ? descriptor : nil)
        defer { close(descriptor) }

        var info = stat()
        XCTAssertEqual(stat(held.path, &info), 0)
        let heldIdentity = CodexFileIdentity(device: UInt64(UInt32(bitPattern: info.st_dev)), inode: info.st_ino)
        XCTAssertEqual(stat(other.path, &info), 0)
        let otherIdentity = CodexFileIdentity(device: UInt64(UInt32(bitPattern: info.st_dev)), inode: info.st_ino)

        XCTAssertEqual(inspector.holdsFile(getpid(), identity: heldIdentity), true)
        XCTAssertEqual(inspector.holdsFile(getpid(), identity: otherIdentity), false)
    }

    func testMissingKernelProcessHasNoMetadata() {
        let inspector = SystemCodexProcessInspector()
        XCTAssertNil(inspector.kernelMetadata(for: Int32.max))
        XCTAssertFalse(inspector.isAlive(Int32.max))
    }

    private func fixture(commands: [String], holdsAuth: Set<pid_t> = []) -> ProcessInspectorFixture {
        let pids = commands.indices.map { pid_t($0 + 1) }
        return ProcessInspectorFixture(ids: pids, entries: Dictionary(uniqueKeysWithValues:
            zip(pids, commands).map { ($0, CodexProcessMetadata(userID: userID, status: UInt32(SRUN), command: $1)) }
        ), alive: Set(pids), holdsAuth: holdsAuth)
    }

    private func withAuthFile(_ body: (URL) throws -> Void) throws {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("coderim-auth-\(UUID().uuidString).json")
        XCTAssertTrue(FileManager.default.createFile(atPath: url.path, contents: Data("{}".utf8)))
        defer { try? FileManager.default.removeItem(at: url) }
        try body(url)
    }

    private func assertGateError(_ error: AccountSwitchError, inspector: ProcessInspectorFixture,
                                 authFile: URL = CodexProcessGate.defaultAuthFile,
                                 file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertThrowsError(try CodexProcessGate.requireStopped(using: inspector, userID: userID, authFile: authFile),
                             file: file, line: line) {
            XCTAssertEqual($0 as? AccountSwitchError, error, file: file, line: line)
        }
    }
}

private struct ProcessInspectorFixture: CodexProcessInspecting {
    var ids: [pid_t]
    var entries: [pid_t: CodexProcessMetadata]
    var alive: Set<pid_t>
    var holdsAuth: Set<pid_t> = []
    var paths: [pid_t: String] = [:]
    var listError: AccountSwitchError?
    var onMetadata: ((pid_t) -> CodexProcessMetadata?)?
    var onPath: ((pid_t) -> String?)?
    var holdsFileResult: ((pid_t) -> Bool?)?

    func processIDs() throws -> [pid_t] {
        if let listError { throw listError }
        return ids
    }
    func metadata(for pid: pid_t) -> CodexProcessMetadata? {
        if let onMetadata { return onMetadata(pid) }
        return entries[pid]
    }
    func executablePath(for pid: pid_t) -> String? {
        if let onPath { return onPath(pid) }
        return paths[pid]
    }
    func isAlive(_ pid: pid_t) -> Bool { alive.contains(pid) }
    func holdsFile(_ pid: pid_t, identity: CodexFileIdentity) -> Bool? {
        if let holdsFileResult { return holdsFileResult(pid) }
        return holdsAuth.contains(pid)
    }
}
