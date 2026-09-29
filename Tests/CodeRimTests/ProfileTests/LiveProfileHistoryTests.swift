import Foundation
import XCTest
@testable import CodeRim

final class LiveProfileHistoryTests: XCTestCase {
    func testLiveReadOnlyProfileHistory() async throws {
        guard ProcessInfo.processInfo.environment["CODERIM_VERIFY_LIVE_PROFILE"] == "1" else {
            throw XCTSkip("Opt-in read-only check of the signed-in account")
        }
        let snapshot = try await ChatGPTProfileClient().fetch()
        XCTAssertNotNil(snapshot.accountKey)
        XCTAssertGreaterThanOrEqual(snapshot.lifetime, snapshot.month)
        XCTAssertGreaterThanOrEqual(snapshot.lifetime, snapshot.week)
        XCTAssertLessThanOrEqual(snapshot.statsAsOf, Date())
        print("LIVE_PROFILE week=\(snapshot.week) month=\(snapshot.month) lifetime=\(snapshot.lifetime) through=\(snapshot.statsAsOf.ISO8601Format())")
    }
}
