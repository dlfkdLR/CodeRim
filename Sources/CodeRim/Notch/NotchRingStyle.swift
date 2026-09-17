import SwiftUI

/// Ring appearance is independent of the measured usage and alert thresholds.
enum NotchRingColorMode: String, CaseIterable, Codable, Sendable {
    case usage, fixed, gradient

    var title: String {
        switch self {
        case .usage: return "Usage colours"
        case .fixed: return "Fixed colour"
        case .gradient: return "Gradient"
        }
    }

    var explanation: String {
        switch self {
        case .usage:
            return "The ring uses your chosen colour below 50%, yellow from 50%, and orange from 70%."
        case .fixed:
            return "The ring keeps your chosen colour at every usage level. Limit alerts stay enabled according to your settings."
        case .gradient:
            return "The ring keeps the same gradient at every usage level. Limit alerts stay enabled according to your settings."
        }
    }
}

enum NotchRingGradient: String, CaseIterable, Codable, Sendable {
    case aurora, ocean, sunset, spectrum

    var title: String { rawValue.capitalized }

    /// Repeat the first stop so a full ring has no visible colour seam.
    var colors: [Color] {
        let hexes: [UInt32]
        switch self {
        case .aurora: hexes = [0x52E5C5, 0x61A8FF, 0xBA88FF, 0x52E5C5]
        case .ocean: hexes = [0x54D9FF, 0x4A8DFF, 0x777BFF, 0x54D9FF]
        case .sunset: hexes = [0xFFCA70, 0xFF8C69, 0xF879BD, 0xFFCA70]
        case .spectrum: hexes = [0xFF7C99, 0xFFD46B, 0x77E3A4, 0x67C8FF, 0xB39AFF, 0xFF7C99]
        }
        return hexes.map { Color(notchHex: $0) }
    }
}

struct NotchRingAppearance: Equatable, Sendable {
    var mode: NotchRingColorMode = .usage
    var gradient: NotchRingGradient = .aurora
    var animatesGradient: Bool = false

    static func stored(in defaults: UserDefaults = .standard) -> Self {
        Self(mode: NotchRingColorMode(rawValue: defaults.string(forKey: "notchRingColorMode") ?? "") ?? .usage,
             gradient: NotchRingGradient(rawValue: defaults.string(forKey: "notchRingGradient") ?? "") ?? .aurora,
             animatesGradient: defaults.bool(forKey: "notchAnimateGradient"))
    }

    func shouldAnimate(reduceMotion: Bool, isVisible: Bool) -> Bool {
        mode == .gradient && animatesGradient && !reduceMotion && isVisible
    }

    /// A clearly visible three-second colour cycle. All rings share a phase, including
    /// after the notch opens again; no repeating animation state to accumulate.
    static func gradientRotation(at date: Date) -> Angle {
        .degrees(date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 3) / 3 * 360)
    }

    func strokeStyle(band: UsageBand, accent: Color) -> AnyShapeStyle {
        switch mode {
        case .usage: return AnyShapeStyle(band.color(accent: accent))
        case .fixed: return AnyShapeStyle(accent)
        case .gradient:
            return AnyShapeStyle(AngularGradient(colors: gradient.colors, center: .center))
        }
    }
}

private struct NotchRingAppearanceKey: EnvironmentKey {
    static let defaultValue = NotchRingAppearance()
}

private struct NotchRingAnimationEnabledKey: EnvironmentKey {
    static let defaultValue = true
}

extension EnvironmentValues {
    /// The folded notch does not need continuously redrawn colours.
    var notchRingAnimationEnabled: Bool {
        get { self[NotchRingAnimationEnabledKey.self] }
        set { self[NotchRingAnimationEnabledKey.self] = newValue }
    }

    var notchRingAppearance: NotchRingAppearance {
        get { self[NotchRingAppearanceKey.self] }
        set { self[NotchRingAppearanceKey.self] = newValue }
    }
}

/// Uses the actual ring renderer, including its geometry and colour policy.
struct NotchRingAppearancePreview: View {
    let appearance: NotchRingAppearance
    let accent: NotchAccentChoice

    var body: some View {
        HStack(spacing: 24) {
            ForEach([25, 60, 90], id: \.self) { percent in
                VStack(spacing: 6) {
                    ProviderRing(usedFraction: Double(percent) / 100, glyph: .openai)
                    Text("\(percent)%")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.white)
                }
            }
        }
        .padding(12)
        .background(.black, in: RoundedRectangle(cornerRadius: 8))
        .environment(\.notchAccentColor, accent.color)
        .environment(\.notchRingAppearance, appearance)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Ring preview at 25, 60, and 90 percent used")
        .accessibilityValue(appearance.mode.title)
    }
}
