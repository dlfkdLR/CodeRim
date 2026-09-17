import Foundation
import XCTest
@testable import CodexMeter

final class UsageDisplayPolicyTests: XCTestCase {
    func testEnablingAccountTotalsDoesNotOverrideAnyLocalPeriod() {
        for period in UsagePeriod.allCases {
            XCTAssertEqual(total(period, .local, local, profile), local.totals(for: period).totalTokens)
        }
    }

    func testAccountPeriodsDoNotSupplementServerTotalsWithUnattributedLocalUsage() {
        XCTAssertEqual(total(.week, .account, local, profile), 500)
        XCTAssertEqual(total(.month, .account, local, profile), 1_000)
        XCTAssertEqual(total(.allTime, .account, local, profile), 2_000)
    }

    func testMissingAccountSnapshotNeverFallsBackToLocalTotals() {
        for period in UsagePeriod.allCases {
            XCTAssertNil(total(period, .account, local, nil))
            XCTAssertEqual(total(period, .local, local, nil), local.totals(for: period).totalTokens)
        }
        XCTAssertEqual(UsageDisplayPolicy.historyContext(scope: .account, profileSnapshot: nil), "Unavailable")
    }

    func testDelayedProfileTodayBucketIsNotPresentedAsCurrentAccountToday() {
        // Profile.today is the server's statsAsOf bucket, not necessarily today.
        XCTAssertNil(total(.today, .account, local, profile))
        XCTAssertEqual(total(.today, .local, local, profile), 120)
    }

    func testAccountHistoryIsAvailableWithoutLocalLogsAndDoesNotPopulateLocalHistory() {
        XCTAssertEqual(total(.allTime, .account, .empty, profile), 2_000)
        XCTAssertEqual(total(.week, .local, .empty, profile), 0)
        XCTAssertEqual(total(.month, .local, .empty, profile), 0)
        XCTAssertEqual(total(.allTime, .local, .empty, profile), 0)
    }

    func testServerCatchUpOnlyReplacesTheAccountValues() {
        let refreshed = ProfileUsageSnapshot(today: 20, week: 520, month: 1_020, lifetime: 2_020,
                                             statsAsOf: profile.statsAsOf.addingTimeInterval(86_400),
                                             generatedAt: profile.generatedAt.addingTimeInterval(86_400))
        XCTAssertEqual(total(.week, .account, local, refreshed), 520)
        XCTAssertEqual(total(.month, .account, local, refreshed), 1_020)
        XCTAssertEqual(total(.allTime, .account, local, refreshed), 2_020)
        for period in UsagePeriod.allCases {
            XCTAssertEqual(total(period, .local, local, refreshed), total(period, .local, local, profile))
        }
    }

    func testLargeServerTotalIsPreservedWithoutAnOverflowingLocalSupplement() {
        let large = ProfileUsageSnapshot(today: 0, week: 500, month: 1_000, lifetime: Int64.max - 10,
                                        statsAsOf: profile.statsAsOf, generatedAt: profile.generatedAt)
        XCTAssertEqual(total(.allTime, .account, local, large), Int64.max - 10)
    }

    func testHistoryContextIdentifiesSourceAndServerCutoff() {
        XCTAssertEqual(UsageDisplayPolicy.historyContext(scope: .local, profileSnapshot: profile), "This Mac")
        let context = UsageDisplayPolicy.historyContext(scope: .account, profileSnapshot: profile)
        XCTAssertEqual(context, "Through \(profile.statsAsOf.formatted(.dateTime.month(.abbreviated).day()))")
    }

    func testLocalAndAccountNavigationStayDistinctWhenProfileAvailabilityChanges() {
        let local = MenuDestination.period(.allTime)
        let account = MenuDestination.period(.allTime, scope: .account)
        XCTAssertNotEqual(local, account)
        for enabled in [false, true] {
            XCTAssertEqual(local.title(usesProfileTotals: enabled), "Local History")
            XCTAssertEqual(account.title(usesProfileTotals: enabled), "Lifetime")
        }
    }

    private func total(_ period: UsagePeriod, _ scope: UsageHistoryScope,
                       _ local: UsageSnapshot, _ profile: ProfileUsageSnapshot?) -> Int64? {
        UsageDisplayPolicy.displayedTotal(for: period, scope: scope,
                                         localSnapshot: local, profileSnapshot: profile)
    }

    private var local: UsageSnapshot {
        UsageSnapshot(today: usage(120), week: usage(240), month: usage(480), allTime: usage(960),
                      quality: .exact, updatedAt: Date())
    }

    private var profile: ProfileUsageSnapshot {
        ProfileUsageSnapshot(today: 300, week: 500, month: 1_000, lifetime: 2_000,
                             statsAsOf: Date(timeIntervalSince1970: 1_789_225_200),
                             generatedAt: Date(timeIntervalSince1970: 1_789_228_800))
    }

    private func usage(_ total: Int64) -> TokenUsage {
        TokenUsage(inputTokens: total, cachedInputTokens: total / 2, outputTokens: 0)
    }
}
