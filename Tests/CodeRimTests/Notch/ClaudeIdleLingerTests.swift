import XCTest
@testable import CodeRim

final class ClaudeIdleLingerTests: XCTestCase {
    private func directory(status: String, updatedAt: Date) throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let started = Int(Date().timeIntervalSince1970 * 1000)
        let json = """
        {"pid":\(getpid()),"sessionId":"s1","cwd":"/tmp/p","startedAt":\(started),\
        "updatedAt":\(Int(updatedAt.timeIntervalSince1970 * 1000)),"status":"\(status)","kind":"interactive"}
        """
        try json.write(to: dir.appendingPathComponent("\(getpid()).json"), atomically: true, encoding: .utf8)
        return dir
    }

    func testAChatIdleForLongIsLeftOutOnlyWhenAsked() throws {
        let dir = try directory(status: "idle", updatedAt: Date().addingTimeInterval(-600))
        defer { try? FileManager.default.removeItem(at: dir) }
        XCTAssertEqual(ClaudeSessionMonitor.read(directory: dir).count, 1)
        XCTAssertEqual(ClaudeSessionMonitor.read(directory: dir, dropIdleAfter: 90).count, 0)
    }

    func testAChatThatJustFinishedOrIsWorkingStays() throws {
        let fresh = try directory(status: "idle", updatedAt: Date().addingTimeInterval(-10))
        let busy = try directory(status: "busy", updatedAt: Date().addingTimeInterval(-600))
        defer { try? FileManager.default.removeItem(at: fresh); try? FileManager.default.removeItem(at: busy) }
        XCTAssertEqual(ClaudeSessionMonitor.read(directory: fresh, dropIdleAfter: 90).count, 1)
        XCTAssertEqual(ClaudeSessionMonitor.read(directory: busy, dropIdleAfter: 90).count, 1)
    }
}
