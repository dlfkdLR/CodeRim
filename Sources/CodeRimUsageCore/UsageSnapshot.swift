import Foundation

package enum DataQuality: String, Codable, Sendable {
    case exact
    case partial
    case stale
    case unavailable
    case error
}

package enum UsagePeriod: String, CaseIterable, Codable, Sendable {
    case today
    case week
    case month
    case allTime
}

package struct UsageSnapshot: Equatable, Sendable {
    package var today: TokenUsage
    package var week: TokenUsage
    package var month: TokenUsage
    package var allTime: TokenUsage
    /// How complete the current periods (today, week, month) are. Old sessions that could only be
    /// read partly, or history still waiting to be re-read, do not lower it — see `historyIncomplete`.
    package var quality: DataQuality
    package var updatedAt: Date?
    /// Older history still has unread or partly read sources; all-time and past analytics may be low.
    package var historyIncomplete = false

    package init(today: TokenUsage, week: TokenUsage, month: TokenUsage, allTime: TokenUsage,
                 quality: DataQuality, updatedAt: Date?, historyIncomplete: Bool = false) {
        self.today = today; self.week = week; self.month = month; self.allTime = allTime
        self.quality = quality; self.updatedAt = updatedAt; self.historyIncomplete = historyIncomplete
    }

    package static let empty = UsageSnapshot(
        today: .zero,
        week: .zero,
        month: .zero,
        allTime: .zero,
        quality: .unavailable,
        updatedAt: nil
    )

    package func totals(for period: UsagePeriod) -> TokenUsage {
        switch period {
        case .today: today
        case .week: week
        case .month: month
        case .allTime: allTime
        }
    }
}
