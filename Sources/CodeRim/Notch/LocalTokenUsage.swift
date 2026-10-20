import Foundation

/// Local transcript totals have a different lifetime and scope from account quotas.
struct LocalTokenUsage: Equatable {
    static let scopeHelp = "Tokens recorded on this Mac since midnight in its current time zone, across accounts and sessions, including background agents. Cached input is included in the total. Account quota percentages use their own reset periods."

    let total: Int?
    let quality: DataQuality
    let isLoading: Bool

    init(snapshot: UsageSnapshot, hasLoaded: Bool) {
        isLoading = !hasLoaded
        quality = snapshot.quality
        switch snapshot.quality {
        case .exact, .partial, .stale:
            total = hasLoaded ? Int(exactly: snapshot.today.totalTokens) : nil
        case .unavailable, .error:
            total = nil
        }
    }

    func text(style: TokenNumberStyle) -> String {
        guard let total else { return isLoading ? "Loading…" : "Unavailable" }
        let value = NotchNumberFormatting.count(total, style: style) + " tokens"
        switch quality {
        case .stale: return value + " (stale)"
        default: return value
        }
    }
}
