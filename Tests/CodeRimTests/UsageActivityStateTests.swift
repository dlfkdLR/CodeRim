import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class UsageActivityStateTests: XCTestCase {
    func testMountedAnalyticsReloadsActivityWhenSameRangeUsageRefreshes() async throws {
        _ = NSApplication.shared
        let store = isolatedLayoutUsageStore()
        let state = SettingsUsageAnalyticsState()
        let navigation = MenuNavigation()
        navigation.usageRange = .sevenDays
        let firstLoad = expectation(description: "Initial activity load")
        var nextLoad: XCTestExpectation? = firstLoad
        var loads: [Date] = []
        var previousThrough: Date?
        state.loadActivity = { _, range, through, _ in
            loads.append(through)
            if through != previousThrough {
                nextLoad?.fulfill()
                nextLoad = nil
            }
            let days = [through]
            return ActivitySnapshot(range: range, messages: .empty(days: days),
                                    tools: .build(days: days, counts: [through: ["Bash": loads.count]]),
                                    skills: .empty(days: days), traits: [:])
        }
        await store.refreshAnalytics(range: .sevenDays)
        previousThrough = store.analyticsSnapshot(for: .sevenDays)?.through
        let host = NSHostingView(rootView: UsageSettingsAnalytics()
            .environmentObject(store).environmentObject(navigation).environmentObject(state))
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 640, height: 900),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.orderFront(nil)
        defer { window.orderOut(nil); window.close() }
        await fulfillment(of: [firstLoad], timeout: 5)
        let previousCount = loads.count
        previousThrough = store.analyticsSnapshot(for: .sevenDays)?.through
        let refreshed = expectation(description: "Same provider and range reload activity")
        nextLoad = refreshed
        await store.refreshAnalytics(range: .sevenDays)
        await fulfillment(of: [refreshed], timeout: 5)
        XCTAssertNotEqual(store.analyticsSnapshot(for: .sevenDays)?.through, previousThrough)
        XCTAssertGreaterThan(loads.count, previousCount)
        XCTAssertEqual(state.activityKey?.through, store.analyticsSnapshot(for: .sevenDays)?.through)
        XCTAssertEqual(state.activity?.tools.total, loads.count)
    }

    func testActivityIdentityChangesAtDayAndCalendarRollover() {
        var utc = Calendar(identifier: .gregorian)
        utc.timeZone = TimeZone(identifier: "UTC")!
        var seoul = utc
        seoul.timeZone = TimeZone(identifier: "Asia/Seoul")!
        let now = Date(timeIntervalSince1970: 1_790_496_000)
        func key(_ date: Date, _ calendar: Calendar) -> SettingsUsageActivityKey {
            .init(provider: .claude, range: .sevenDays, through: date, sourceUpdatedAt: now, calendar: calendar)
        }
        XCTAssertNotEqual(key(now, utc), key(now.addingTimeInterval(86_400), utc))
        XCTAssertNotEqual(key(now, utc), key(now, seoul))
        XCTAssertNotEqual(key(now, utc), .init(provider: .claude, range: .sevenDays, through: now,
                                               sourceUpdatedAt: now.addingTimeInterval(1), calendar: utc))
    }

    func testAlreadyCancelledLoadCannotTakeOwnershipFromSuspendedCurrentLoad() async {
        let state = SettingsUsageAnalyticsState()
        let now = Date(timeIntervalSince1970: 1_790_496_000)
        let currentKey = SettingsUsageActivityKey(provider: .claude, range: .thirtyDays, through: now,
                                                  sourceUpdatedAt: now, calendar: .current)
        let staleKey = SettingsUsageActivityKey(provider: .claude, range: .sevenDays, through: now,
                                                sourceUpdatedAt: now, calendar: .current)
        let started = expectation(description: "Current activity suspended")
        var resumeCurrent: CheckedContinuation<ActivitySnapshot, Never>?
        var staleLoads = 0
        state.loadActivity = { _, range, _, _ in
            if range == .thirtyDays {
                return await withCheckedContinuation { continuation in
                    resumeCurrent = continuation
                    started.fulfill()
                }
            }
            staleLoads += 1
            return .empty(range: range)
        }
        let current = Task { await state.refreshActivity(for: currentKey) }
        await fulfillment(of: [started], timeout: 5)
        let stale = Task { await state.refreshActivity(for: staleKey) }
        stale.cancel()
        await stale.value
        XCTAssertEqual(staleLoads, 0)
        resumeCurrent?.resume(returning: .empty(range: .thirtyDays))
        await current.value
        XCTAssertEqual(state.activityKey, currentKey)
        XCTAssertEqual(state.activity?.range, .thirtyDays)
    }

    func testObsoleteActivityLoadCannotReplaceNewerResultEvenWithoutCancellation() async {
        let state = SettingsUsageAnalyticsState()
        let now = Date(timeIntervalSince1970: 1_790_496_000)
        let oldKey = SettingsUsageActivityKey(provider: .claude, range: .sevenDays, through: now,
                                              sourceUpdatedAt: now, calendar: .current)
        let newKey = SettingsUsageActivityKey(provider: .claude, range: .thirtyDays, through: now,
                                              sourceUpdatedAt: now, calendar: .current)
        let started = expectation(description: "Old activity suspended")
        var resumeOld: CheckedContinuation<ActivitySnapshot, Never>?
        state.loadActivity = { _, range, _, _ in
            if range == .sevenDays {
                return await withCheckedContinuation { continuation in
                    resumeOld = continuation
                    started.fulfill()
                }
            }
            return .empty(range: range)
        }
        let old = Task { await state.refreshActivity(for: oldKey) }
        await fulfillment(of: [started], timeout: 5)
        await state.refreshActivity(for: newKey)
        XCTAssertEqual(state.activityKey, newKey)
        resumeOld?.resume(returning: .empty(range: .sevenDays))
        await old.value
        XCTAssertEqual(state.activityKey, newKey)
        XCTAssertEqual(state.activity?.range, .thirtyDays)
    }
}
