import Foundation

enum UsageHistoryScope: String, Hashable, Sendable {
    case local
    case account

    var title: String { self == .local ? "This Mac" : "ChatGPT account" }
}

enum UsageDisplayPolicy {
    static func displayedTotal(
        for period: UsagePeriod,
        scope: UsageHistoryScope,
        localSnapshot: UsageSnapshot,
        profileSnapshot: ProfileUsageSnapshot?
    ) -> Int64? {
        if scope == .local { return localSnapshot.totals(for: period).totalTokens }
        // Local logs have no account identity. Neither a date cutoff nor a
        // larger local total can make them safe to add to account-wide usage.
        guard let profileSnapshot else { return nil }
        return switch period {
        // The profile's today bucket belongs to statsAsOf, which can be an old day.
        case .today: nil
        case .week: profileSnapshot.week
        case .month: profileSnapshot.month
        case .allTime: profileSnapshot.lifetime
        }
    }

    static func historyContext(scope: UsageHistoryScope, profileSnapshot: ProfileUsageSnapshot?) -> String {
        guard scope == .account else { return "This Mac" }
        guard let profileSnapshot else { return "Unavailable" }
        return "Through \(profileSnapshot.statsAsOf.formatted(.dateTime.month(.abbreviated).day()))"
    }

    static let accountHistoryHelp = "Account totals reported by ChatGPT through the displayed snapshot date. "
        + "Recent activity appears when ChatGPT updates its totals. This Mac's live usage is shown separately."

    static let localHistoryHelp = "Local session usage on this Mac across accounts. "
        + "Switching accounts does not reset or reassign this history."
}
