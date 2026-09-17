import Foundation

/// The settings and account controls on a left or right screen edge.
enum NotchControlsPosition: String, CaseIterable, Identifiable {
    case automatic
    case above
    case below

    static let preferenceKey = "notchControlsPosition"
    var id: String { rawValue }

    var title: String {
        switch self {
        case .automatic: "Auto"
        case .above: "Above"
        case .below: "Below"
        }
    }

    static func stored(in defaults: UserDefaults = .standard) -> Self {
        Self(rawValue: defaults.string(forKey: preferenceKey) ?? "") ?? .automatic
    }
}
