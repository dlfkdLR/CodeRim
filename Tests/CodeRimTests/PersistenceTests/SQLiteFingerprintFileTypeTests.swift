import Darwin
import Foundation
import XCTest
@testable import CodeRim

private final class FingerprintReadCompletion: @unchecked Sendable {
    private let lock = NSLock()
    private var completed = false
    func finish() { lock.withLock { completed = true } }
    var isFinished: Bool { lock.withLock { completed } }
}

final class SQLiteFingerprintFileTypeTests: XCTestCase {
    func testExistingRegularFingerprintKeyIsPreserved() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("usage.sqlite")
        let keyURL = URL(fileURLWithPath: url.path + ".fingerprint-key")
        let original = Data(repeating: 0x5a, count: 32)
        try original.write(to: keyURL)
        let database = try SQLiteDatabase(url: url)
        let key = await database.sourceFingerprintKey()
        XCTAssertEqual(key, original)
        XCTAssertEqual(try Data(contentsOf: keyURL), original)
    }

    func testFIFOFingerprintKeyIsRejectedWithoutWaitingForAWriter() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("usage.sqlite")
        let keyURL = URL(fileURLWithPath: url.path + ".fingerprint-key")
        guard mkfifo(keyURL.path, 0o600) == 0 else {
            XCTFail("Unable to create the owned non-regular-file fixture."); return
        }
        let completion = FingerprintReadCompletion()
        let result = Task.detached { () -> String? in
            defer { completion.finish() }
            do { _ = try SQLiteDatabase(url: url); return nil }
            catch SQLiteDatabaseError.open(let message) { return message }
            catch { return "unexpected error type" }
        }
        // Always release a baseline blocking open so the regression cannot strand a test thread.
        var releaseFD: Int32 = -1
        defer { if releaseFD >= 0 { close(releaseFD) } }
        do { try await Task.sleep(for: .milliseconds(500)) }
        catch {
            releaseFD = Darwin.open(keyURL.path, O_RDWR | O_NONBLOCK | O_CLOEXEC)
            _ = await result.value
            throw error
        }
        let completedBeforeWriter = completion.isFinished
        releaseFD = Darwin.open(keyURL.path, O_RDWR | O_NONBLOCK | O_CLOEXEC)
        XCTAssertGreaterThanOrEqual(releaseFD, 0)
        let message = await result.value
        XCTAssertTrue(completedBeforeWriter, "Opening a non-regular fingerprint key waited for a writer.")
        XCTAssertEqual(message, "the source fingerprint key is invalid")
        var info = stat()
        XCTAssertEqual(lstat(keyURL.path, &info), 0)
        XCTAssertEqual(info.st_mode & S_IFMT, S_IFIFO)
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
    }
}
