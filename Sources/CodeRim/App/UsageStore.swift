import AppKit
import Foundation
import SwiftUI

@MainActor
final class UsageStore: ObservableObject {
    let provider: UsageProvider
    @Published private(set) var snapshot = UsageSnapshot.empty
    @Published private(set) var hasLoadedSnapshot = false
    @Published private(set) var isRefreshing = false
    @Published private(set) var isImportingHistory = false
    @Published private(set) var isMaintainingData = false
    @Published private(set) var statusMessage = "Looking for Codex usage…"
    @Published private(set) var lastSourceRefreshAt: Date?
    @Published private(set) var dataOperationMessage: String?
    @Published private(set) var dataOperationFailed = false
    @Published private(set) var dataStatistics = DataStatistics.empty
    @Published private(set) var sourceCount = 0
    @Published private(set) var analyticsSnapshots: [AnalyticsRange: AnalyticsSnapshot] = [:]
    @Published private(set) var isAnalyticsRefreshing = false
    @Published private(set) var analyticsStatusMessage = "Analytics are ready to load"

    private let formatter = TokenFormatter()
    private let defaults: UserDefaults
    private var collector: CodexUsageCollector?
    private var sourceRoots: [URL] = []
    private var watcher: CodexSessionWatcher?
    private var watcherTask: Task<Void, Never>?
    private var debounceTask: Task<Void, Never>?
    private var refreshSchedulerTask: Task<Void, Never>?
    private var wakeTask: Task<Void, Never>?
    private var defaultsTask: Task<Void, Never>?
    private var clockChangeTask: Task<Void, Never>?
    private var timeZoneChangeTask: Task<Void, Never>?
    private var calendarBoundaryTask: Task<Void, Never>?
    private var refreshPending = false
    private var requestedAnalyticsRanges: Set<AnalyticsRange> = [.today]
    private var analyticsRevision: UInt64 = 0
    private var analyticsRequests: [AnalyticsRange: UUID] = [:]
    private var previousWeekStartRawValue = WeekStart.monday.rawValue
    private var previousRefreshModeRawValue = RefreshMode.automatic.rawValue

    var sourceStatusText: String {
        if !hasLoadedSnapshot || (isRefreshing && lastSourceRefreshAt == nil) {
            return "Checking…"
        }
        if sourceCount > 0 {
            return "Connected"
        }
        return "No session files found"
    }

    var operationAwareStatusSymbol: String {
        if isRefreshing || isMaintainingData || isImportingHistory {
            return "arrow.triangle.2.circlepath"
        }
        return switch snapshot.quality {
        case .exact, .partial: "checkmark.circle"
        case .stale: "exclamationmark.triangle"
        case .unavailable: "questionmark.circle"
        case .error: "exclamationmark.triangle"
        }
    }

    init(
        provider: UsageProvider = .codex,
        analyticsSnapshots: [AnalyticsRange: AnalyticsSnapshot] = [:],
        initialSnapshot: UsageSnapshot? = nil,
        automaticallyRefresh: Bool = true,
        collector: CodexUsageCollector? = nil,
        defaults: UserDefaults = .standard
    ) {
        self.provider = provider
        self.collector = collector
        self.defaults = defaults
        statusMessage = "Looking for \(provider.title) usage…"
        self.analyticsSnapshots = analyticsSnapshots
        requestedAnalyticsRanges.formUnion(analyticsSnapshots.keys)
        if let initialSnapshot {
            self.snapshot = initialSnapshot
            hasLoadedSnapshot = true
            lastSourceRefreshAt = initialSnapshot.updatedAt
            statusMessage = initialSnapshot.updatedAt == nil ? "No \(provider.title) usage found" : "Updated"
        }
        guard automaticallyRefresh else { return }
        startAutomaticRefresh()
    }

    func startAutomaticRefresh() {
        guard refreshSchedulerTask == nil else { return }
        previousWeekStartRawValue = storedWeekStartRawValue
        previousRefreshModeRawValue = currentRefreshMode.rawValue
        refreshSchedulerTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(5))
                guard !Task.isCancelled, let self else { return }
                self.refreshIfScheduled()
            }
        }
        wakeTask = Task { [weak self] in
            let notifications = NSWorkspace.shared.notificationCenter.notifications(
                named: NSWorkspace.didWakeNotification
            )
            for await _ in notifications {
                guard !Task.isCancelled else { return }
                if self?.currentRefreshMode == .manual {
                    await self?.recalculateVisiblePeriods()
                } else {
                    await self?.refresh()
                }
            }
        }
        defaultsTask = Task { [weak self] in
            let notifications = NotificationCenter.default.notifications(
                named: UserDefaults.didChangeNotification,
                object: nil
            )
            for await _ in notifications {
                guard !Task.isCancelled, let self else { return }
                self.objectWillChange.send()
                let weekStart = self.storedWeekStartRawValue
                if weekStart != self.previousWeekStartRawValue {
                    self.previousWeekStartRawValue = weekStart
                    await self.recalculateVisiblePeriods()
                }
                let refreshMode = self.currentRefreshMode.rawValue
                if refreshMode != self.previousRefreshModeRawValue {
                    self.previousRefreshModeRawValue = refreshMode
                    self.updateWatcherForRefreshMode()
                    await self.refresh()
                }
            }
        }
        clockChangeTask = Task { [weak self] in
            let notifications = NotificationCenter.default.notifications(named: .NSSystemClockDidChange)
            for await _ in notifications {
                guard !Task.isCancelled else { return }
                self?.scheduleCalendarBoundary()
                await self?.recalculateVisiblePeriods()
            }
        }
        timeZoneChangeTask = Task { [weak self] in
            let notifications = NotificationCenter.default.notifications(named: .NSSystemTimeZoneDidChange)
            for await _ in notifications {
                guard !Task.isCancelled else { return }
                self?.scheduleCalendarBoundary()
                await self?.recalculateVisiblePeriods()
            }
        }
        scheduleCalendarBoundary()
    }

    func stopAutomaticRefresh() {
        stopWatcher()
        debounceTask?.cancel()
        debounceTask = nil
        refreshSchedulerTask?.cancel()
        refreshSchedulerTask = nil
        wakeTask?.cancel()
        wakeTask = nil
        defaultsTask?.cancel()
        defaultsTask = nil
        clockChangeTask?.cancel()
        clockChangeTask = nil
        timeZoneChangeTask?.cancel()
        timeZoneChangeTask = nil
        calendarBoundaryTask?.cancel()
        calendarBoundaryTask = nil
    }

    func refresh() async {
        guard !isMaintainingData else {
            refreshPending = true
            return
        }
        guard !isRefreshing else {
            refreshPending = true
            return
        }
        isRefreshing = true
        await DiagnosticsLogger.shared.record(.refreshStarted)
        defer {
            isRefreshing = false
            if refreshPending {
                refreshPending = false
                scheduleContinuationRefresh()
            }
        }

        do {
            let collector = try await collector()
            let weekStart = selectedWeekStart
            if snapshot.updatedAt == nil {
                let cached = try await collector.cachedSnapshot(weekStart: weekStart)
                hasLoadedSnapshot = true
                if cached.updatedAt != nil {
                    snapshot = cached
                    statusMessage = "Updating local usage…"
                } else {
                    statusMessage = "Scanning local \(provider.title) usage…"
                }
            }
            let result = try await collector.refresh(weekStart: weekStart)
            hasLoadedSnapshot = true
            if result.snapshot != snapshot {
                snapshot = result.snapshot
            }
            dataStatistics = result.statistics
            sourceCount = result.sourceCount
            lastSourceRefreshAt = Date()
            isImportingHistory = result.hasMoreWork
            invalidateAnalytics(clearSnapshots: false)
            await refreshRequestedAnalytics(using: collector)
            updateWatcherForRefreshMode()
            await DiagnosticsLogger.shared.record(
                .refreshCompleted(
                    quality: result.snapshot.quality,
                    sourceCount: result.sourceCount,
                    processedBytes: result.processedBytes
                )
            )
            if result.hasMoreWork {
                statusMessage = "Importing local history…"
                scheduleContinuationRefresh()
            } else {
                switch result.snapshot.quality {
                case .exact, .partial:
                    statusMessage = result.snapshot.updatedAt == nil ? "No \(provider.title) usage found" : "Updated just now"
                case .stale:
                    statusMessage = "Refresh failed"
                case .unavailable:
                    statusMessage = result.sourceCount == 0 ? "\(provider.title) sessions not found" : "No \(provider.title) usage found"
                case .error:
                    statusMessage = "Refresh failed"
                }
            }
        } catch is CancellationError {
            isImportingHistory = false
            hasLoadedSnapshot = true
            statusMessage = "Refresh cancelled"
        } catch {
            isImportingHistory = false
            hasLoadedSnapshot = true
            await DiagnosticsLogger.shared.record(
                .refreshFailed(hasCachedSnapshot: snapshot.updatedAt != nil)
            )
            let resourceMessage: String? = switch error {
            case CodexSourceDiscoveryError.sourceLimitExceeded:
                "Too many \(provider.title) session files to scan safely"
            case SQLiteDatabaseError.resourceLimit:
                "Local database safety limit reached"
            default:
                nil
            }
            if let resourceMessage {
                snapshot.quality = snapshot.updatedAt == nil ? .error : .stale
                statusMessage = resourceMessage
            } else if snapshot.updatedAt != nil {
                snapshot.quality = .stale
                statusMessage = "Refresh failed"
            } else {
                snapshot.quality = .error
                statusMessage = "Unable to read local usage"
            }
        }
    }

    func rebuildStatistics() async {
        guard !isMaintainingData, !isRefreshing else { return }
        isMaintainingData = true
        statusMessage = "Rebuilding local statistics…"
        dataOperationMessage = statusMessage
        dataOperationFailed = false
        invalidateAnalytics(clearSnapshots: true)
        defer { isMaintainingData = false }

        do {
            let collector = try await collector()
            let weekStart = selectedWeekStart
            let result = try await collector.rebuild(weekStart: weekStart)
            snapshot = result.snapshot
            hasLoadedSnapshot = true
            dataStatistics = result.statistics
            sourceCount = result.sourceCount
            refreshPending = false
            lastSourceRefreshAt = Date()
            isImportingHistory = result.hasMoreWork
            await refreshRequestedAnalytics(using: collector)
            await DiagnosticsLogger.shared.record(.rebuildCompleted(quality: result.snapshot.quality))
            statusMessage = result.hasMoreWork
                ? "Importing local history…"
                : "Rebuild complete"
            dataOperationMessage = statusMessage
            if result.hasMoreWork { scheduleContinuationRefresh() }
        } catch {
            await DiagnosticsLogger.shared.record(.rebuildFailed)
            snapshot.quality = snapshot.updatedAt == nil ? .error : .stale
            statusMessage = "Unable to rebuild statistics"
            dataOperationMessage = statusMessage
            dataOperationFailed = true
        }
    }

    func clearLocalHistory() async {
        guard !isMaintainingData, !isRefreshing else { return }
        isMaintainingData = true
        statusMessage = "Clearing local history…"
        dataOperationMessage = statusMessage
        dataOperationFailed = false
        invalidateAnalytics(clearSnapshots: true)
        defer { isMaintainingData = false }

        do {
            let collector = try await collector()
            let weekStart = selectedWeekStart
            let result = try await collector.clearLocalHistory(weekStart: weekStart)
            snapshot = result.snapshot
            hasLoadedSnapshot = true
            dataStatistics = result.statistics
            sourceCount = result.sourceCount
            refreshPending = false
            lastSourceRefreshAt = Date()
            isImportingHistory = result.hasMoreWork
            await refreshRequestedAnalytics(using: collector)
            await DiagnosticsLogger.shared.record(.clearCompleted)
            statusMessage = result.hasMoreWork
                ? "Importing local history…"
                : (result.maintenanceWarning ?? "Local history cleared")
            dataOperationMessage = statusMessage
            dataOperationFailed = result.maintenanceWarning != nil
            if result.hasMoreWork { scheduleContinuationRefresh() }
        } catch {
            await DiagnosticsLogger.shared.record(.clearFailed)
            snapshot.quality = snapshot.updatedAt == nil ? .error : .stale
            statusMessage = "Unable to clear local history"
            dataOperationMessage = statusMessage
            dataOperationFailed = true
        }
    }

    private func collector() async throws -> CodexUsageCollector {
        if let collector { return collector }

        let provider = self.provider
        let setup = try await Task.detached(priority: .utility) {
            let database = try SQLiteDatabase(url: provider.databaseURL)
            let roots = provider.sourceRoots()
            return (database, roots)
        }.value

        let collector = CodexUsageCollector(database: setup.0, roots: setup.1, provider: provider)
        self.collector = collector
        sourceRoots = setup.1
        updateWatcherForRefreshMode()
        return collector
    }

    func analyticsSnapshot(for range: AnalyticsRange) -> AnalyticsSnapshot? {
        analyticsSnapshots[range]
    }

    func refreshAnalytics(range: AnalyticsRange) async {
        requestedAnalyticsRanges.insert(range)
        guard !isMaintainingData else { return }
        let revision = analyticsRevision
        do {
            let collector = try await collector()
            guard revision == analyticsRevision, !isMaintainingData, !Task.isCancelled else { return }
            await refreshAnalytics(range: range, using: collector)
        } catch {
            guard revision == analyticsRevision, !Task.isCancelled else { return }
            analyticsStatusMessage = analyticsSnapshots[range] == nil
                ? "Analytics are unavailable"
                : "Showing the last analytics snapshot"
        }
    }

    private func refreshAnalytics(range: AnalyticsRange, using collector: CodexUsageCollector) async {
        let request = UUID()
        let revision = analyticsRevision
        analyticsRequests[range] = request
        isAnalyticsRefreshing = true
        defer {
            if analyticsRequests[range] == request { analyticsRequests.removeValue(forKey: range) }
            isAnalyticsRefreshing = !analyticsRequests.isEmpty
        }
        do {
            let snapshot = try await collector.analyticsSnapshot(range: range)
            guard revision == analyticsRevision, analyticsRequests[range] == request,
                  !Task.isCancelled else { return }
            analyticsSnapshots[range] = snapshot
            analyticsStatusMessage = "Analytics updated"
        } catch {
            guard revision == analyticsRevision, analyticsRequests[range] == request,
                  !Task.isCancelled else { return }
            analyticsStatusMessage = analyticsSnapshots[range] == nil
                ? "Analytics are unavailable"
                : "Showing the last analytics snapshot"
        }
    }

    private func invalidateAnalytics(clearSnapshots: Bool) {
        // Results already in flight must not restore data from before maintenance
        // or supersede a newer import, even if their task finishes later.
        analyticsRevision &+= 1
        analyticsRequests.removeAll()
        isAnalyticsRefreshing = false
        if clearSnapshots { analyticsSnapshots.removeAll() }
    }

    private func refreshRequestedAnalytics(using collector: CodexUsageCollector) async {
        let revision = analyticsRevision
        for range in AnalyticsRange.allCases where requestedAnalyticsRanges.contains(range) {
            guard revision == analyticsRevision, !Task.isCancelled else { return }
            await refreshAnalytics(range: range, using: collector)
        }
    }

    private func startWatcher(roots: [URL]) {
        guard watcher == nil else { return }
        let existingRoots = Array(
            Set(
                roots
                    .map { $0.deletingLastPathComponent().standardizedFileURL }
                    .filter { FileManager.default.fileExists(atPath: $0.path) }
            )
        ).sorted { $0.path < $1.path }
        guard !existingRoots.isEmpty else { return }

        let watcher = CodexSessionWatcher(roots: existingRoots)
        self.watcher = watcher
        watcher.start()
        watcherTask = Task { [weak self, watcher] in
            for await _ in watcher.events {
                guard !Task.isCancelled else { return }
                self?.scheduleRefresh()
            }
        }
    }

    private func stopWatcher() {
        watcherTask?.cancel()
        watcherTask = nil
        watcher?.stop()
        watcher = nil
    }

    private func updateWatcherForRefreshMode() {
        if currentRefreshMode.usesFileEvents {
            startWatcher(roots: sourceRoots)
        } else {
            stopWatcher()
        }
    }

    private func scheduleRefresh() {
        scheduleRefresh(delay: .milliseconds(500))
    }

    private func scheduleRefresh(delay: Duration) {
        guard currentRefreshMode.usesFileEvents else { return }
        scheduleRefreshRegardlessOfMode(delay: delay)
    }

    private func scheduleContinuationRefresh() {
        scheduleRefreshRegardlessOfMode(delay: .milliseconds(100))
    }

    private func scheduleRefreshRegardlessOfMode(delay: Duration) {
        guard debounceTask == nil else { return }
        debounceTask = Task { [weak self] in
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled else { return }
            self?.debounceTask = nil
            await self?.refresh()
        }
    }

    private var currentRefreshMode: RefreshMode {
        RefreshMode(rawValue: defaults.string(forKey: "refreshMode") ?? "") ?? .automatic
    }

    private var selectedWeekStart: WeekStart {
        WeekStart(rawValue: storedWeekStartRawValue) ?? .monday
    }

    private var storedWeekStartRawValue: Int {
        defaults.object(forKey: "weekStart") == nil
            ? WeekStart.monday.rawValue
            : defaults.integer(forKey: "weekStart")
    }

    private func refreshIfScheduled(now: Date = Date()) {
        guard let seconds = currentRefreshMode.pollingInterval else { return }
        guard let lastSourceRefreshAt else {
            Task { await refresh() }
            return
        }
        guard now.timeIntervalSince(lastSourceRefreshAt) >= seconds else { return }
        Task { await refresh() }
    }

    func recalculateVisiblePeriods() async {
        guard !isMaintainingData, !isRefreshing else {
            refreshPending = true
            return
        }
        do {
            let collector = try await collector()
            let cached = try await collector.cachedSnapshot(weekStart: selectedWeekStart)
            hasLoadedSnapshot = true
            if cached != snapshot {
                snapshot = cached
            }
            invalidateAnalytics(clearSnapshots: false)
            await refreshRequestedAnalytics(using: collector)
            if cached.quality != .exact {
                statusMessage = switch cached.quality {
                case .partial: cached.updatedAt == nil ? "No \(provider.title) usage found" : "Updated"
                case .stale: "Refresh failed"
                case .unavailable: "No \(provider.title) usage found"
                case .error: "Unable to read local usage"
                case .exact: statusMessage
                }
            }
        } catch {
            hasLoadedSnapshot = true
            if snapshot.updatedAt != nil {
                snapshot.quality = .stale
                statusMessage = "Refresh failed"
            } else {
                snapshot.quality = .error
                statusMessage = "Unable to read local usage"
            }
        }
    }

    private func scheduleCalendarBoundary() {
        calendarBoundaryTask?.cancel()
        calendarBoundaryTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let delay = self?.secondsUntilNextCalendarDay() else { return }
                try? await Task.sleep(for: .seconds(delay))
                guard !Task.isCancelled else { return }
                await self?.recalculateVisiblePeriods()
            }
        }
    }

    private func secondsUntilNextCalendarDay(now: Date = Date(), calendar: Calendar = .current) -> Double {
        let start = calendar.startOfDay(for: now)
        let next = calendar.date(byAdding: .day, value: 1, to: start)
            ?? now.addingTimeInterval(3_600)
        return max(1, next.timeIntervalSince(now) + 0.25)
    }

    deinit {
        watcherTask?.cancel()
        debounceTask?.cancel()
        refreshSchedulerTask?.cancel()
        wakeTask?.cancel()
        defaultsTask?.cancel()
        clockChangeTask?.cancel()
        timeZoneChangeTask?.cancel()
        calendarBoundaryTask?.cancel()
        watcher?.stop()
    }
}

enum RefreshMode: String, CaseIterable, Identifiable, Sendable {
    case automatic
    case thirtySeconds = "30"
    case oneMinute = "60"
    case twoMinutes = "120"
    case fiveMinutes = "300"
    case fifteenMinutes = "900"
    case thirtyMinutes = "1800"
    case manual

    var id: String { rawValue }

    var title: String {
        switch self {
        case .automatic: "Automatic"
        case .thirtySeconds: "30 Seconds"
        case .oneMinute: "1 Minute"
        case .twoMinutes: "2 Minutes"
        case .fiveMinutes: "5 Minutes"
        case .fifteenMinutes: "15 Minutes"
        case .thirtyMinutes: "30 Minutes"
        case .manual: "Manual"
        }
    }

    var usesFileEvents: Bool { self == .automatic }

    var pollingInterval: TimeInterval? {
        switch self {
        case .automatic: 60
        case .thirtySeconds: 30
        case .oneMinute: 60
        case .twoMinutes: 120
        case .fiveMinutes: 300
        case .fifteenMinutes: 900
        case .thirtyMinutes: 1_800
        case .manual: nil
        }
    }
}

