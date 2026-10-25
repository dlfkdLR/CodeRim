import SwiftUI

private struct NotchTurnETAKey: EnvironmentKey {
    static let defaultValue: [String: TurnETATracker.Snapshot] = [:]
}

/// A session row the pointer is over, and where it is in the panel.
struct SessionHover: Equatable {
    let id: String
    /// In the panel's named coordinate space (`NotchPanelSpace.name`).
    let frame: CGRect
}

enum NotchPanelSpace {
    static let name = "notchPanel"
}

private struct NotchSessionHoverKey: EnvironmentKey {
    static let defaultValue: @MainActor (SessionHover?) -> Void = { _ in }
}

extension EnvironmentValues {
    /// Called with the row under the pointer, or nil when it leaves. The
    /// time-left card is drawn by the panel, beside the usage card, not by the row.
    var notchSessionHover: @MainActor (SessionHover?) -> Void {
        get { self[NotchSessionHoverKey.self] }
        set { self[NotchSessionHoverKey.self] = newValue }
    }

    /// The live estimate for each working session, keyed by session id.
    var notchTurnETA: [String: TurnETATracker.Snapshot] {
        get { self[NotchTurnETAKey.self] }
        set { self[NotchTurnETAKey.self] = newValue }
    }
}
