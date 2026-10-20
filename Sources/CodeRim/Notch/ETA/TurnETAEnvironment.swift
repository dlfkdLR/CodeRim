import SwiftUI

private struct NotchTurnETAKey: EnvironmentKey {
    static let defaultValue: [String: TurnETATracker.Snapshot] = [:]
}

extension EnvironmentValues {
    /// The live estimate for each working session, keyed by session id.
    var notchTurnETA: [String: TurnETATracker.Snapshot] {
        get { self[NotchTurnETAKey.self] }
        set { self[NotchTurnETAKey.self] = newValue }
    }
}
