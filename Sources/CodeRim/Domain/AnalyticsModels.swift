import Foundation

enum AnalyticsRange: String, CaseIterable, Identifiable, Hashable, Sendable {
    case today
    case sevenDays
    case thirtyDays

    var id: Self { self }

    var title: String {
        switch self {
        case .today: "Today"
        case .sevenDays: "7D"
        case .thirtyDays: "30D"
        }
    }

    func interval(through end: Date, calendar: Calendar) -> DateInterval {
        let today = calendar.startOfDay(for: end)
        let start = switch self {
        case .today:
            today
        case .sevenDays:
            calendar.date(byAdding: .day, value: -6, to: today) ?? today
        case .thirtyDays:
            calendar.date(byAdding: .day, value: -29, to: today) ?? today
        }
        return DateInterval(start: start, end: end)
    }

    func bucketIntervals(through end: Date, calendar: Calendar) -> [DateInterval] {
        let range = interval(through: end, calendar: calendar)
        let component: Calendar.Component = self == .today ? .hour : .day
        var buckets: [DateInterval] = []
        var cursor = range.start
        while cursor <= end {
            let next = calendar.date(byAdding: component, value: 1, to: cursor)
                ?? cursor.addingTimeInterval(component == .hour ? 3_600 : 86_400)
            buckets.append(DateInterval(start: cursor, end: min(next, end.addingTimeInterval(0.000_001))))
            guard next > cursor, next <= end else { break }
            cursor = next
        }
        return buckets
    }
}

struct ModelUsageSummary: Equatable, Sendable, Identifiable {
    let modelID: String?
    let usage: TokenUsage
    let highContextUsage: TokenUsage
    let hasUnknownPricingContext: Bool

    init(
        modelID: String?,
        usage: TokenUsage,
        highContextUsage: TokenUsage = .zero,
        hasUnknownPricingContext: Bool = false
    ) {
        self.modelID = modelID
        self.usage = usage
        self.highContextUsage = highContextUsage
        self.hasUnknownPricingContext = hasUnknownPricingContext
    }

    var id: String { modelID ?? "unknown-model" }
    var displayName: String { modelID ?? "Unknown Model" }

    /// Ordering for the analytics lists.
    ///
    /// Every one of these rows is built by mapping a `Dictionary`, whose order is
    /// arbitrary and reseeded per process, and `sorted(by:)` is not stable — so a
    /// comparator that only looks at the token total leaves rows that *tie* in
    /// whatever order the sort happened to land them. Two projects with the same
    /// total would swap places between refreshes, and again after a relaunch, for
    /// no reason the user can see.
    ///
    /// Each comparator therefore ends in a total order: a tiebreaker on identity,
    /// which never ties, so the same data always produces the same list.
    static func byUsageThenName(_ lhs: Self, _ rhs: Self) -> Bool {
        guard lhs.usage.totalTokens == rhs.usage.totalTokens else {
            return lhs.usage.totalTokens > rhs.usage.totalTokens
        }
        return lhs.id < rhs.id
    }
}

struct UsageBucket: Equatable, Sendable, Identifiable {
    let start: Date
    let end: Date
    let models: [ModelUsageSummary]

    var id: Date { start }
    var usage: TokenUsage {
        models.reduce(TokenUsage.zero) { $0.adding($1.usage) }
    }
}

struct ProjectUsageSummary: Equatable, Sendable, Identifiable {
    let id: String
    let name: String
    let usage: TokenUsage
    let models: [ModelUsageSummary]
    let sessionCount: Int

    /// See `ModelUsageSummary.byUsageThenName`. Name first so a tie reads
    /// alphabetically rather than by an opaque identifier, then the id, which
    /// is unique and settles two projects that share a name.
    static func byUsageThenName(_ lhs: Self, _ rhs: Self) -> Bool {
        guard lhs.usage.totalTokens == rhs.usage.totalTokens else {
            return lhs.usage.totalTokens > rhs.usage.totalTokens
        }
        guard lhs.name == rhs.name else { return lhs.name < rhs.name }
        return lhs.id < rhs.id
    }
}

struct SessionUsageSummary: Equatable, Sendable, Identifiable {
    let id: String
    let projectID: String?
    let projectName: String?
    let startedAt: Date?
    let lastActivityAt: Date
    let usage: TokenUsage
    let models: [ModelUsageSummary]
    let directSubagentCount: Int
    let imageAttachmentCount: Int
    let parentSessionID: String?

    var displayName: String {
        if let projectName, !projectName.isEmpty { return projectName }
        return "Session \(id.prefix(8))"
    }

    /// See `ModelUsageSummary.byUsageThenName`. Sessions recorded in the same
    /// second are common — a batch import gives a whole run one timestamp.
    static func byActivityThenID(_ lhs: Self, _ rhs: Self) -> Bool {
        guard lhs.lastActivityAt == rhs.lastActivityAt else {
            return lhs.lastActivityAt > rhs.lastActivityAt
        }
        return lhs.id < rhs.id
    }
}

struct AnalyticsSnapshot: Equatable, Sendable {
    let range: AnalyticsRange
    let interval: DateInterval
    let through: Date
    let usage: TokenUsage
    let quality: DataQuality
    let buckets: [UsageBucket]
    let models: [ModelUsageSummary]
    let projects: [ProjectUsageSummary]
    let sessions: [SessionUsageSummary]

    static func empty(range: AnalyticsRange, through: Date, calendar: Calendar) -> AnalyticsSnapshot {
        AnalyticsSnapshot(
            range: range,
            interval: range.interval(through: through, calendar: calendar),
            through: through,
            usage: .zero,
            quality: .unavailable,
            buckets: [],
            models: [],
            projects: [],
            sessions: []
        )
    }
}
