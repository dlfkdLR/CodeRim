import XCTest
@testable import CodeRim

final class ClaudeCompactionHookTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("hook-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    private var hook: ClaudeCompactionHook {
        ClaudeCompactionHook(settings: root.appendingPathComponent(".claude/settings.json"),
                             script: root.appendingPathComponent(".coderim/hooks/compaction.sh"),
                             markers: root.appendingPathComponent(".coderim/compacting"))
    }

    private func writeSettings(_ text: String) throws {
        try FileManager.default.createDirectory(at: hook.settings.deletingLastPathComponent(),
                                                withIntermediateDirectories: true)
        try text.write(to: hook.settings, atomically: true, encoding: .utf8)
    }

    private func settings() throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: hook.settings)) as? [String: Any])
    }

    func testInstallKeepsEverythingElseAndBacksUp() throws {
        try writeSettings(#"{"model":"opus","hooks":{"PreCompact":[{"hooks":[{"type":"command","command":"echo mine"}]}],"Stop":[{"hooks":[{"type":"command","command":"say done"}]}]}}"#)
        try hook.install()
        XCTAssertTrue(hook.isInstalled)
        let result = try settings()
        XCTAssertEqual(result["model"] as? String, "opus")
        let hooks = try XCTUnwrap(result["hooks"] as? [String: Any])
        XCTAssertNotNil(hooks["Stop"])
        XCTAssertEqual((hooks["PreCompact"] as? [Any])?.count, 2, "the user's own hook must survive")
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: hook.settings.appendingPathExtension("coderim-backup").path))
    }

    func testInstallIsIdempotent() throws {
        try hook.install()
        try hook.install()
        let hooks = try XCTUnwrap(settings()["hooks"] as? [String: Any])
        XCTAssertEqual((hooks["PreCompact"] as? [Any])?.count, 1)
        XCTAssertEqual((hooks["PostCompact"] as? [Any])?.count, 1)
    }

    func testUninstallRemovesOnlyOurEntries() throws {
        try writeSettings(#"{"hooks":{"PreCompact":[{"hooks":[{"type":"command","command":"echo mine"}]}]}}"#)
        try hook.install()
        try hook.uninstall()
        XCTAssertFalse(hook.isInstalled)
        let hooks = try XCTUnwrap(settings()["hooks"] as? [String: Any])
        XCTAssertEqual((hooks["PreCompact"] as? [Any])?.count, 1)
        XCTAssertNil(hooks["PostCompact"])
        XCTAssertFalse(FileManager.default.fileExists(atPath: hook.script.path))
    }

    func testUnreadableSettingsAreNeverOverwritten() throws {
        try writeSettings("{ not json")
        XCTAssertThrowsError(try hook.install()) { error in
            XCTAssertEqual(error as? ClaudeCompactionHook.Failure, .unreadableSettings)
        }
        XCTAssertEqual(try String(contentsOf: hook.settings, encoding: .utf8), "{ not json")
    }

    func testMissingSettingsFileIsCreated() throws {
        try hook.install()
        XCTAssertTrue(hook.isInstalled)
    }

    func testTheScriptMarksAndClearsASession() throws {
        try hook.install()
        func run(_ argument: String, input: String) throws {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/bin/sh")
            process.arguments = [hook.script.path, argument]
            process.environment = ["HOME": root.path, "PATH": "/usr/bin:/bin"]
            let pipe = Pipe()
            process.standardInput = pipe
            try process.run()
            pipe.fileHandleForWriting.write(Data(input.utf8))
            try pipe.fileHandleForWriting.close()
            process.waitUntilExit()
            XCTAssertEqual(process.terminationStatus, 0)
        }
        try run("pre", input: #"{"session_id":"abc-123","hook_event_name":"PreCompact","trigger":"auto"}"#)
        XCTAssertEqual(hook.compactingSessionIDs(), ["abc-123"])
        try run("post", input: #"{"session_id":"abc-123","hook_event_name":"PostCompact"}"#)
        XCTAssertTrue(hook.compactingSessionIDs().isEmpty)
    }

    func testTheScriptRefusesAnIdThatCouldEscapeTheDirectory() throws {
        try hook.install()
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = [hook.script.path, "pre"]
        process.environment = ["HOME": root.path, "PATH": "/usr/bin:/bin"]
        let pipe = Pipe()
        process.standardInput = pipe
        try process.run()
        pipe.fileHandleForWriting.write(Data(#"{"session_id":"../../evil"}"#.utf8))
        try pipe.fileHandleForWriting.close()
        process.waitUntilExit()
        XCTAssertTrue(hook.compactingSessionIDs().isEmpty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: root.appendingPathComponent("evil").path))
    }

    func testStaleMarkersAreIgnored() throws {
        try FileManager.default.createDirectory(at: hook.markers, withIntermediateDirectories: true)
        FileManager.default.createFile(atPath: hook.markers.appendingPathComponent("old").path, contents: nil)
        XCTAssertEqual(hook.compactingSessionIDs(), ["old"])
        XCTAssertTrue(hook.compactingSessionIDs(now: Date().addingTimeInterval(3_600)).isEmpty)
    }
}
