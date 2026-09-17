import Foundation
import XCTest
@testable import CodeRim

@MainActor
final class ProfileUsageStoreTests: XCTestCase {
    func testDefaultIsOptInAndRefreshDoesNotAccessFetcher() async throws {
        let fixture = try DefaultsFixture()
        defer { fixture.remove() }
        let counter = StoreInvocationCounter()
        let store = ProfileUsageStore(defaults: fixture.defaults) { _, _, _ in
            await counter.increment()
            throw ProfileUsageError.transportFailure
        }

        XCTAssertFalse(store.isEnabled)
        XCTAssertEqual(store.status, .disabled)
        XCTAssertNil(store.snapshot)

        await store.refresh()

        let invocationCount = await counter.value()
        XCTAssertEqual(invocationCount, 0)
        XCTAssertEqual(store.statusMessage, "Account totals are off")
    }

    func testEnabledRefreshKeepsProfileDataInMemoryAndForwardsCalendarInputs() async throws {
        let fixture = try DefaultsFixture()
        defer { fixture.remove() }
        let recorder = FetchRecorder()
        let expected = ProfileUsageSnapshot(
            today: 1,
            week: 2,
            month: 3,
            lifetime: 4,
            statsAsOf: try isoDate("2026-08-28T00:00:00Z"),
            generatedAt: try isoDate("2026-08-28T01:00:00Z")
        )
        let store = ProfileUsageStore(defaults: fixture.defaults) { now, calendar, weekStart in
            await recorder.record(now: now, calendar: calendar, weekStart: weekStart)
            return expected
        }
        let now = try isoDate("2026-08-28T09:41:00Z")
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 9 * 3_600)!

        store.setEnabled(true)
        await store.refresh(now: now, calendar: calendar, weekStart: .sunday)

        XCTAssertTrue(fixture.defaults.bool(forKey: ProfileUsageStore.enabledPreferenceKey))
        XCTAssertEqual(store.snapshot, expected)
        XCTAssertEqual(store.status, .ready)
        XCTAssertFalse(store.isRefreshing)
        let arguments = await recorder.value()
        XCTAssertEqual(arguments?.now, now)
        XCTAssertEqual(arguments?.timeZone, calendar.timeZone)
        XCTAssertEqual(arguments?.weekStart, .sunday)

        store.setEnabled(false)
        XCTAssertNil(store.snapshot)
        XCTAssertEqual(store.status, .disabled)
        XCTAssertFalse(fixture.defaults.bool(forKey: ProfileUsageStore.enabledPreferenceKey))
    }

    func testErrorsMapToStableSanitizedStatuses() async throws {
        let credentialErrors: [ProfileUsageError] = [
            .credentialsUnavailable,
            .unsafeCredentialFile,
            .invalidCredentials
        ]
        for error in credentialErrors {
            let fixture = try DefaultsFixture(enabled: true)
            defer { fixture.remove() }
            let store = ProfileUsageStore(defaults: fixture.defaults) { _, _, _ in throw error }

            await store.refresh()

            XCTAssertEqual(store.status, .credentialsUnavailable)
            XCTAssertEqual(store.statusMessage, "Codex sign-in is unavailable")
        }

        let fixture = try DefaultsFixture(enabled: true)
        defer { fixture.remove() }
        let store = ProfileUsageStore(defaults: fixture.defaults) { _, _, _ in
            throw NSError(
                domain: "secret.raw.error",
                code: 42,
                userInfo: [NSLocalizedDescriptionKey: "Bearer private-token"]
            )
        }

        await store.refresh()

        XCTAssertEqual(store.status, .unavailable)
        XCTAssertEqual(store.statusMessage, "Account totals are unavailable")
        XCTAssertFalse(store.statusMessage.contains("private-token"))
        XCTAssertFalse(store.statusMessage.contains("secret.raw.error"))
    }

    func testCredentialFailureClearsPreviousAccountSnapshotButNetworkFailureKeepsIt() async throws {
        let credentialFixture = try DefaultsFixture(enabled: true)
        defer { credentialFixture.remove() }
        let expected = ProfileUsageSnapshot(
            today: 1,
            week: 2,
            month: 3,
            lifetime: 4,
            statsAsOf: try isoDate("2026-08-28T00:00:00Z"),
            generatedAt: try isoDate("2026-08-28T01:00:00Z")
        )
        let credentialProbe = SequencedFetchProbe(
            outcomes: [.success(expected), .failure(.invalidCredentials)]
        )
        let credentialStore = ProfileUsageStore(defaults: credentialFixture.defaults) { _, _, _ in
            try await credentialProbe.fetch()
        }

        await credentialStore.refresh()
        XCTAssertEqual(credentialStore.snapshot, expected)
        await credentialStore.refresh()
        XCTAssertNil(credentialStore.snapshot)
        XCTAssertEqual(credentialStore.status, .credentialsUnavailable)

        let networkFixture = try DefaultsFixture(enabled: true)
        defer { networkFixture.remove() }
        let networkProbe = SequencedFetchProbe(
            outcomes: [.success(expected), .failure(.transportFailure)]
        )
        let networkStore = ProfileUsageStore(defaults: networkFixture.defaults) { _, _, _ in
            try await networkProbe.fetch()
        }

        await networkStore.refresh()
        await networkStore.refresh()
        XCTAssertEqual(networkStore.snapshot, expected)
        XCTAssertEqual(networkStore.status, .unavailable)
    }

    func testDisablingDuringManualRefreshCancelsRequestAndDiscardsResponse() async throws {
        let fixture = try DefaultsFixture(enabled: true)
        defer { fixture.remove() }
        let probe = CancellableAutomaticFetchProbe()
        let store = ProfileUsageStore(defaults: fixture.defaults) { _, _, _ in
            try await probe.fetch()
        }

        let refresh = Task { await store.refresh() }
        try await waitUntil { await probe.hasStarted() }
        store.setEnabled(false)
        try await waitUntil { await probe.wasCancelled() }
        await refresh.value

        XCTAssertFalse(store.isEnabled)
        XCTAssertFalse(store.isRefreshing)
        XCTAssertNil(store.snapshot)
        XCTAssertEqual(store.status, .disabled)
    }

    func testObservedDefaultsEnableAutomaticallyRefreshesOnceWithSelectedWeekStart() async throws {
        let fixture = try DefaultsFixture()
        defer { fixture.remove() }
        let response = ProfileUsageSnapshot(
            today: 1,
            week: 2,
            month: 3,
            lifetime: 4,
            statsAsOf: try isoDate("2026-08-28T00:00:00Z"),
            generatedAt: try isoDate("2026-08-28T01:00:00Z")
        )
        let probe = AutomaticFetchProbe(response: response)
        let store = ProfileUsageStore(defaults: fixture.defaults) { now, calendar, weekStart in
            await probe.fetch(now: now, calendar: calendar, weekStart: weekStart)
        }
        await Task.yield()

        fixture.defaults.set(WeekStart.sunday.rawValue, forKey: "weekStart")
        fixture.defaults.set(true, forKey: ProfileUsageStore.enabledPreferenceKey)
        NotificationCenter.default.post(name: UserDefaults.didChangeNotification, object: fixture.defaults)

        try await waitUntil { store.status == .ready }
        try await Task.sleep(for: .milliseconds(20))
        let recorded = await probe.value()
        XCTAssertTrue(store.isEnabled)
        XCTAssertEqual(store.snapshot, response)
        XCTAssertEqual(recorded.count, 1)
        XCTAssertEqual(recorded.weekStart, .sunday)

        fixture.defaults.set(WeekStart.monday.rawValue, forKey: "weekStart")
        NotificationCenter.default.post(name: UserDefaults.didChangeNotification, object: fixture.defaults)
        try await waitUntil {
            let value = await probe.value()
            return value.count == 2
        }
        let updated = await probe.value()
        XCTAssertEqual(updated.weekStart, .monday)
    }

    func testObservedDefaultsDisableCancelsAutomaticRefreshAndClearsSnapshot() async throws {
        let fixture = try DefaultsFixture()
        defer { fixture.remove() }
        let probe = CancellableAutomaticFetchProbe()
        let store = ProfileUsageStore(defaults: fixture.defaults) { _, _, _ in
            try await probe.fetch()
        }
        await Task.yield()

        fixture.defaults.set(true, forKey: ProfileUsageStore.enabledPreferenceKey)
        NotificationCenter.default.post(name: UserDefaults.didChangeNotification, object: fixture.defaults)
        try await waitUntil { await probe.hasStarted() }

        fixture.defaults.set(false, forKey: ProfileUsageStore.enabledPreferenceKey)
        NotificationCenter.default.post(name: UserDefaults.didChangeNotification, object: fixture.defaults)
        try await waitUntil {
            let cancelled = await probe.wasCancelled()
            return cancelled && !store.isRefreshing
        }

        XCTAssertFalse(store.isEnabled)
        XCTAssertFalse(store.isRefreshing)
        XCTAssertNil(store.snapshot)
        XCTAssertEqual(store.status, .disabled)
    }

    func testRapidDisableAndReenableWaitsForCancelledRefreshBeforeStartingReplacement() async throws {
        let fixture = try DefaultsFixture()
        defer { fixture.remove() }
        let stale = ProfileUsageSnapshot(
            today: 1,
            week: 1,
            month: 1,
            lifetime: 1,
            statsAsOf: try isoDate("2026-08-27T00:00:00Z"),
            generatedAt: try isoDate("2026-08-27T01:00:00Z")
        )
        let fresh = ProfileUsageSnapshot(
            today: 2,
            week: 2,
            month: 2,
            lifetime: 2,
            statsAsOf: try isoDate("2026-08-28T00:00:00Z"),
            generatedAt: try isoDate("2026-08-28T01:00:00Z")
        )
        let probe = SuspendedFirstFetchProbe(first: stale, second: fresh)
        let store = ProfileUsageStore(defaults: fixture.defaults) { _, _, _ in
            await probe.fetch()
        }
        await Task.yield()

        fixture.defaults.set(true, forKey: ProfileUsageStore.enabledPreferenceKey)
        NotificationCenter.default.post(name: UserDefaults.didChangeNotification, object: fixture.defaults)
        try await waitUntil { await probe.fetchCount() == 1 }

        fixture.defaults.set(false, forKey: ProfileUsageStore.enabledPreferenceKey)
        NotificationCenter.default.post(name: UserDefaults.didChangeNotification, object: fixture.defaults)
        try await waitUntil { !store.isEnabled }
        fixture.defaults.set(true, forKey: ProfileUsageStore.enabledPreferenceKey)
        NotificationCenter.default.post(name: UserDefaults.didChangeNotification, object: fixture.defaults)
        try await waitUntil { store.isEnabled }

        let countBeforeResume = await probe.fetchCount()
        XCTAssertEqual(countBeforeResume, 1)
        await probe.resumeFirstFetch()
        try await waitUntil { store.snapshot == fresh }

        let finalCount = await probe.fetchCount()
        XCTAssertEqual(finalCount, 2)
        XCTAssertEqual(store.status, .ready)
    }

    func testCalendarContextChangeInvalidatesOldPeriodsUntilReplacementCompletes() async throws {
        let fixture = try DefaultsFixture(enabled: true)
        defer { fixture.remove() }
        let stale = ProfileUsageSnapshot(
            today: 10,
            week: 20,
            month: 30,
            lifetime: 40,
            statsAsOf: try isoDate("2026-08-31T00:00:00Z"),
            generatedAt: try isoDate("2026-08-31T01:00:00Z")
        )
        let fresh = ProfileUsageSnapshot(
            today: 0,
            week: 0,
            month: 0,
            lifetime: 40,
            statsAsOf: try isoDate("2026-08-31T00:00:00Z"),
            generatedAt: try isoDate("2026-09-01T01:00:00Z")
        )
        let probe = SuspendedSecondFetchProbe(first: stale, second: fresh)
        let store = ProfileUsageStore(defaults: fixture.defaults) { _, _, _ in
            await probe.fetch()
        }

        await store.refresh()
        XCTAssertEqual(store.snapshot, stale)

        store.handleCalendarContextChange()

        XCTAssertNil(store.snapshot, "Old week/month totals must not survive a calendar rollover")
        XCTAssertEqual(store.status, .idle)
        try await waitUntil { await probe.fetchCount() == 2 }
        XCTAssertNil(store.snapshot)

        await probe.resumeSecondFetch()
        try await waitUntil { store.snapshot == fresh }
        XCTAssertEqual(store.status, .ready)
    }

    func testCalendarContextChangeDoesNotRestoreStalePeriodsWhenRefreshFails() async throws {
        let fixture = try DefaultsFixture(enabled: true)
        defer { fixture.remove() }
        let stale = ProfileUsageSnapshot(
            today: 10,
            week: 20,
            month: 30,
            lifetime: 40,
            statsAsOf: try isoDate("2026-08-31T00:00:00Z"),
            generatedAt: try isoDate("2026-08-31T01:00:00Z")
        )
        let probe = SequencedFetchProbe(
            outcomes: [.success(stale), .failure(.transportFailure)]
        )
        let store = ProfileUsageStore(defaults: fixture.defaults) { _, _, _ in
            try await probe.fetch()
        }

        await store.refresh()
        XCTAssertEqual(store.snapshot, stale)

        store.handleCalendarContextChange()
        try await waitUntil { store.status == .unavailable }

        XCTAssertNil(store.snapshot)
        XCTAssertFalse(store.isRefreshing)
    }

    private func isoDate(_ value: String) throws -> Date {
        try Date.ISO8601FormatStyle().parse(value)
    }

    private func waitUntil(
        _ predicate: @escaping () async -> Bool
    ) async throws {
        for _ in 0 ..< 100 {
            if await predicate() { return }
            try await Task.sleep(for: .milliseconds(5))
        }
        XCTFail("Timed out waiting for asynchronous profile state")
    }
}

private struct DefaultsFixture {
    let name: String
    let defaults: UserDefaults

    init(enabled: Bool? = nil) throws {
        name = "CodeRimProfileTests.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        if let enabled {
            defaults.set(enabled, forKey: ProfileUsageStore.enabledPreferenceKey)
        }
    }

    func remove() {
        defaults.removePersistentDomain(forName: name)
    }
}

private actor FetchRecorder {
    struct Arguments: Sendable {
        let now: Date
        let timeZone: TimeZone
        let weekStart: WeekStart
    }

    private var arguments: Arguments?

    func record(now: Date, calendar: Calendar, weekStart: WeekStart) {
        arguments = Arguments(now: now, timeZone: calendar.timeZone, weekStart: weekStart)
    }

    func value() -> Arguments? {
        arguments
    }
}

private actor StoreInvocationCounter {
    private var count = 0

    func increment() {
        count += 1
    }

    func value() -> Int {
        count
    }
}

private actor AutomaticFetchProbe {
    struct Recorded: Sendable {
        let count: Int
        let weekStart: WeekStart?
    }

    private let response: ProfileUsageSnapshot
    private var count = 0
    private var weekStart: WeekStart?

    init(response: ProfileUsageSnapshot) {
        self.response = response
    }

    func fetch(
        now _: Date,
        calendar _: Calendar,
        weekStart: WeekStart
    ) -> ProfileUsageSnapshot {
        count += 1
        self.weekStart = weekStart
        return response
    }

    func value() -> Recorded {
        Recorded(count: count, weekStart: weekStart)
    }
}

private actor CancellableAutomaticFetchProbe {
    private var started = false
    private var cancelled = false

    func fetch() async throws -> ProfileUsageSnapshot {
        started = true
        do {
            try await Task.sleep(for: .seconds(10))
        } catch is CancellationError {
            cancelled = true
            throw CancellationError()
        }
        throw ProfileUsageError.transportFailure
    }

    func hasStarted() -> Bool {
        started
    }

    func wasCancelled() -> Bool {
        cancelled
    }
}

private actor SequencedFetchProbe {
    enum Outcome: Sendable {
        case success(ProfileUsageSnapshot)
        case failure(ProfileUsageError)
    }

    private let outcomes: [Outcome]
    private var index = 0

    init(outcomes: [Outcome]) {
        self.outcomes = outcomes
    }

    func fetch() throws -> ProfileUsageSnapshot {
        let outcome = outcomes[min(index, outcomes.count - 1)]
        index += 1
        switch outcome {
        case let .success(snapshot):
            return snapshot
        case let .failure(error):
            throw error
        }
    }
}

private actor SuspendedFirstFetchProbe {
    private let first: ProfileUsageSnapshot
    private let second: ProfileUsageSnapshot
    private var count = 0
    private var firstContinuation: CheckedContinuation<Void, Never>?

    init(first: ProfileUsageSnapshot, second: ProfileUsageSnapshot) {
        self.first = first
        self.second = second
    }

    func fetch() async -> ProfileUsageSnapshot {
        count += 1
        if count == 1 {
            await withCheckedContinuation { continuation in
                firstContinuation = continuation
            }
            return first
        }
        return second
    }

    func fetchCount() -> Int {
        count
    }

    func resumeFirstFetch() {
        let continuation = firstContinuation
        firstContinuation = nil
        continuation?.resume()
    }
}

private actor SuspendedSecondFetchProbe {
    private let first: ProfileUsageSnapshot
    private let second: ProfileUsageSnapshot
    private var count = 0
    private var secondContinuation: CheckedContinuation<Void, Never>?

    init(first: ProfileUsageSnapshot, second: ProfileUsageSnapshot) {
        self.first = first
        self.second = second
    }

    func fetch() async -> ProfileUsageSnapshot {
        count += 1
        if count == 1 { return first }
        await withCheckedContinuation { continuation in
            secondContinuation = continuation
        }
        return second
    }

    func fetchCount() -> Int {
        count
    }

    func resumeSecondFetch() {
        let continuation = secondContinuation
        secondContinuation = nil
        continuation?.resume()
    }
}
