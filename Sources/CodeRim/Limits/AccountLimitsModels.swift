import Foundation

struct AccountLimitWindow: Equatable, Sendable, Identifiable {
    private static let maximumPaceWindowMinutes = 366 * 24 * 60

    let id: String
    let limitID: String
    let displayName: String
    let windowDurationMinutes: Int
    let usedPercent: Double
    let resetsAt: Date?

    var remainingPercent: Double {
        min(100, max(0, 100 - usedPercent))
    }

    var windowLabel: String {
        switch windowDurationMinutes {
        case 270...330: return "5 hours"
        case 9_000...11_000: return "Weekly"
        default:
            if windowDurationMinutes >= 1_440 {
                let days = windowDurationMinutes / 1_440
                return "\(days) \(days == 1 ? "day" : "days")"
            }
            return "\(windowDurationMinutes) \(windowDurationMinutes == 1 ? "minute" : "minutes")"
        }
    }

    func pace(at now: Date) -> AccountLimitPace? {
        guard windowDurationMinutes > 0,
              windowDurationMinutes <= Self.maximumPaceWindowMinutes,
              let resetsAt,
              resetsAt > now else {
            return nil
        }

        let duration = TimeInterval(windowDurationMinutes) * 60
        guard duration.isFinite, duration > 0 else { return nil }
        let startsAt = resetsAt.addingTimeInterval(-duration)
        let elapsed = now.timeIntervalSince(startsAt)
        guard elapsed > 0 else { return nil }

        let expectedUsedPercent = min(100, max(0, elapsed / duration * 100))
        guard expectedUsedPercent >= 3 else { return nil }

        let observedUsedPercent = min(100, max(0, usedPercent))
        let difference = observedUsedPercent - expectedUsedPercent
        let state: AccountLimitPace.State
        if abs(difference) < 1 {
            state = .onPace
        } else if difference > 0 {
            state = .ahead
        } else {
            state = .reserve
        }

        let projectedExhaustion: Date?
        if observedUsedPercent > 0 {
            let usedPerSecond = observedUsedPercent / elapsed
            let secondsUntilExhaustion = (100 - observedUsedPercent) / usedPerSecond
            let projected = now.addingTimeInterval(secondsUntilExhaustion)
            projectedExhaustion = projected < resetsAt ? projected : nil
        } else {
            projectedExhaustion = nil
        }

        return AccountLimitPace(
            state: state,
            differencePercent: abs(difference),
            projectedExhaustion: projectedExhaustion
        )
    }
}

struct AccountLimitPace: Equatable, Sendable {
    enum State: Equatable, Sendable {
        case ahead
        case onPace
        case reserve
    }

    let state: State
    let differencePercent: Double
    let projectedExhaustion: Date?

    var summary: String {
        switch state {
        case .ahead:
            "\(Int(differencePercent.rounded()))% above even pace"
        case .onPace:
            "On even pace"
        case .reserve:
            "\(Int(differencePercent.rounded()))% below even pace"
        }
    }

    var compactSummary: String {
        switch state {
        case .ahead:
            "\(Int(differencePercent.rounded()))% above pace"
        case .onPace:
            "On pace"
        case .reserve:
            "\(Int(differencePercent.rounded()))% below pace"
        }
    }
}

struct ResetCreditSummary: Equatable, Sendable {
    let availableCount: Int?
    let unlimited: Bool
    let expiresAt: Date?
}

struct AccountLimitsSnapshot: Equatable, Sendable {
    let windows: [AccountLimitWindow]
    let resetCredits: ResetCreditSummary?
    let fetchedAt: Date
}

enum AccountLimitStatus: Equatable, Sendable {
    case disabled
    case loading
    case ready
    case stale
    case unavailable

    var allowsPaceEstimates: Bool {
        self == .ready
    }
}

enum AccountLimitError: Error, LocalizedError, Equatable {
    case trustedAppServerNotFound
    case processLaunchFailed
    case timedOut
    case responseTooLarge
    case malformedResponse
    case server(String)

    var errorDescription: String? {
        switch self {
        case .trustedAppServerNotFound:
            "A trusted Codex app-server was not found."
        case .processLaunchFailed:
            "Codex app-server could not be started."
        case .timedOut:
            "Codex account limits timed out."
        case .responseTooLarge:
            "Codex app-server returned too much data."
        case .malformedResponse:
            "Codex app-server returned an unsupported response."
        case let .server(message):
            message
        }
    }
}

/// Shared by the compact summary and the detail page; never freezes at "just now".
enum LimitFreshness {
    static func text(fetchedAt: Date, now: Date = Date(), stale: Bool = false) -> String {
        let age = max(0, now.timeIntervalSince(fetchedAt))
        let elapsed: String
        switch age {
        case ..<5: elapsed = "just now"
        case ..<60: elapsed = "\(Int(age)) sec ago"
        case ..<3_600: elapsed = "\(Int(age / 60)) min ago"
        case ..<86_400: elapsed = "\(Int(age / 3_600)) hr ago"
        default:
            let days = Int(age / 86_400)
            elapsed = "\(days) \(days == 1 ? "day" : "days") ago"
        }
        return stale ? "Last known · updated \(elapsed)" : "Updated \(elapsed)"
    }
}
