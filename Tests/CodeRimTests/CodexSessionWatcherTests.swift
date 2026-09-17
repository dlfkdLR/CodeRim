import Foundation
import XCTest
@testable import CodeRim

final class CodexSessionWatcherTests: XCTestCase {
    func testWatcherEmitsWhenANestedSessionFileChanges() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)

        let watcher = CodexSessionWatcher(roots: [root])
        let event = expectation(description: "FSEvents notification")
        let eventTask = Task {
            for await _ in watcher.events {
                event.fulfill()
                return
            }
        }
        watcher.start()
        defer {
            watcher.stop()
            eventTask.cancel()
        }

        try await Task.sleep(for: .milliseconds(100))
        let sessions = root.appendingPathComponent("sessions", isDirectory: true)
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        try Data("{}\n".utf8).write(to: sessions.appendingPathComponent("session.jsonl"))

        await fulfillment(of: [event], timeout: 5)
    }

    func testStoppingWatcherFinishesItsEventStream() async {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)

        let watcher = CodexSessionWatcher(roots: [root])
        watcher.start()
        let finished = expectation(description: "event stream finished")
        let eventTask = Task {
            for await _ in watcher.events {}
            finished.fulfill()
        }

        watcher.stop()
        await fulfillment(of: [finished], timeout: 2)
        eventTask.cancel()
    }
}
