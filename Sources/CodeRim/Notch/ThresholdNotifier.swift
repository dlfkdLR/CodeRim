import Foundation
import UserNotifications
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

/// One alert-worthy crossing of a provider's limit.
struct ThresholdAlert: Equatable {
    /// 80 or 100 — the two crossings worth interrupting someone for.
    let threshold: Int
    let providerID: String
    let providerName: String
    let windowLabel: String
    let usedPercent: Int
    let resetsAt: Date?
}

/// Watches the store's snapshots and reports the moment a provider's headline
/// limit crosses 80% or reaches 100%.
///
/// Crossing, not level: a provider parked at 91% must not alert twice, so the
/// highest threshold currently crossed is remembered. The memory clears when
/// the reading drops back below the first threshold, or when the window's reset
/// moves to a later period — the next climb is a new fact worth announcing.
///
/// Remembered per provider and account: reaching 100% on one account says
/// nothing about the account switched to next.
///
/// The delivery is injected rather than reached for, so the whole rule is
/// testable without ever touching the notification centre.
@MainActor
final class ThresholdNotifier {
    private struct Memory {
        var window: String?
        #if CODERIM_RUST_ACCOUNTING
        var crossing = CRThresholdMemory()
        #else
        var level: Int
        var resetsAt: Date?
        #endif
    }

    /// A reset that moves by more than this is a new period, not clock jitter in "resets in N s".
    private static let newPeriodShift: TimeInterval = 30 * 60
    private var crossed: [String: Memory] = [:]
    private let isMuted: (String) -> Bool
    private let deliver: (ThresholdAlert) -> Void

    init(isMuted: @escaping (String) -> Bool = { _ in false },
         deliver: @escaping (ThresholdAlert) -> Void = { _ in }) {
        self.isMuted = isMuted
        self.deliver = deliver
    }

    func observe(_ snapshots: [ProviderSnapshot]) {
        for snapshot in snapshots {
            observe(snapshot)
        }
    }

    private func observe(_ snapshot: ProviderSnapshot) {
        #if CODERIM_RUST_ACCOUNTING
        guard let headline = snapshot.headline, let fraction = headline.usedFraction, fraction.isFinite else { return }
        let key = snapshot.id + "|" + (snapshot.accountIdentity ?? "")
        let transition = Self.advance(&crossed[key, default: Memory()], headline: headline, fraction: fraction)
        // End the dictionary borrow before isMuted or deliver can reenter the notifier.
        guard transition.alerts != 0, !isMuted(snapshot.id) else { return }
        if Int(transition.alerts) & CR_THRESHOLD_CROSSED_80 != 0 {
            emit(80, percent: Int(transition.used_percent), headline: headline, snapshot: snapshot)
        }
        if Int(transition.alerts) & CR_THRESHOLD_CROSSED_100 != 0 {
            emit(100, percent: Int(transition.used_percent), headline: headline, snapshot: snapshot)
        }
        #else
        // A provider can report any finite number (Copilot: used 1e100 of 1); converting that to
        // `Int` would trap, so the percentage is bounded before anything is derived from it.
        guard let fraction = snapshot.usedFraction, fraction.isFinite else { return }
        let percent = min(max(fraction * 100, 0), 1_000)
        let level = percent >= 100 ? 100 : percent >= 80 ? 80 : 0
        let headline = snapshot.headline

        let key = snapshot.id + "|" + (snapshot.accountIdentity ?? "")
        var memory = crossed[key] ?? Memory(window: nil, level: 0, resetsAt: nil)
        let sameWindow = memory.window == headline?.id
        // A reset that moved to a later period is a new period, whatever the window is called: Codex names
        // its windows by their reset time, so a new period also arrives under a new window id.
        if let next = headline?.resetsAt, let last = memory.resetsAt,
           next.timeIntervalSince(last) > Self.newPeriodShift {
            memory.level = 0   // a new limit period began while the reading was not seen low
        }
        let previous = memory.level
        crossed[key] = Memory(window: headline?.id, level: level,
                              resetsAt: headline?.resetsAt ?? (sameWindow ? memory.resetsAt : nil))
        guard level > previous, !isMuted(snapshot.id) else { return }

        guard let headline else { return }
        for threshold in [80, 100] where threshold > previous && threshold <= level {
            emit(threshold, percent: Int(percent.rounded()), headline: headline, snapshot: snapshot)
        }
        #endif
    }

    #if CODERIM_RUST_ACCOUNTING
    private static func advance(_ memory: inout Memory, headline: LimitWindow, fraction: Double) -> CRThresholdTransition {
        let reset = headline.resetsAt
        let transition = cr_threshold_observe(memory.crossing, fraction, reset?.timeIntervalSinceReferenceDate ?? 0,
                                              reset != nil, memory.window == headline.id)
        memory.crossing = transition.memory
        memory.window = headline.id
        return transition
    }
    #endif

    private func emit(_ threshold: Int, percent: Int, headline: LimitWindow, snapshot: ProviderSnapshot) {
        deliver(ThresholdAlert(threshold: threshold, providerID: snapshot.id, providerName: snapshot.displayName,
                               windowLabel: headline.label, usedPercent: percent, resetsAt: headline.resetsAt))
    }
}

/// The side end of `ThresholdNotifier`: permission asked lazily, on the first
/// crossing rather than at launch — a prompt in the first seconds of a first
/// run reads as an app grabbing, one earned by a real event reads as a service.
enum ThresholdAlerts {
    static func deliver(_ alert: ThresholdAlert) {
        // `center` is fetched fresh inside the completion handler rather than
        // captured: `UNUserNotificationCenter` is not `Sendable`, and the
        // handler is, so a capture would warn under Swift 6. `current()` is the
        // same singleton wherever it is called.
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert]) { granted, _ in
            guard granted else { return }
            let center = UNUserNotificationCenter.current()

            let content = UNMutableNotificationContent()
            content.title = alert.threshold >= 100
                ? "\(alert.providerName) limit reached"
                : "\(alert.providerName) is at \(alert.usedPercent)%"
            if alert.threshold >= 100 {
                content.body = "Its \(alert.windowLabel.lowercased()) limit is spent"
                    + (alert.resetsAt.map { " — resets \($0.formatted(date: .omitted, time: .shortened))" } ?? ".")
            } else {
                content.body = "\(alert.usedPercent)% of its \(alert.windowLabel.lowercased()) limit used."
            }
            // One thread per provider, so two limits ending together read as
            // two notes, not one merged pile.
            content.threadIdentifier = alert.providerID

            let request = UNNotificationRequest(
                identifier: "\(alert.providerID).\(alert.threshold).\(Int(Date().timeIntervalSince1970))",
                content: content, trigger: nil)
            center.add(request)
        }
    }
}
