import SwiftUI

// Ported from vinzdg/codenotch (MIT) — DesignSystem/Design.swift, Typography.swift,
// Palette.swift. The edge notch is a deliberate second visual system, kept apart
// from CodeRim's "quiet instrument" tokens: a pure-black pill sampled from
// Codenotch's design frames.

/// Every number in the notch UI is measured off Codenotch's Figma frame
/// (2000 x 2000 px), so the layout is *proportionally* exact rather than
/// eyeballed. One anchor picks the scale: the provider ring is 44pt across and
/// measures 117px in the frame. Change `scale` and the whole surface resizes
/// together, still in the design's proportions.
enum NotchDesign {
    /// Points per pixel of the design frame.
    static let scale: CGFloat = 44.0 / 117.0

    /// A distance measured in design-frame pixels, in points.
    static func px(_ pixels: CGFloat) -> CGFloat { pixels * scale }

    /// Cap-height fraction of an em for SF Pro.
    private static let capRatio: CGFloat = 0.714

    /// The point size whose capital letters are `pixels` tall in the frame.
    static func fontSize(capPixels pixels: CGFloat) -> CGFloat {
        px(pixels) / capRatio
    }
}

/// Sizes derived from cap heights measured in the design frame, so they track
/// `NotchDesign.scale` along with everything else.
enum NotchType {
    /// The percent under each provider ring. Cap height 27px.
    static let percent = Font.system(size: NotchDesign.fontSize(capPixels: 27), weight: .semibold)
    /// A card title. Cap height 26px.
    static let cardTitle = Font.system(size: NotchDesign.fontSize(capPixels: 26), weight: .semibold)
    /// Card body copy. Cap height 18px.
    static let cardBody = Font.system(size: NotchDesign.fontSize(capPixels: 18), weight: .regular)
}

/// Sampled from Codenotch's `docs/design/frame-124-hover-tooltip.png`.
enum NotchPalette {
    static let notch         = Color.black
    static let card          = Color.black
    static let ringTrack     = Color(notchHex: 0x303030)
    static let barTrack      = Color(notchHex: 0x2D2D2D)

    static let ample         = Color(notchHex: 0x00FF88)   // green
    static let watch         = Color(notchHex: 0xF2FF00)   // yellow
    static let critical      = Color(notchHex: 0xFF3F00)   // orange

    static let textPrimary   = Color.white
    static let textSecondary = Color(notchHex: 0x808080)
}

extension Color {
    init(notchHex hex: UInt32) {
        self.init(
            .sRGB,
            red:   Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue:  Double(hex & 0xFF) / 255,
            opacity: 1
        )
    }
}

// MARK: - Environment: accent colour + reduce transparency

/// The selected ring colour: used below the warning bands in Usage colours
/// mode, or at every usage level in Fixed colour mode.
enum NotchAccentChoice: String, CaseIterable, Codable, Sendable {
    case system, green, blue, purple, pink, orange

    var color: Color {
        switch self {
        case .system: return NotchPalette.ample
        case .green:  return NotchPalette.ample
        case .blue:   return Color(notchHex: 0x3B9CFF)
        case .purple: return Color(notchHex: 0x9B7DFF)
        case .pink:   return Color(notchHex: 0xFF6EC7)
        case .orange: return Color(notchHex: 0xFF9F3F)
        }
    }
}

private struct NotchAccentColorKey: EnvironmentKey {
    static let defaultValue: Color = NotchPalette.ample
}

private struct NotchReduceTransparencyKey: EnvironmentKey {
    static let defaultValue: Bool = false
}

extension EnvironmentValues {
    var notchAccentColor: Color {
        get { self[NotchAccentColorKey.self] }
        set { self[NotchAccentColorKey.self] = newValue }
    }

    /// True when macOS "Reduce Transparency" is on, or explicitly overridden.
    var notchReduceTransparency: Bool {
        get { self[NotchReduceTransparencyKey.self] || accessibilityReduceTransparency }
        set { self[NotchReduceTransparencyKey.self] = newValue }
    }
}
