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
    var quality: DataQuality
    var updatedAt: Date?

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
