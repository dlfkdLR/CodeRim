import AppKit
import Foundation

@MainActor
final class AccountLimitStore: ObservableObject {
    @Published private(set) var snapshot: AccountLimitsSnapshot?
    @Published private(set) var status: AccountLimitStatus = .loading
    @Published private(set) var statusMessage = "Reading account limits…"
    @Published private(set) var isRefreshing = false

    private let provider: AccountLimitProviding
    private let defaults: UserDefaults
    private let pollingInterval: Duration?
    private var pollingTask: Task<Void, Never>?
    private var defaultsTask: Task<Void, Never>?
    private var freshnessTask: Task<Void, Never>?
    private var lifecycleTasks: [Task<Void, Never>] = []
    private var readGeneration = 0
    private var inFlightReadTask: Task<AccountLimitsSnapshot, Error>?
    /// The last `isEnabled` this store acted on, so an unrelated defaults write
    /// does not restart the poll.
    private var lastKnownEnabled: Bool

    init(
        provider: AccountLimitProviding = AppServerLimitProvider(),
        defaults: UserDefaults = .standard,
        pollingInterval: Duration? = .seconds(120)
    ) {
        self.provider = provider
        self.defaults = defaults
        self.pollingInterval = pollingInterval
        lastKnownEnabled = defaults.object(forKey: "accountLimitsEnabled") == nil
            || defaults.bool(forKey: "accountLimitsEnabled")
        synchronizeEnabledPreference()
        freshnessTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(30))
                guard !Task.isCancelled else { return }
                self?.updateFreshness()
            }
        }
        for (center, name) in [(NSWorkspace.shared.notificationCenter, NSWorkspace.didWakeNotification),
                               (NotificationCenter.default, NSApplication.didBecomeActiveNotification)] {
            lifecycleTasks.append(Task { [weak self] in
                for await _ in center.notifications(named: name) {
                    guard !Task.isCancelled else { return }
                    await self?.refreshIfNeeded()
                }
            })
        }
        defaultsTask = Task { [weak self] in
            let changes = NotificationCenter.default.notifications(
                named: UserDefaults.didChangeNotification,
                object: nil
            )
            for await _ in changes {
                guard !Task.isCancelled else { return }
                // `didChangeNotification` carries no key. Only the enabled flag
                // matters here — every other preference write (an alert mute, a
                // notch drag) would otherwise cancel and immediately restart the
                // poll, firing a fresh network read each time.
                guard let self, self.isEnabled != self.lastKnownEnabled else { continue }
                self.synchronizeEnabledPreference()
            }
        }
    }

    deinit {
        pollingTask?.cancel()
        defaultsTask?.cancel()
        freshnessTask?.cancel()
        lifecycleTasks.forEach { $0.cancel() }
        inFlightReadTask?.cancel()
    }

    var isEnabled: Bool {
        defaults.object(forKey: "accountLimitsEnabled") == nil
            || defaults.bool(forKey: "accountLimitsEnabled")
    }

    func synchronizeEnabledPreference() {
        lastKnownEnabled = isEnabled
        pollingTask?.cancel()
        pollingTask = nil
        guard isEnabled else {
            cancelRead()
            status = .disabled
            statusMessage = "Account limits are disabled"
            snapshot = nil
            return
        }
        if snapshot == nil {
            status = .loading
            statusMessage = "Reading account limits…"
        }
        guard let pollingInterval else { return }
        pollingTask = Task { [weak self] in
            await self?.refresh()
            while !Task.isCancelled {
                try? await Task.sleep(for: pollingInterval)
                guard !Task.isCancelled else { return }
                await self?.refresh()
            }
        }
    }

    func refresh() async {
        guard isEnabled, !isRefreshing, !AccountSwitchActivity.isSwitching else { return }
        updateFreshness()
        readGeneration += 1
        let operation = readGeneration
        let accountGeneration = AccountSwitchActivity.generation
        isRefreshing = true
        if snapshot == nil { status = .loading }
        let readTask = Task { try await provider.readLimits() }
        inFlightReadTask = readTask
        defer {
            if readGeneration == operation { isRefreshing = false; inFlightReadTask = nil }
        }
        do {
            let refreshed = try await withTaskCancellationHandler {
                try await readTask.value
            } onCancel: { readTask.cancel() }
            guard readGeneration == operation, isEnabled, accountGeneration == AccountSwitchActivity.generation, !AccountSwitchActivity.isSwitching else { return }
            snapshot = refreshed
            status = .ready
            updateFreshness()
        } catch {
            guard readGeneration == operation, isEnabled, accountGeneration == AccountSwitchActivity.generation, !AccountSwitchActivity.isSwitching else { return }
            if snapshot != nil {
                status = .stale
                statusMessage = "Showing last known limits"
            } else {
                status = .unavailable
                statusMessage = "Account limits unavailable"
            }
        }
    }

    func refreshIfNeeded(now: Date = Date()) async {
        updateFreshness(now: now)
        guard snapshot == nil || now.timeIntervalSince(snapshot!.fetchedAt) >= 60 else { return }
        await refresh()
    }

    func updateFreshness(now: Date = Date()) {
        guard isEnabled, let snapshot, status == .ready || status == .stale else { return }
        if now.timeIntervalSince(snapshot.fetchedAt) >= 300 { status = .stale }
        statusMessage = status == .stale ? "Showing last known limits"
            : LimitFreshness.text(fetchedAt: snapshot.fetchedAt, now: now)
    }

    private func cancelRead() {
        readGeneration += 1
        inFlightReadTask?.cancel()
        inFlightReadTask = nil
        isRefreshing = false
    }

    func clearForAccountSwitch() {
        cancelRead()
        snapshot = nil
        status = .loading
        statusMessage = "Switching account…"
    }
}
