import XCTest
@testable import CodeRim

final class StaleStatusMessageTests: XCTestCase {
    func testNeverReadKeepsWaitingCopy() {
        XCTAssertEqual(ProviderSnapshot.staleMessage(since: .distantPast),
                       "Waiting for the first reading…")
    }

    func testOldReadingSaysHowOldAndThatRefreshFailed() {
        let now = Date()
        XCTAssertEqual(ProviderSnapshot.staleMessage(since: now.addingTimeInterval(-12 * 60), now: now),
                       "Last read 12 min ago · refresh failed")
    }

    func testVeryRecentReadingDoesNotSayZeroMinutes() {
        let now = Date()
        XCTAssertEqual(ProviderSnapshot.staleMessage(since: now.addingTimeInterval(-5), now: now),
                       "Last read just now · refresh failed")
    }
}
