import Foundation
import XCTest
@testable import CodeRim

final class AccountLimitPaceTests: XCTestCase {
    func testAheadOfPaceProjectsExhaustionBeforeReset() throws {
        let now = Date(timeIntervalSince1970: 18_000)
        let window = makeWindow(usedPercent: 75, now: now)

        let pace = try XCTUnwrap(window.pace(at: now))

        XCTAssertEqual(pace.state, .ahead)
        XCTAssertEqual(pace.differencePercent, 25, accuracy: 0.001)
        XCTAssertEqual(pace.compactSummary, "25% above pace")
        XCTAssertNotNil(pace.projectedExhaustion)
        XCTAssertLessThan(try XCTUnwrap(pace.projectedExhaustion), try XCTUnwrap(window.resetsAt))
    }

    func testReservePaceDoesNotClaimEarlyExhaustion() throws {
        let now = Date(timeIntervalSince1970: 18_000)
        let window = makeWindow(usedPercent: 25, now: now)

        let pace = try XCTUnwrap(window.pace(at: now))

        XCTAssertEqual(pace.state, .reserve)
        XCTAssertEqual(pace.differencePercent, 25, accuracy: 0.001)
        XCTAssertEqual(pace.compactSummary, "25% below pace")
        XCTAssertNil(pace.projectedExhaustion)
    }

    func testPaceIsHiddenBeforeMeaningfulWindowProgress() {
        let duration: TimeInterval = 10 * 60 * 60
        let now = Date(timeIntervalSince1970: duration * 0.02)
        let window = AccountLimitWindow(
            id: "weekly",
            limitID: "codex",
            displayName: "Codex",
            windowDurationMinutes: 600,
            usedPercent: 1,
            resetsAt: Date(timeIntervalSince1970: duration)
        )

        XCTAssertNil(window.pace(at: now))
    }

    func testPaceIsUnavailableAfterReset() {
        let now = Date(timeIntervalSince1970: 36_001)
        let window = AccountLimitWindow(
            id: "weekly",
            limitID: "codex",
            displayName: "Codex",
            windowDurationMinutes: 600,
            usedPercent: 50,
            resetsAt: Date(timeIntervalSince1970: 36_000)
        )

        XCTAssertNil(window.pace(at: now))
    }

    func testUntrustedExtremeDurationFailsClosedWithoutOverflow() {
        let now = Date(timeIntervalSince1970: 18_000)
        let window = AccountLimitWindow(
            id: "extreme",
            limitID: "unknown",
            displayName: "Unknown",
            windowDurationMinutes: .max,
            usedPercent: 50,
            resetsAt: now.addingTimeInterval(18_000)
        )

        XCTAssertNil(window.pace(at: now))
    }

    func testOnlyReadySnapshotsAllowPaceEstimates() {
        XCTAssertTrue(AccountLimitStatus.ready.allowsPaceEstimates)
        XCTAssertFalse(AccountLimitStatus.loading.allowsPaceEstimates)
        XCTAssertFalse(AccountLimitStatus.stale.allowsPaceEstimates)
        XCTAssertFalse(AccountLimitStatus.unavailable.allowsPaceEstimates)
        XCTAssertFalse(AccountLimitStatus.disabled.allowsPaceEstimates)
    }

    private func makeWindow(usedPercent: Double, now: Date) -> AccountLimitWindow {
        AccountLimitWindow(
            id: "weekly",
            limitID: "codex",
            displayName: "Codex",
            windowDurationMinutes: 600,
            usedPercent: usedPercent,
            resetsAt: now.addingTimeInterval(18_000)
        )
    }
}
