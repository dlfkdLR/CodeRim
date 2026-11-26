import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

struct UsagePace {
    /// Used quota minus elapsed time, expressed in percentage points.
    let percentagePoints: Double

    var isDeficit: Bool { percentagePoints > 0 }

    var summary: String {
        #if CODERIM_RUST_ACCOUNTING
        let number = cr_quota_pace_magnitude(percentagePoints)
        if number.style != CR_QUOTA_UNAVAILABLE {
            return "\(number.displayText)% \(isDeficit ? "deficit" : "reserved")"
        }
        #endif
        let magnitude = abs(percentagePoints)
        let rounded = (magnitude * 10).rounded() / 10
        let value = rounded == 0 && magnitude > 0
            ? "<0.1"
            : String(format: "%.1f", locale: Locale(identifier: "en_US_POSIX"), rounded)
                .replacingOccurrences(of: ".0", with: "")
        return "\(value)% \(isDeficit ? "deficit" : "reserved")"
    }
}

extension LimitWindow {
    func usagePace(now: Date) -> UsagePace? {
        #if CODERIM_RUST_ACCOUNTING
        guard let usedFraction, let duration, let resetsAt else { return nil }
        let points = cr_quota_notch_pace(usedFraction, duration, resetsAt.timeIntervalSince(now))
        return points.isNaN ? nil : UsagePace(percentagePoints: points)
        #else
        guard let usedFraction, usedFraction.isFinite, usedFraction >= 0,
              let duration, duration.isFinite, duration > 0,
              let resetsAt else { return nil }
        let remainingTime = resetsAt.timeIntervalSince(now)
        guard remainingTime.isFinite, remainingTime > 0 else { return nil }
        // Provider and device clocks can put a fresh reset just beyond one full cycle.
        let remainingFraction = min(remainingTime / duration, 1)
        let elapsedFraction = 1 - remainingFraction
        // Once the allowance is exhausted there is no negative quota to account for.
        let spentFraction = min(usedFraction, 1)
        return UsagePace(
            percentagePoints: (spentFraction - elapsedFraction) * 100
        )
        #endif
    }
}
