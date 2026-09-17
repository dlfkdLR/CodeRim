import SwiftUI

private struct UsageDetailUsesWindowWidthKey: EnvironmentKey {
    static let defaultValue = false
}

extension EnvironmentValues {
    var usageDetailUsesWindowWidth: Bool {
        get { self[UsageDetailUsesWindowWidthKey.self] }
        set { self[UsageDetailUsesWindowWidthKey.self] = newValue }
    }
}

/// Analytics retains its compact presentation in the menu, and expands with
/// the available column when opened from Settings.
struct UsageDetailWidth: ViewModifier {
    @Environment(\.usageDetailUsesWindowWidth) private var usesWindowWidth

    func body(content: Content) -> some View {
        content
            .frame(width: usesWindowWidth ? nil : MenuPopoverMetrics.width, alignment: .topLeading)
            .frame(maxWidth: usesWindowWidth ? .infinity : nil, alignment: .topLeading)
    }
}
