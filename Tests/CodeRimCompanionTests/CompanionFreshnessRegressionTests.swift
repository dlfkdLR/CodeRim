import CodeRimShared
import Foundation
import XCTest

final class CompanionFreshnessRegressionTests: XCTestCase {
    func testPartialLimitsAgeAndResetWithoutInventingAFreshReading() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        var limits = CompanionLimits(state: .partial, updatedAt: now, windows: [
            CompanionLimitWindow(id: "weekly", name: "Weekly", usedPercent: 50, resetsAt: now.addingTimeInterval(600))
        ], staleAfterSeconds: 300)
        XCTAssertEqual(limits.evaluated(at: now).state, .partial)
        XCTAssertEqual(limits.evaluated(at: now.addingTimeInterval(301)).state, .stale)
        limits.windows[0].resetsAt = now
        XCTAssertEqual(limits.evaluated(at: now).state, .stale)
        limits.windows[0].resetsAt = nil
        limits.updatedAt = nil
        XCTAssertEqual(limits.evaluated(at: now).state, .partial)
        limits.windows[0].resetsAt = now
        XCTAssertEqual(limits.evaluated(at: now).state, .stale)
        limits.windows[0].resetsAt = nil
        limits.updatedAt = now.addingTimeInterval(61)
        XCTAssertEqual(limits.evaluated(at: now).state, .stale)
    }

    func testUnreadableStatesKeepTheirAuthenticationAndSetupMeaning() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        for state in [CompanionState.loading, .disabled, .unavailable, .needsAuth, .accessDenied, .unsupported] {
            let limits = CompanionLimits(state: state, updatedAt: nil, windows: [])
            XCTAssertEqual(limits.evaluated(at: now).state, state)
        }
    }
}
