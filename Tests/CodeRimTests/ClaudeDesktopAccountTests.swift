import XCTest
@testable import CodeRim

final class ClaudeDesktopAccountTests: XCTestCase {
    private func config(_ json: String) throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("claude-desktop-\(UUID().uuidString).json")
        try Data(json.utf8).write(to: url)
        addTeardownBlock { try? FileManager.default.removeItem(at: url) }
        return url
    }

    func testReadsOnlyTheRecordedAccountID() throws {
        let url = try config(#"{"oauth:tokenCacheV2":"opaque","lastKnownAccountUuid":"AAA-1"}"#)
        XCTAssertEqual(ClaudeDesktopAccount.accountID(configuration: url), "AAA-1")
        XCTAssertFalse(ClaudeDesktopAccount.differs(from: "aaa-1", configuration: url))
        XCTAssertTrue(ClaudeDesktopAccount.differs(from: "BBB-2", configuration: url))
    }

    func testUnknownDesktopIsNeverReportedAsDifferent() throws {
        let missing = FileManager.default.temporaryDirectory.appendingPathComponent("absent-\(UUID().uuidString).json")
        XCTAssertNil(ClaudeDesktopAccount.accountID(configuration: missing))
        XCTAssertFalse(ClaudeDesktopAccount.differs(from: "BBB-2", configuration: missing))
        XCTAssertFalse(ClaudeDesktopAccount.differs(from: "BBB-2", configuration: try config(#"{"locale":"en"}"#)))
    }
}
