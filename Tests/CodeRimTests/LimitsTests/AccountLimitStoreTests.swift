import Foundation
import XCTest
@testable import CodeRim

@MainActor
final class AccountLimitStoreTests: XCTestCase {
    func testOfflineFailureKeepsLastKnownSnapshotInMemory() async throws {
        let defaults = try makeDefaults()
        defaults.set(true, forKey: "accountLimitsEnabled")
        let expected = AccountLimitsSnapshot(
            windows: [
                AccountLimitWindow(
                    id: "weekly",
                    limitID: "codex",
                    displayName: "Codex",
                    windowDurationMinutes: 10_080,
                    usedPercent: 25,
                    resetsAt: nil
                )
            ],
            resetCredits: nil,
            fetchedAt: Date()
        )
        let provider = SequencedLimitProvider([
            .success(expected),
            .failure(AccountLimitError.timedOut)
        ])
        let store = AccountLimitStore(
            provider: provider,
            defaults: defaults,
            pollingInterval: nil
        )

        await store.refresh()
        XCTAssertEqual(store.snapshot, expected)
        XCTAssertEqual(store.status, .ready)

        await store.refresh()
        XCTAssertEqual(store.snapshot, expected)
        XCTAssertEqual(store.status, .stale)
        XCTAssertEqual(store.statusMessage, "Showing last known limits")
    }

    func testDisabledPreferenceNeverCallsProvider() async throws {
        let defaults = try makeDefaults()
        defaults.set(false, forKey: "accountLimitsEnabled")
        let provider = SequencedLimitProvider([])
        let store = AccountLimitStore(
            provider: provider,
            defaults: defaults,
            pollingInterval: nil
        )

        await store.refresh()
        XCTAssertEqual(store.status, .disabled)
        XCTAssertNil(store.snapshot)
        let calls = await provider.callCount
        XCTAssertEqual(calls, 0)
    }

    func testProviderPropagatesTimeoutWithoutLaunchingFallbackSource() async {
        let provider = AppServerLimitProvider(
            executableResolver: { URL(fileURLWithPath: "/trusted/codex") },
            runner: TimeoutRunner()
        )

        do {
            _ = try await provider.readLimits()
            XCTFail("Expected timeout")
        } catch {
            XCTAssertEqual(error as? AccountLimitError, .timedOut)
        }
    }

    func testUnrelatedDefaultsWriteDoesNotRestartThePoll() async throws {
        let defaults = try makeDefaults()
        defaults.set(true, forKey: "accountLimitsEnabled")
        let snapshot = AccountLimitsSnapshot(
            windows: [AccountLimitWindow(id: "w", limitID: "codex", displayName: "Codex",
                                         windowDurationMinutes: 10_080, usedPercent: 10, resetsAt: nil)],
            resetCredits: nil, fetchedAt: Date()
        )
        let provider = CountingLimitProvider(snapshot)
        let store = AccountLimitStore(provider: provider, defaults: defaults, pollingInterval: .seconds(120))
        // Let the initial poll's first read land.
        try await Task.sleep(for: .milliseconds(200))
        let baseline = await provider.callCount

        // A mute toggle / notch drag is a write to some other key.
        defaults.set("copilot", forKey: AppPreferences.mutedAlertProvidersKey)
        try await Task.sleep(for: .milliseconds(200))
        let afterUnrelated = await provider.callCount
        XCTAssertEqual(afterUnrelated, baseline, "an unrelated preference write must not trigger a fresh read")

        // Flipping the actual flag still resynchronises.
        defaults.set(false, forKey: "accountLimitsEnabled")
        try await Task.sleep(for: .milliseconds(200))
        XCTAssertEqual(store.status, .disabled)
    }

    func testOldSnapshotBecomesStaleAndActiveAppRefreshesIt() async throws {
        let snapshot = AccountLimitsSnapshot(windows: [], resetCredits: nil, fetchedAt: Date())
        let provider = CountingLimitProvider(snapshot)
        let store = AccountLimitStore(provider: provider, defaults: try makeDefaults(), pollingInterval: nil)
        await store.refresh()
        XCTAssertEqual(store.status, .ready)
        store.updateFreshness(now: snapshot.fetchedAt.addingTimeInterval(16 * 3600))
        XCTAssertEqual(store.status, .stale)
        XCTAssertFalse(store.status.allowsPaceEstimates)
        XCTAssertEqual(LimitFreshness.text(fetchedAt: snapshot.fetchedAt,
            now: snapshot.fetchedAt.addingTimeInterval(16 * 3600), stale: true), "Last known · updated 16 hr ago")
        await store.refreshIfNeeded(now: snapshot.fetchedAt.addingTimeInterval(120))
        let calls = await provider.callCount
        XCTAssertEqual(calls, 2)
        XCTAssertFalse(store.isRefreshing)
    }

    private func makeDefaults() throws -> UserDefaults {
        let name = "AccountLimitStoreTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defaults.removePersistentDomain(forName: name)
        return defaults
    }
}

private actor SequencedLimitProvider: AccountLimitProviding {
    private var outcomes: [Result<AccountLimitsSnapshot, Error>]
    private(set) var callCount = 0

    init(_ outcomes: [Result<AccountLimitsSnapshot, Error>]) {
        self.outcomes = outcomes
    }

    func readLimits() async throws -> AccountLimitsSnapshot {
        callCount += 1
        guard !outcomes.isEmpty else { throw AccountLimitError.malformedResponse }
        return try outcomes.removeFirst().get()
    }
}

private actor CountingLimitProvider: AccountLimitProviding {
    private let snapshot: AccountLimitsSnapshot
    private(set) var callCount = 0

    init(_ snapshot: AccountLimitsSnapshot) { self.snapshot = snapshot }

    func readLimits() async throws -> AccountLimitsSnapshot {
        callCount += 1
        return snapshot
    }
}

private struct TimeoutRunner: AppServerProcessRunning {
    func run(
        executable _: URL,
        arguments _: [String],
        standardInput _: Data,
        timeout _: Duration,
        maximumOutputBytes _: Int
    ) async throws -> Data {
        throw AccountLimitError.timedOut
    }
}
