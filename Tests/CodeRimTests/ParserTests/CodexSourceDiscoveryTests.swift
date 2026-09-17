import Foundation
import XCTest
@testable import CodeRim

final class CodexSourceDiscoveryTests: XCTestCase {
    func testSourceCountLimitFailsClosed() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("{}\n".utf8).write(to: root.appendingPathComponent("one.jsonl"))
        try Data("{}\n".utf8).write(to: root.appendingPathComponent("two.jsonl"))

        XCTAssertThrowsError(
            try CodexSourceDiscovery().discover(in: [root], maximumSourceCount: 1)
        ) { error in
            guard case CodexSourceDiscoveryError.sourceLimitExceeded(1) = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
    }
}
