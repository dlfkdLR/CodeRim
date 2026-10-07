import Foundation

enum DataQuality: String, Codable, Sendable {
    case exact
    case partial
    case stale
    case unavailable
    case error
}

enum UsagePeriod: String, CaseIterable, Codable, Sendable {
    case today
    case week
    case month
    case allTime
}

struct UsageSnapshot: Equatable, Sendable {
    var today: TokenUsage
    var week: TokenUsage
    var month: TokenUsage
    var allTime: TokenUsage
    /// How complete the current periods (today, week, month) are. Old sessions that could only be
    /// read partly, or history still waiting to be re-read, do not lower it — see `historyIncomplete`.
    var quality: DataQuality
    var updatedAt: Date?
    /// Older history still has unread or partly read sources; all-time and past analytics may be low.
    var historyIncomplete = false

    static let empty = UsageSnapshot(
        today: .zero,
        week: .zero,
        month: .zero,
        allTime: .zero,
        quality: .unavailable,
        updatedAt: nil
    )

    func totals(for period: UsagePeriod) -> TokenUsage {
        switch period {
        case .today: today
        case .week: week
        case .month: month
        case .allTime: allTime
        }
    }
}
