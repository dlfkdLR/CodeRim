import Combine
import CodeRimShared
import Foundation
import WidgetKit
import os

/// One sanitized snapshot for all installed providers, independent of notch visibility.
@MainActor
final class CompanionSnapshotPublisher {
    private static let log = Logger(subsystem: "dev.codexmeter.CodexMeter", category: "companion")
    private var subscriptions: Set<AnyCancellable> = []
    private var lastWidgetReload: Date?
    private var lastWidgetPublished: CompanionSnapshot?
    private lazy var cliWriter = CompanionSnapshotWriter(label: "dev.codexmeter.companion.cli", write: { snapshot in
        try CompanionSnapshotFile.write(snapshot, to: CompanionSnapshotFile.cliURL)
    }, completion: { [weak self] snapshot, success in
        Task { @MainActor in
            if !success { Self.log.error("Could not publish CLI usage snapshot.") }
            if CompanionSnapshotFile.usesLocalFile { self?.didPublishWidget(snapshot, success: success) }
        }
    })
    private lazy var widgetWriter = CompanionSnapshotWriter(label: "dev.codexmeter.companion.widget", write: { snapshot in
        // Resolving an App Group URL may itself require filesystem/permission work.
        guard let url = CompanionSnapshotFile.widgetURL else { throw CompanionSnapshotFile.SnapshotError.invalidFile }
        try CompanionSnapshotFile.write(snapshot, to: url)
    }, completion: { [weak self] snapshot, success in
        Task { @MainActor in self?.didPublishWidget(snapshot, success: success) }
    })
    private var requestedHistory: Set<UsageProvider> = []
    private var localPeriodsAsOf: [UsageProvider: Date] = [:]
    private let codex: UsageStore
    private let claude: UsageStore
    private let limits: AccountLimitStore
    private let claudeIntegration: ClaudeIntegrationStore
    private let notch: NotchController

    init(codex: UsageStore, claude: UsageStore, limits: AccountLimitStore,
         claudeIntegration: ClaudeIntegrationStore, notch: NotchController = .shared) {
        self.codex = codex
        self.claude = claude
        self.limits = limits
        self.claudeIntegration = claudeIntegration
        self.notch = notch
        // UsageSnapshot.updatedAt is the last event date, not the aggregation date.
        // Every newly emitted aggregate was calculated for the current calendar.
        for store in [codex, claude] {
            store.$lastSourceRefreshAt.dropFirst().compactMap { $0 }.sink { [weak self, weak store] date in
                guard let store else { return }
                self?.localPeriodsAsOf[store.provider] = date
            }.store(in: &subscriptions)
            store.$snapshot.dropFirst().sink { [weak self, weak store] _ in
                guard let store else { return }
                self?.localPeriodsAsOf[store.provider] = Date()
            }.store(in: &subscriptions)
        }
        Publishers.MergeMany([
            codex.objectWillChange.eraseToAnyPublisher(),
            claude.objectWillChange.eraseToAnyPublisher(),
            limits.objectWillChange.eraseToAnyPublisher(),
            claudeIntegration.objectWillChange.eraseToAnyPublisher(),
            notch.objectWillChange.eraseToAnyPublisher()
        ])
        .debounce(for: .milliseconds(150), scheduler: RunLoop.main)
        .sink { [weak self] _ in self?.publish() }
        .store(in: &subscriptions)
        publish()
    }

    func publish(now: Date = Date()) {
        requestHistoryIfNeeded()
        let codexState: CompanionState = switch limits.status {
        case .ready: .ready
        case .stale: .stale
        case .disabled: .disabled
        case .loading: .loading
        case .unavailable: .unavailable
        }
        let claudeState: CompanionState = switch claudeIntegration.status {
        case .ready: .ready
        case .stale: .stale
        case .disabled: .disabled
        case .checking: .loading
        case .needsAccount: .needsAuth
        default: .unavailable
        }
        let readings = Dictionary(notch.snapshots.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let providers = CompanionProviderID.allCases.map { id -> CompanionProvider in
            guard notch.selectedProviderIDs.contains(id.rawValue) else {
                return Self.disabled(id)
            }
            switch id {
            case .codex:
                return CompanionProvider(id: id.rawValue, name: id.name,
                    localUsage: Self.localUsage(codex, calculatedAt: localPeriodsAsOf[.codex]),
                    limits: Self.limitUsage(limits.snapshot, state: codexState,
                        codexPlan: CodexAccountStore.shared.currentPlanType),
                    history: Self.history(codex.analyticsSnapshot(for: .thirtyDays)),
                    plan: CodexAccountStore.shared.currentPlanType)
            case .claude:
                return CompanionProvider(id: id.rawValue, name: id.name,
                    localUsage: claudeIntegration.isEnabled
                        ? Self.localUsage(claude, calculatedAt: localPeriodsAsOf[.claude]) : nil,
                    limits: Self.limitUsage(claudeIntegration.snapshot, state: claudeState),
                    history: claudeIntegration.isEnabled ? Self.history(claude.analyticsSnapshot(for: .thirtyDays)) : nil)
            default:
                return Self.provider(id, snapshot: readings[id.rawValue],
                                     updatedAt: notch.lastUpdatedAt(providerID: id.rawValue))
            }
        }
        let snapshot = CompanionSnapshot(generatedAt: now, providers: providers)
        cliWriter.submit(snapshot)
        if !CompanionSnapshotFile.usesLocalFile { widgetWriter.submit(snapshot) }
    }

    private func didPublishWidget(_ snapshot: CompanionSnapshot, success: Bool) {
        guard success else { Self.log.error("Could not publish widget usage snapshot."); return }
        let now = Date()
        let statesChanged = lastWidgetPublished?.providers.map { $0.limits.state }
            != snapshot.providers.map { $0.limits.state }
        if statesChanged || lastWidgetReload == nil || now.timeIntervalSince(lastWidgetReload!) >= 60 {
            WidgetCenter.shared.reloadAllTimelines()
            lastWidgetReload = now
        }
        lastWidgetPublished = snapshot
    }


    private func requestHistoryIfNeeded() {
        for store in [codex, claude] {
            guard notch.selectedProviderIDs.contains(store.provider.rawValue),
                  store.provider != .claude || claudeIntegration.isEnabled,
                  requestedHistory.insert(store.provider).inserted else { continue }
            Task { [weak store] in await store?.refreshAnalytics(range: .thirtyDays) }
        }
    }

    static func history(_ snapshot: AnalyticsSnapshot?) -> CompanionHistory? {
        guard let snapshot, snapshot.quality != .unavailable, snapshot.quality != .error else { return nil }
        let costs = CostChartSummary(snapshot: snapshot)
        let days = costs.buckets.map { value in
            CompanionHistoryDay(date: value.bucket.start, tokens: value.bucket.usage.totalTokens,
                estimatedCostUSD: value.cost.amountUSD.map { NSDecimalNumber(decimal: $0).doubleValue },
                costIsPartial: value.cost.isPartial)
        }
        let state: CompanionState = snapshot.quality == .stale ? .stale : snapshot.quality == .partial ? .partial : .ready
        return CompanionHistory(state: state, updatedAt: snapshot.through, days: days,
            totalTokens: snapshot.usage.totalTokens,
            estimatedCostUSD: costs.total.amountUSD.map { NSDecimalNumber(decimal: $0).doubleValue },
            costIsPartial: costs.total.isPartial)
    }

    static func disabled(_ id: CompanionProviderID) -> CompanionProvider {
        CompanionProvider(id: id.rawValue, name: id.name, localUsage: nil,
            limits: CompanionLimits(state: .disabled, updatedAt: nil, windows: [],
                                    message: "Add this provider in CodeRim Settings."),
            enabled: false)
    }

    static func provider(_ id: CompanionProviderID, snapshot: ProviderSnapshot?, updatedAt: Date?) -> CompanionProvider {
        guard let snapshot else {
            return CompanionProvider(id: id.rawValue, name: id.name, localUsage: nil,
                limits: CompanionLimits(state: .unavailable, updatedAt: nil, windows: [],
                    message: id == .ollamaLocal ? "Start Ollama and load a model." : "Open CodeRim to check the connection."))
        }
        let state: CompanionState = switch snapshot.status {
        case .ok: .ready
        case .stale: .stale
        case .needsAuth: .needsAuth
        case .accessDenied: .accessDenied
        case .unsupported: .unsupported
        case .error: .unavailable
        }
        let message: String? = switch state {
        case .needsAuth: "Configure this provider in CodeRim Settings, then refresh."
        case .accessDenied: "Allow access in CodeRim Settings."
        case .unsupported: snapshot.statusMessage ?? "Check this provider in CodeRim Settings."
        case .unavailable: "Usage is temporarily unavailable."
        default: nil
        }
        let windows: [CompanionLimitWindow] = (state == .ready || state == .stale) ? snapshot.windows.map {
            CompanionLimitWindow(id: $0.id,
                name: [$0.group, $0.label].compactMap { $0 }.joined(separator: " · "),
                durationMinutes: Int(($0.duration ?? 0) / 60),
                usedPercent: $0.usedFraction.map { $0 * 100 }, resetsAt: $0.resetsAt,
                remainingCount: $0.remaining, usedCount: $0.used,
                unit: id == .ollamaLocal ? ($0.id == "loaded" ? "models" : "MB") : nil, displayValue: $0.displayValue)
        } : []
        return CompanionProvider(id: id.rawValue, name: id.name, localUsage: nil,
            limits: CompanionLimits(state: state, updatedAt: updatedAt, windows: windows,
                staleAfterSeconds: 900, message: message, headlineID: snapshot.headlineID),
            fidelity: snapshot.fidelity.rawValue, plan: snapshot.accountPlan)
    }

    static func localUsage(_ store: UsageStore, calculatedAt: Date?) -> CompanionLocalUsage? {
        guard store.hasLoadedSnapshot, let calculatedAt else { return nil }
        let snapshot = store.snapshot
        let state: CompanionState = switch snapshot.quality {
        case .exact: .ready
        case .partial: .partial
        case .stale: .stale
        case .unavailable, .error: .unavailable
        }
        var totals: [String: CompanionTokens] = [:]
        for (period, value) in [
            (CompanionPeriod.today, snapshot.today), (.week, snapshot.week),
            (.month, snapshot.month), (.allTime, snapshot.allTime)
        ] {
            totals[period.rawValue] = CompanionTokens(inputTokens: value.inputTokens,
                cachedInputTokens: value.cachedInputTokens, outputTokens: value.outputTokens)
        }
        return CompanionLocalUsage(state: state, updatedAt: store.lastSourceRefreshAt,
            periodsAsOf: calculatedAt, totals: totals)
    }

    static func limitUsage(_ snapshot: AccountLimitsSnapshot?, state: CompanionState,
                           codexPlan: String? = nil) -> CompanionLimits {
        let visible = state == .ready || state == .stale
        let source = CodexPlanLimits.visibleWindows(snapshot?.windows ?? [], plan: codexPlan)
        let namesAreNeeded = Set(source.map(\.displayName)).count > 1
        let windows = visible ? source.map {
            CompanionLimitWindow(id: $0.id, name: namesAreNeeded ? "\($0.displayName) · \($0.windowLabel)" : $0.windowLabel,
                durationMinutes: $0.windowDurationMinutes, usedPercent: $0.usedPercent,
                resetsAt: $0.resetsAt)
        } : []
        return CompanionLimits(state: state, updatedAt: visible ? snapshot?.fetchedAt : nil,
            windows: windows, headlineID: NotchLimitMapping.headlineID(source))
    }
}
