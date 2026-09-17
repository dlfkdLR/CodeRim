import AppKit
import Foundation
import SwiftUI

@MainActor
final class ProfileUsageStore: ObservableObject {
    nonisolated static let enabledPreferenceKey = "profileSyncEnabled"

    @Published private(set) var snapshot: ProfileUsageSnapshot?
    @Published private(set) var isEnabled: Bool
    @Published private(set) var isRefreshing = false
    @Published private(set) var status: ProfileUsageStatus

    var statusMessage: String { status.message }

    private let defaults: UserDefaults
    private let fetcher: ProfileUsageFetching
    private var defaultsTask: Task<Void, Never>?
    private var automaticRefreshTask: Task<Void, Never>?
    private var clockChangeTask: Task<Void, Never>?
    private var timeZoneChangeTask: Task<Void, Never>?
    private var wakeTask: Task<Void, Never>?
    private var calendarBoundaryTask: Task<Void, Never>?
    private var inFlightFetchTask: Task<ProfileUsageSnapshot, any Error>?
    private var inFlightRefreshID: UUID?
    private let allowsAccountTotals: Bool
    private var enabledGeneration = 0
    private var observedWeekStartRawValue: Int

    init(
        defaults: UserDefaults = .standard,
        allowsAccountTotals: Bool = true,
        fetcher: @escaping ProfileUsageFetching = { now, calendar, weekStart in
            try await ChatGPTProfileClient().fetch(
                now: now,
                calendar: calendar,
                weekStart: weekStart
            )
        }
    ) {
        self.defaults = defaults
        self.allowsAccountTotals = allowsAccountTotals
        self.fetcher = fetcher
        let enabled = allowsAccountTotals && (defaults.object(forKey: Self.enabledPreferenceKey) as? Bool ?? false)
        isEnabled = enabled
        status = enabled ? .idle : .disabled
        observedWeekStartRawValue = Self.storedWeekStartRawValue(in: defaults)
        // The live usage interface never fetches delayed profile totals, even
        // when a previous version left profile sync enabled in saved preferences.
        guard allowsAccountTotals else { return }
        defaultsTask = Task { [weak self] in
            let notifications = NotificationCenter.default.notifications(
                named: UserDefaults.didChangeNotification,
                object: nil
            )
            for await _ in notifications {
                guard !Task.isCancelled else { return }
                self?.synchronizeEnabledPreference()
            }
        }
        clockChangeTask = Task { [weak self] in
            let notifications = NotificationCenter.default.notifications(
                named: .NSSystemClockDidChange
            )
            for await _ in notifications {
                guard !Task.isCancelled else { return }
                self?.handleCalendarContextChange()
            }
        }
        timeZoneChangeTask = Task { [weak self] in
            let notifications = NotificationCenter.default.notifications(
                named: .NSSystemTimeZoneDidChange
            )
            for await _ in notifications {
                guard !Task.isCancelled else { return }
                self?.handleCalendarContextChange()
            }
        }
        wakeTask = Task { [weak self] in
            let notifications = NSWorkspace.shared.notificationCenter.notifications(
                named: NSWorkspace.didWakeNotification
            )
            for await _ in notifications {
                guard !Task.isCancelled else { return }
                self?.handleCalendarContextChange()
            }
        }
        scheduleCalendarBoundary()
    }

    deinit {
        defaultsTask?.cancel()
        automaticRefreshTask?.cancel()
        clockChangeTask?.cancel()
        timeZoneChangeTask?.cancel()
        wakeTask?.cancel()
        calendarBoundaryTask?.cancel()
        inFlightFetchTask?.cancel()
    }

    func setEnabled(_ enabled: Bool) {
        defaults.set(enabled, forKey: Self.enabledPreferenceKey)
        applyEnabledState(enabled)
    }

    func synchronizeEnabledPreference() {
        let enabled = allowsAccountTotals && (defaults.object(forKey: Self.enabledPreferenceKey) as? Bool ?? false)
        let wasEnabled = isEnabled
        let weekStartRawValue = Self.storedWeekStartRawValue(in: defaults)
        let weekStartChanged = weekStartRawValue != observedWeekStartRawValue
        observedWeekStartRawValue = weekStartRawValue
        applyEnabledState(enabled)
        if enabled, weekStartChanged {
            invalidateSnapshotForCalendarContextChange()
        }
        if enabled, !wasEnabled || weekStartChanged {
            scheduleAutomaticRefresh()
        }
    }

    private func applyEnabledState(_ enabled: Bool) {
        guard enabled != isEnabled else { return }
        enabledGeneration &+= 1
        isEnabled = enabled
        if enabled {
            status = snapshot == nil ? .idle : .ready
            scheduleCalendarBoundary()
        } else {
            automaticRefreshTask?.cancel()
            automaticRefreshTask = nil
            calendarBoundaryTask?.cancel()
            calendarBoundaryTask = nil
            inFlightFetchTask?.cancel()
            snapshot = nil
            status = .disabled
        }
    }

    private func scheduleAutomaticRefresh() {
        automaticRefreshTask?.cancel()
        let weekStart = selectedWeekStart
        let generation = enabledGeneration
        automaticRefreshTask = Task { [weak self] in
            guard let self else { return }
            while self.isRefreshing {
                do {
                    try await Task.sleep(for: .milliseconds(10))
                } catch {
                    return
                }
                guard self.isEnabled, self.enabledGeneration == generation else { return }
            }
            guard self.isEnabled, self.enabledGeneration == generation else { return }
            await self.refresh(weekStart: weekStart)
            if !Task.isCancelled {
                self.automaticRefreshTask = nil
            }
        }
    }

    private var selectedWeekStart: WeekStart {
        let rawValue = Self.storedWeekStartRawValue(in: defaults)
        return WeekStart(rawValue: rawValue) ?? .monday
    }

    func handleCalendarContextChange() {
        scheduleCalendarBoundary()
        guard isEnabled else { return }
        invalidateSnapshotForCalendarContextChange()
        scheduleAutomaticRefresh()
    }

    func clearForAccountSwitch() {
        invalidateSnapshotForCalendarContextChange()
    }

    private func invalidateSnapshotForCalendarContextChange() {
        enabledGeneration &+= 1
        automaticRefreshTask?.cancel()
        automaticRefreshTask = nil
        inFlightFetchTask?.cancel()
        snapshot = nil
        status = .idle
    }

    private func scheduleCalendarBoundary() {
        calendarBoundaryTask?.cancel()
        guard isEnabled else {
            calendarBoundaryTask = nil
            return
        }
        calendarBoundaryTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let delay = self?.secondsUntilNextCalendarDay() else { return }
                do {
                    try await Task.sleep(for: .seconds(delay))
                } catch {
                    return
                }
                guard !Task.isCancelled else { return }
                self?.handleCalendarContextChange()
            }
        }
    }

    private func secondsUntilNextCalendarDay(
        now: Date = Date(),
        calendar: Calendar = .current
    ) -> Double {
        let start = calendar.startOfDay(for: now)
        let next = calendar.date(byAdding: .day, value: 1, to: start)
            ?? now.addingTimeInterval(3_600)
        return max(1, next.timeIntervalSince(now) + 0.25)
    }

    private nonisolated static func storedWeekStartRawValue(in defaults: UserDefaults) -> Int {
        defaults.object(forKey: "weekStart") == nil
            ? WeekStart.monday.rawValue
            : defaults.integer(forKey: "weekStart")
    }

    func refresh(
        now: Date = Date(),
        calendar: Calendar = .current,
        weekStart: WeekStart = .monday
    ) async {
        guard isEnabled else {
            status = .disabled
            return
        }
        guard !isRefreshing, !AccountSwitchActivity.isSwitching else { return }

        isRefreshing = true
        status = .refreshing
        let generation = enabledGeneration
        let fetchTask = Task {
            try await fetcher(now, calendar, weekStart)
        }
        let refreshID = UUID()
        inFlightFetchTask = fetchTask
        inFlightRefreshID = refreshID
        defer {
            if inFlightRefreshID == refreshID {
                inFlightFetchTask = nil
                inFlightRefreshID = nil
                isRefreshing = false
            }
        }

        do {
            let refreshedSnapshot = try await withTaskCancellationHandler {
                try await fetchTask.value
            } onCancel: {
                fetchTask.cancel()
            }
            guard isEnabled, generation == enabledGeneration else { return }
            snapshot = refreshedSnapshot
            status = .ready
        } catch is CancellationError {
            guard isEnabled, generation == enabledGeneration else { return }
            status = snapshot == nil ? .idle : .ready
        } catch let error as ProfileUsageError {
            guard isEnabled, generation == enabledGeneration else { return }
            switch error {
            case .credentialsUnavailable, .unsafeCredentialFile, .invalidCredentials:
                snapshot = nil
                status = .credentialsUnavailable
            default:
                status = .unavailable
            }
        } catch {
            guard isEnabled, generation == enabledGeneration else { return }
            status = .unavailable
        }
    }
}
