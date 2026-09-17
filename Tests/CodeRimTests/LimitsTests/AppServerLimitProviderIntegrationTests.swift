import XCTest
@testable import CodeRim

final class AppServerLimitProviderIntegrationTests: XCTestCase {
    func testReadsLimitsFromInstalledSignedCodexAppServer() async throws {
        try XCTSkipUnless(
            ProcessInfo.processInfo.environment["CODERIM_RUN_APP_SERVER_TESTS"] == "1",
            "Set CODERIM_RUN_APP_SERVER_TESTS=1 to probe the installed signed Codex app-server."
        )

        let snapshot = try await AppServerLimitProvider().readLimits()
        XCTAssertFalse(snapshot.windows.isEmpty)
        XCTAssertTrue(snapshot.windows.allSatisfy { (0...100).contains($0.remainingPercent) })
    }
}
