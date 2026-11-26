import CodeRimUsageCore
import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

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
#if CODERIM_RUST_ACCOUNTING
        let periodTag: Int = switch period {
        case .today: CR_HISTORY_TODAY
        case .week: CR_HISTORY_WEEK
        case .month: CR_HISTORY_MONTH
        case .allTime: CR_HISTORY_ALL_TIME
        }
        let source = cr_history_total_source(scope == .account ? 1 : 0, UInt8(periodTag), profileSnapshot == nil ? 0 : 1)
        return switch Int(source) {
        case CR_HISTORY_LOCAL: localSnapshot.totals(for: period).totalTokens
        case CR_HISTORY_ACCOUNT_WEEK: profileSnapshot?.week
        case CR_HISTORY_ACCOUNT_MONTH: profileSnapshot?.month
        case CR_HISTORY_ACCOUNT_LIFETIME: profileSnapshot?.lifetime
        default: nil
        }
#else
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
#endif
    }

    static func historyContext(scope: UsageHistoryScope, profileSnapshot: ProfileUsageSnapshot?) -> String {
        guard scope == .account else { return "This Mac" }
        guard let profileSnapshot else { return "Unavailable" }
        return "Through \(profileSnapshot.statsAsOf.formatted(.dateTime.month(.abbreviated).day()))"
    }

    static let accountHistoryHelp = "ChatGPT's account total already includes synced local and cloud usage. "
        + "Local totals are not added again. Recent usage appears after the server updates its totals. "
        + "This Mac's live usage and account-unassigned history remain available separately."

    static let localHistoryHelp = "Local session usage on this Mac across accounts. "
        + "Switching accounts does not reset or reassign this history."
}

/// The store performs invalidation and scheduling on its existing main-actor lifecycle.
struct ProfileSyncDecision {
    let enabled: Bool
    let invalidateSnapshot: Bool
    let scheduleRefresh: Bool

    init(allowed: Bool, requested: @autoclosure () -> Bool, wasEnabled: Bool = false,
         weekStartChanged: Bool = false) {
        // Live-only stores never read the saved profile-sync preference.
        let preference = allowed ? requested() : false
#if CODERIM_RUST_ACCOUNTING
        let decision = cr_profile_sync_policy(allowed ? 1 : 0, preference ? 1 : 0,
                                              wasEnabled ? 1 : 0, weekStartChanged ? 1 : 0)
        enabled = decision.enabled == 1
        invalidateSnapshot = decision.invalidate_snapshot == 1
        scheduleRefresh = decision.schedule_refresh == 1
#else
        enabled = allowed && preference
        invalidateSnapshot = enabled && weekStartChanged
        scheduleRefresh = enabled && (!wasEnabled || weekStartChanged)
#endif
    }
}
