import XCTest
@testable import CodeRim

final class SessionFocusScriptTests: XCTestCase {
    func testQuotingKeepsHostileTextInsideTheLiteral() {
        XCTAssertEqual(SessionFocus.quoted(#"a "b" \c"#), #""a \"b\" \\c""#)
    }

    func testTerminalAndITermMatchByTTY() throws {
        for id in ["com.apple.Terminal", "com.googlecode.iterm2"] {
            let script = try XCTUnwrap(SessionFocus.focusScript(bundleID: id, tty: "/dev/ttys003",
                                                                workingDirectory: nil, title: nil))
            XCTAssertTrue(script.contains(#"tty of"#) && script.contains(#""/dev/ttys003""#))
        }
        XCTAssertNil(SessionFocus.focusScript(bundleID: "com.apple.Terminal", tty: nil, workingDirectory: "/x", title: nil))
    }

    func testGhosttyMatchesWorkingDirectoryAndPrefersTheTitledTab() throws {
        let script = try XCTUnwrap(SessionFocus.focusScript(bundleID: "com.mitchellh.ghostty", tty: nil,
            workingDirectory: "/Users/me/My \"App\"", title: "전체 코드 리뷰"))
        XCTAssertTrue(script.contains(#"working directory is "/Users/me/My \"App\"""#))
        XCTAssertTrue(script.contains(#"contains "전체 코드 리뷰""#))
        XCTAssertTrue(script.contains("focus chosen"))
    }

    func testOtherAppsAreOnlyRaised() {
        XCTAssertNil(SessionFocus.focusScript(bundleID: "dev.warp.Warp-Stable", tty: "/dev/ttys001",
                                              workingDirectory: "/x", title: "t"))
    }

    func testFindsTheControllingTerminalOfAProcessThatHasNone() {
        // The test runner is not attached to a tty under `swift test` in CI;
        // either way the answer must be a /dev path or nil, never garbage.
        if let tty = SessionFocus.controllingTTY(of: getpid()) { XCTAssertTrue(tty.hasPrefix("/dev/")) }
    }
}
