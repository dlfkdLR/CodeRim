import Foundation
import Combine

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

    var series: [SettingsUsageSeries] {
        series(usage: snapshot.usage, models: snapshot.models)
    }

    var points: [SettingsUsagePoint] {
        snapshot.buckets.flatMap { bucket in
            series(usage: bucket.usage, models: bucket.models).map {
                SettingsUsagePoint(date: bucket.start, seriesID: $0.id, title: $0.title, tokens: $0.tokens)
            }
        }
    }

    func series(for bucket: UsageBucket?) -> [SettingsUsageSeries] {
        guard let bucket else { return series }
        return series(usage: bucket.usage, models: bucket.models)
    }

    func bucket(at date: Date?) -> UsageBucket? {
        guard let date else { return nil }
        // Charts selects a position within the whole displayed day, including
        // the unfinished part of today's bar after the last recorded event.
        return snapshot.buckets.first { Calendar.current.isDate(date, inSameDayAs: $0.start) }
    }

    static func rankedSessions(_ sessions: [SessionUsageSummary]) -> [SessionUsageSummary] {
        sessions.sorted {
            if $0.usage.totalTokens != $1.usage.totalTokens {
                return $0.usage.totalTokens > $1.usage.totalTokens
            }
            return $0.id < $1.id
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

enum SettingsUsageSection: String, CaseIterable, Identifiable {
    case overview, analytics, limits
    var id: Self { self }
}


@MainActor
final class SettingsUsageAnalyticsState: ObservableObject {
    @Published var grouping = SettingsUsageGrouping.tokenType
    @Published var selectedDate: Date?
    @Published var showsAllSessions = false
    @Published var expandedSessions: Set<String> = []
    @Published var expandedPeriods: Set<UsagePeriod> = []
}
