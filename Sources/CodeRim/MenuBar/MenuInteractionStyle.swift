import SwiftUI

/// A quiet pointer/keyboard affordance that never changes the row's size.
struct MenuInteractionStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        MenuInteractionBody(configuration: configuration)
    }
}

private struct MenuInteractionBody: View {
    let configuration: ButtonStyleConfiguration
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.isFocused) private var isFocused
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.colorSchemeContrast) private var contrast
    @State private var isHovered = false

    private var fillOpacity: Double {
        guard isEnabled else { return 0 }
        if configuration.isPressed { return contrast == .increased ? 0.18 : 0.1 }
        if isHovered { return contrast == .increased ? 0.12 : 0.055 }
        return 0
    }

    var body: some View {
        configuration.label
            .background(Color.primary.opacity(fillOpacity), in: RoundedRectangle(cornerRadius: 8))
            .overlay {
                RoundedRectangle(cornerRadius: 8)
                    .strokeBorder(isFocused && isEnabled ? Color.accentColor : .clear, lineWidth: 2)
                    .allowsHitTesting(false)
            }
            .opacity(isEnabled ? 1 : 0.45)
            .onHover { isHovered = $0 }
            .animation(reduceMotion ? nil : .easeOut(duration: 0.12), value: isHovered)
    }
}
