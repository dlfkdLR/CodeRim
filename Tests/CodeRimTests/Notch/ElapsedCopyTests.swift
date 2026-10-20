import XCTest
@testable import CodeRim

final class NotchElapsedCopyTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_787_900_000)

    func testFreshChangesReadAsJustNow() {
        XCTAssertEqual(ElapsedCopy.text(since: now.addingTimeInterval(-5), now: now), "just now")
        XCTAssertEqual(ElapsedCopy.text(since: now.addingTimeInterval(-44), now: now), "just now")
    }

    func testMinutes() {
        XCTAssertEqual(ElapsedCopy.text(since: now.addingTimeInterval(-6 * 60), now: now), "6 min")
        XCTAssertEqual(ElapsedCopy.text(since: now.addingTimeInterval(-59 * 60), now: now), "59 min")
    }

    func testHours() {
        XCTAssertEqual(ElapsedCopy.text(since: now.addingTimeInterval(-60 * 60), now: now), "1 hr")
        XCTAssertEqual(ElapsedCopy.text(since: now.addingTimeInterval(-65 * 60), now: now), "1 hr 5 min")
    }

    func testDayHourMinuteBreakdownKeepsAllUnits() {
        let cases: [(Int, String)] = [
            (23 * 60 + 59, "23 hr 59 min"),
            (24 * 60, "1 d 0 hr 0 min"),
            (24 * 60 + 1, "1 d 0 hr 1 min"),
            (26 * 60 + 30, "1 d 2 hr 30 min"),
            (47 * 60 + 59, "1 d 23 hr 59 min"),
            (48 * 60, "2 d 0 hr 0 min"),
            (73 * 60 + 5, "3 d 1 hr 5 min")
        ]
        for (minutes, expected) in cases {
            let since = now.addingTimeInterval(-Double(minutes) * 60)
            XCTAssertEqual(ElapsedCopy.text(since: since, now: now), expected)
            XCTAssertEqual(ElapsedCopy.ago(since: since, now: now), expected + " ago")
        }
    }

    /// A clock that has drifted backwards must not print a negative age.
    func testFutureTimestampsDoNotGoNegative() {
        XCTAssertEqual(ElapsedCopy.text(since: now.addingTimeInterval(120), now: now), "just now")
    }
}
