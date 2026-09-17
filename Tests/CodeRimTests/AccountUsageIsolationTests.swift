import Foundation
import XCTest
@testable import CodeRim

final class AccountUsageIsolationTests: XCTestCase {
    func testSwitchingProfilesDoesNotAssignMixedLocalHistoryToEitherAccount() throws {
        let now = try date("2026-09-14T12:00:00+09:00")
        // The source sessions belong to different accounts, but the local data
        // model cannot retain or filter by that ownership.
        let local = try localSnapshot([
            ("account-a-session", "2026-09-14T09:00:00+09:00", 100),
            ("account-b-session", "2026-09-14T11:00:00+09:00", 20)
        ], now: now)

        for cutoff in ["2026-09-13T00:00:00+09:00", "2026-09-14T00:00:00+09:00"] {
            let through = try date(cutoff)
            let accountA = ProfileUsageSnapshot(today: 0, week: 300, month: 500, lifetime: 2_000,
                                                statsAsOf: through, generatedAt: now)
            let accountB = ProfileUsageSnapshot(today: 0, week: 0, month: 1_000, lifetime: 1_000,
                                                statsAsOf: through, generatedAt: now)
            // A -> B -> A must only replace the account snapshot. Local history
            // stays visible as This Mac; no unknown portion is assigned to A or B.
            for account in [accountA, accountB, accountA] {
                XCTAssertEqual(total(.week, local, account), account.week, cutoff)
                XCTAssertEqual(total(.month, local, account), account.month, cutoff)
                XCTAssertEqual(total(.allTime, local, account), account.lifetime, cutoff)
                XCTAssertEqual(total(.today, local, account), 120)
            }
            XCTAssertEqual(total(.allTime, local, nil), 120)
        }
    }

    func testLocalAppendIsLiveWithoutChangingAccountTotalsBeforeServerRefresh() throws {
        let now = try date("2026-09-14T12:00:00+09:00")
        let first = try localSnapshot([
            ("account-a-session", "2026-09-14T09:00:00+09:00", 100)
        ], now: now)
        let next = try localSnapshot([
            ("account-a-session", "2026-09-14T09:00:00+09:00", 100),
            ("account-b-session", "2026-09-14T11:00:00+09:00", 20)
        ], now: now)
        let profile = ProfileUsageSnapshot(today: 0, week: 0, month: 1_000, lifetime: 1_000,
                                            statsAsOf: try date("2026-09-13T00:00:00+09:00"),
                                            generatedAt: now)
        for period in [UsagePeriod.week, .month, .allTime] {
            XCTAssertEqual(total(period, first, profile), total(period, next, profile))
            XCTAssertEqual(total(period, next, nil) - total(period, first, nil), 20)
        }
        XCTAssertEqual(total(.today, next, profile) - total(.today, first, profile), 20)
    }

    private func localSnapshot(_ values: [(String, String, Int64)], now: Date) throws -> UsageSnapshot {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "Asia/Seoul"))
        let events = try values.enumerated().map { index, value in
            UsageEvent(eventKey: value.0, occurredAt: try date(value.1), sessionID: value.0,
                       model: nil, projectPath: nil,
                       usage: TokenUsage(inputTokens: value.2, cachedInputTokens: 0, outputTokens: 0),
                       sourcePath: "/fixture/\(value.0)", sourcePosition: Int64(index))
        }
        return AggregationService().snapshot(from: events, now: now, calendar: calendar, weekStart: .monday)
    }

    private func total(_ period: UsagePeriod, _ local: UsageSnapshot, _ profile: ProfileUsageSnapshot?) -> Int64 {
        UsageDisplayPolicy.displayedTotal(for: period,
            scope: profile == nil || period == .today ? .local : .account,
            localSnapshot: local, profileSnapshot: profile)!
    }

    private func date(_ value: String) throws -> Date {
        try XCTUnwrap(ISO8601DateFormatter().date(from: value))
    }
}

@MainActor
final class AccountProfileIsolationTests: XCTestCase {
    func testLiveUsageModeIgnoresSavedProfileSyncWithoutFetching() async throws {
        let suite = "CodeRim.LiveUsage.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(true, forKey: ProfileUsageStore.enabledPreferenceKey)
        let store = ProfileUsageStore(defaults: defaults, allowsAccountTotals: false) { _, _, _ in
            XCTFail("Live usage must not request delayed account totals")
            throw ProfileUsageError.transportFailure
        }
        store.synchronizeEnabledPreference()
        await store.refresh()
        XCTAssertFalse(store.isEnabled)
        XCTAssertEqual(store.status, .disabled)
        XCTAssertNil(store.snapshot)
        XCTAssertTrue(defaults.bool(forKey: ProfileUsageStore.enabledPreferenceKey),
                      "Do not rewrite an existing preference just to select live usage")
    }

    func testSwitchClearsPreviousAccountEvenWhenTheNextFetchFails() async throws {
        let suite = "CodeRim.AccountIsolation.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(true, forKey: ProfileUsageStore.enabledPreferenceKey)
        let first = profile(total: 2_000)
        let probe = AccountProfileFetchProbe(first: first, next: .failure(.transportFailure))
        let store = ProfileUsageStore(defaults: defaults) { _, _, _ in try await probe.fetch() }

        await store.refresh()
        XCTAssertEqual(store.snapshot, first)
        store.clearForAccountSwitch()
        XCTAssertNil(store.snapshot)
        await store.refresh()
        XCTAssertEqual(store.status, .unavailable)
        XCTAssertNil(store.snapshot, "The departing account must not become the fallback for the new account")
    }

    func testLateResponseFromDepartingAccountCannotReturnAfterSwitch() async throws {
        let suite = "CodeRim.AccountIsolation.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(true, forKey: ProfileUsageStore.enabledPreferenceKey)
        let accountA = profile(total: 2_000)
        let accountB = profile(total: 1_000)
        let probe = AccountProfileFetchProbe(first: accountA, next: .success(accountB), suspendFirst: true)
        let store = ProfileUsageStore(defaults: defaults) { _, _, _ in try await probe.fetch() }
        let departingRefresh = Task { await store.refresh() }
        for _ in 0..<100 {
            if await probe.isSuspended() { break }
            try await Task.sleep(for: .milliseconds(5))
        }
        let suspended = await probe.isSuspended()
        XCTAssertTrue(suspended)

        store.clearForAccountSwitch()
        XCTAssertNil(store.snapshot)
        // Deliberately finish despite cancellation, as a late network callback can.
        await probe.resumeFirst()
        await departingRefresh.value
        XCTAssertNil(store.snapshot)
        XCTAssertFalse(store.isRefreshing)
        await store.refresh()
        XCTAssertEqual(store.snapshot, accountB)
        XCTAssertEqual(store.status, .ready)
    }

    private func profile(total: Int64) -> ProfileUsageSnapshot {
        ProfileUsageSnapshot(today: 0, week: total, month: total, lifetime: total,
                             statsAsOf: Date(), generatedAt: Date())
    }
}

private actor AccountProfileFetchProbe {
    private let first: ProfileUsageSnapshot
    private let next: Result<ProfileUsageSnapshot, ProfileUsageError>
    private let suspendFirst: Bool
    private var count = 0
    private var continuation: CheckedContinuation<Void, Never>?

    init(first: ProfileUsageSnapshot, next: Result<ProfileUsageSnapshot, ProfileUsageError>, suspendFirst: Bool = false) {
        self.first = first
        self.next = next
        self.suspendFirst = suspendFirst
    }

    func fetch() async throws -> ProfileUsageSnapshot {
        count += 1
        if count == 1 {
            if suspendFirst {
                await withCheckedContinuation { continuation = $0 }
            }
            return first
        }
        return try next.get()
    }

    func isSuspended() -> Bool { continuation != nil }

    func resumeFirst() {
        continuation?.resume()
        continuation = nil
    }
}
