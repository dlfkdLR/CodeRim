import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

struct UsageRefreshPolicy: Equatable {
    private let seconds: UInt16
    let usesFileEvents: Bool

    init(_ storedValue: String?) {
        #if CODERIM_RUST_ACCOUNTING
        var raw = storedValue ?? ""
        let mode = raw.withUTF8 { cr_usage_refresh_policy($0.baseAddress, $0.count) }
        seconds = mode.interval_seconds
        usesFileEvents = mode.uses_file_events
        #else
        let mode = RefreshMode(rawValue: storedValue ?? "") ?? .automatic
        usesFileEvents = mode == .automatic
        seconds = switch mode {
        case .automatic, .oneMinute: 60
        case .thirtySeconds: 30
        case .twoMinutes: 120
        case .fiveMinutes: 300
        case .fifteenMinutes: 900
        case .thirtyMinutes: 1_800
        case .manual: 0
        }
        #endif
    }

    var pollingInterval: TimeInterval? { seconds == 0 ? nil : TimeInterval(seconds) }

    func shouldPoll(elapsed: TimeInterval?) -> Bool {
        #if CODERIM_RUST_ACCOUNTING
        cr_usage_refresh_poll_due(seconds, elapsed != nil, elapsed ?? 0)
        #else
        seconds != 0 && (elapsed.map { $0 >= TimeInterval(seconds) } ?? true)
        #endif
    }
}

struct UsageRefreshRequests {
    private var pending = false
    private var pendingForce = false

    /// Returns the analytics request for the admitted refresh, or nil if queued.
    mutating func begin(whileBusy busy: Bool, forceAnalytics: Bool) -> Bool? {
        #if CODERIM_RUST_ACCOUNTING
        let admission = cr_usage_refresh_admit(pending, pendingForce, busy, forceAnalytics)
        pending = admission.pending
        pendingForce = admission.pending_force
        return admission.start ? admission.force_analytics : nil
        #else
        let force = pendingForce || forceAnalytics
        guard !busy else {
            pending = true
            pendingForce = force
            return nil
        }
        pendingForce = false
        return force
        #endif
    }

    /// A completed maintenance operation or scheduled continuation consumes the pending flag.
    /// A queued forced analytics request stays live until a refresh starts.
    @discardableResult
    mutating func takePending() -> Bool {
        defer { pending = false }
        return pending
    }
}
