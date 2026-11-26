import CodeRimUsageCore
import Foundation
import Combine
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

enum SettingsUsageGrouping: String, CaseIterable, Identifiable {
    case tokenType, model
    var id: Self { self }
    var title: String { self == .tokenType ? "By token type" : "By model" }
}

/// Presentation only: never combines account totals with unattributed local data.
struct SettingsUsageSeries: Identifiable {
    let id: String
    let title: String
    let tokens: Int64
}

struct SettingsUsagePoint: Identifiable {
    let date: Date
    let seriesID: String
    let title: String
    let tokens: Int64
    var id: String { "\(date.timeIntervalSince1970)|\(seriesID)" }
}

struct UsageAnalyticsPresentation {
    let snapshot: AnalyticsSnapshot
    let grouping: SettingsUsageGrouping
    let showsCachedInput: Bool

#if CODERIM_RUST_ACCOUNTING
    private let projection: RustAnalyticsProjection

    init(snapshot: AnalyticsSnapshot, grouping: SettingsUsageGrouping, showsCachedInput: Bool) {
        self.snapshot = snapshot
        self.grouping = grouping
        self.showsCachedInput = showsCachedInput
        projection = RustAnalyticsProjection(models: snapshot.models, grouping: grouping, showsCachedInput: showsCachedInput)
    }
#endif

    var series: [SettingsUsageSeries] {
#if CODERIM_RUST_ACCOUNTING
        if let series = projection.series(models: snapshot.models, usage: snapshot.usage) { return series }
#endif
        return series(usage: snapshot.usage, models: snapshot.models)
    }

    var points: [SettingsUsagePoint] {
#if CODERIM_RUST_ACCOUNTING
        if let points = projection.points(buckets: snapshot.buckets) { return points }
#endif
        return snapshot.buckets.flatMap { bucket in
            series(usage: bucket.usage, models: bucket.models).map {
                SettingsUsagePoint(date: bucket.start, seriesID: $0.id, title: $0.title, tokens: $0.tokens)
            }
        }
    }

    func series(for bucket: UsageBucket?) -> [SettingsUsageSeries] {
        guard let bucket else { return series }
#if CODERIM_RUST_ACCOUNTING
        if let series = projection.series(models: bucket.models) { return series }
#endif
        return series(usage: bucket.usage, models: bucket.models)
    }

    func bucket(at date: Date?) -> UsageBucket? {
        guard let date else { return nil }
        // Charts selects a position within the whole displayed day, including
        // the unfinished part of today's bar after the last recorded event.
        return snapshot.buckets.first {
            date >= $0.start && (date < $0.end || ($0.id == snapshot.buckets.last?.id && date == $0.end))
        }
    }

    static func rankedSessions(_ sessions: [SessionUsageSummary]) -> [SessionUsageSummary] {
        sessions.sorted {
            if $0.usage.totalTokens != $1.usage.totalTokens {
                return $0.usage.totalTokens > $1.usage.totalTokens
            }
            return $0.id < $1.id
        }
    }

    static func matchingSessions(_ sessions: [SessionUsageSummary], query: String) -> [SessionUsageSummary] {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return sessions }
        return sessions.filter {
            $0.displayName.localizedStandardContains(query)
                || $0.id.localizedStandardContains(query)
                || $0.lastActivityAt.formatted(date: .abbreviated, time: .omitted).localizedStandardContains(query)
                || $0.lastActivityAt.formatted(.iso8601).localizedStandardContains(query)
        }
    }

    static func matchingIndexedChats(_ chats: [IndexedChatUsage], query: String) -> [IndexedChatUsage] {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return chats }
        return chats.filter {
            $0.title.localizedStandardContains(query)
                || $0.id.localizedStandardContains(query)
                || ($0.updatedAt?.formatted(date: .abbreviated, time: .omitted)
                    .localizedStandardContains(query) ?? false)
                || ($0.updatedAt?.formatted(.iso8601).localizedStandardContains(query) ?? false)
        }
    }

    private var leadingModels: [ModelUsageSummary] {
        Array(snapshot.models.sorted(by: ModelUsageSummary.byUsageThenName).prefix(5))
    }

    private func series(usage: TokenUsage, models: [ModelUsageSummary]) -> [SettingsUsageSeries] {
        if grouping == .tokenType {
            if showsCachedInput {
                // Cached input is a subset of Input, so the stack must subtract it.
                return [
                    .init(id: "input", title: "Uncached input", tokens: max(0, usage.inputTokens - usage.cachedInputTokens)),
                    .init(id: "cached", title: "Cached input", tokens: usage.cachedInputTokens),
                    .init(id: "output", title: "Output", tokens: usage.outputTokens)
                ]
            }
            return [
                .init(id: "input", title: "Input", tokens: usage.inputTokens),
                .init(id: "output", title: "Output", tokens: usage.outputTokens)
            ]
        }
        let leading = leadingModels
        let leadingIDs = Set(leading.map(\.id))
        var result = leading.map { model in
            SettingsUsageSeries(id: "model:\(model.id)", title: model.displayName,
                                tokens: models.first { $0.id == model.id }?.usage.totalTokens ?? 0)
        }
        if snapshot.models.count > leading.count {
            let other = models.filter { !leadingIDs.contains($0.id) }
                .reduce(TokenUsage.zero) { $0.adding($1.usage) }
            result.append(.init(id: "other-models", title: "Other models", tokens: other.totalTokens))
        }
        return result
    }
}


#if CODERIM_RUST_ACCOUNTING
/// The chart uses Swift's canonical string equality for IDs and its ordering for
/// tied labels. Rust needs only numeric counters and their five possible slots.
private struct RustAnalyticsProjection {
    struct Label {
        let id: String
        let title: String
        let column: Int
    }

    let labels: [Label]
    let masks: [String: UInt8]
    let mode: UInt8

    init(models: [ModelUsageSummary], grouping: SettingsUsageGrouping, showsCachedInput: Bool) {
        if grouping == .model {
            mode = 2
            let leading = models.sorted(by: ModelUsageSummary.byUsageThenName).prefix(5)
            var labels: [Label] = []
            var masks: [String: UInt8] = [:]
            for (index, model) in leading.enumerated() {
                labels.append(Label(id: "model:\(model.id)", title: model.displayName, column: index))
                masks[model.id, default: 0] |= 1 << index
            }
            if models.count > leading.count {
                labels.append(Label(id: "other-models", title: "Other models", column: 5))
            }
            self.labels = labels
            self.masks = masks
        } else {
            masks = [:]
            mode = showsCachedInput ? 1 : 0
            labels = showsCachedInput
                ? [Label(id: "input", title: "Uncached input", column: 0), Label(id: "cached", title: "Cached input", column: 1),
                   Label(id: "output", title: "Output", column: 2)]
                : [Label(id: "input", title: "Input", column: 0), Label(id: "output", title: "Output", column: 1)]
        }
    }

    func series(models: [ModelUsageSummary], usage: TokenUsage? = nil) -> [SettingsUsageSeries]? {
        let stack = stack(models: models, usage: usage)
        guard stack.valid != 0 else { return nil }
        return labels.map { SettingsUsageSeries(id: $0.id, title: $0.title, tokens: stack[$0.column]) }
    }

    func points(buckets: [UsageBucket]) -> [SettingsUsagePoint]? {
        var points: [SettingsUsagePoint] = []
        points.reserveCapacity(buckets.count * labels.count)
        for bucket in buckets {
            let stack = stack(models: bucket.models, usage: nil)
            guard stack.valid != 0 else { return nil }
            for label in labels {
                points.append(SettingsUsagePoint(date: bucket.start, seriesID: label.id, title: label.title,
                                                 tokens: stack[label.column]))
            }
        }
        return points
    }

    private func stack(models: [ModelUsageSummary], usage: TokenUsage?) -> cr_chart_stack {
        // The range summary supplies an independent total. Individual buckets
        // derive it by folding their model rows in the original order.
        if mode != 2, let usage {
            var row = cr_chart_row(input: usage.inputTokens, cached: usage.cachedInputTokens,
                                   output: usage.outputTokens, model_slots: 0)
            return cr_project_chart_stack(&row, 1, mode)
        }
        let rows = models.map { model in
            cr_chart_row(input: model.usage.inputTokens, cached: model.usage.cachedInputTokens,
                         output: model.usage.outputTokens, model_slots: mode == 2 ? masks[model.id] ?? 0 : 0)
        }
        return rows.withUnsafeBufferPointer { cr_project_chart_stack($0.baseAddress, $0.count, mode) }
    }
}

private extension cr_chart_stack {
    subscript(column: Int) -> Int64 {
        switch column {
        case 0: values.0
        case 1: values.1
        case 2: values.2
        case 3: values.3
        case 4: values.4
        default: values.5
        }
    }
}
#endif

enum SettingsUsageSection: String, CaseIterable, Identifiable {
    case overview, analytics, limits
    var id: Self { self }
}


struct SettingsUsageActivityKey: Equatable {
    let provider: UsageProvider
    let range: AnalyticsRange
    let through: Date?
    let sourceUpdatedAt: Date?
    let calendar: Calendar
}

@MainActor
final class SettingsUsageAnalyticsState: ObservableObject {
    @Published var grouping = SettingsUsageGrouping.tokenType
    @Published var selectedDate: Date?
    @Published var showsAllSessions = false
    @Published var sessionSearch = ""
    @Published var expandedSessions: Set<String> = []
    @Published var expandedPeriods: Set<UsagePeriod> = []
    /// What the agents did in the range, read from their logs, and which
    /// provider and range that answers.
    @Published var activity: ActivitySnapshot?
    var activityKey: SettingsUsageActivityKey?
    private var activityRequest: UUID?
    /// How it is read. Tests replace it so they never open the logs on this Mac.
    var loadActivity: (UsageProvider, AnalyticsRange, Date, Calendar) async -> ActivitySnapshot = UsageActivityScanner.loadShared

    func refreshActivity(for key: SettingsUsageActivityKey) async {
        guard !Task.isCancelled else { return }
        let request = UUID()
        activityRequest = request
        if activityKey != key { activity = nil }
        let loaded = await loadActivity(key.provider, key.range, key.through ?? Date(), key.calendar)
        guard activityRequest == request, !Task.isCancelled else { return }
        activityKey = key
        activity = loaded
    }
}
