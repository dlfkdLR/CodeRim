import XCTest
@testable import CodeRim

final class AccountLimitPresentationTests: XCTestCase {
    func testPrimaryWeeklyComesFirstRegardlessOfResponseOrder() {
        let weekly = window("codex-weekly", provider: "codex", minutes: 10_080)
        let sparkShort = window("spark-short", provider: "spark", minutes: 300)
        let sparkWeekly = window("spark-weekly", provider: "spark", minutes: 10_080)
        for input in [[sparkShort, weekly, sparkWeekly], [sparkWeekly, sparkShort, weekly], [weekly, sparkWeekly, sparkShort]] {
            let result = AccountLimitPresentation.visibleWindows(input, includesAdditional: true)
            XCTAssertEqual(result, [weekly, sparkShort, sparkWeekly])
        }
    }

    func testWeeklyIsFirstWhenAdditionalLimitsAreHidden() {
        let weekly = window("codex-weekly", provider: "CoDeX", minutes: 10_080)
        let short = window("codex-short", provider: "codex", minutes: 300)
        let spark = window("spark-short", provider: "spark", minutes: 300)
        XCTAssertEqual(
            AccountLimitPresentation.visibleWindows([short, spark, weekly], includesAdditional: false),
            [weekly, short]
        )
    }

    func testMissingWeeklyKeepsOtherWindowsWithoutInventingOne() {
        let short = window("spark-short", provider: "spark", minutes: 300)
        let weekly = window("spark-weekly", provider: "spark", minutes: 10_080)
        XCTAssertEqual(AccountLimitPresentation.visibleWindows([weekly, short], includesAdditional: true), [short, weekly])
        XCTAssertTrue(AccountLimitPresentation.visibleWindows([weekly, short], includesAdditional: false).isEmpty)
    }

    private func window(_ id: String, provider: String, minutes: Int) -> AccountLimitWindow {
        AccountLimitWindow(id: id, limitID: provider, displayName: provider,
                           windowDurationMinutes: minutes, usedPercent: 12, resetsAt: nil)
    }
}
