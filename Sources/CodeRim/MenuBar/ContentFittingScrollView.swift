import SwiftUI

/// Short detail screens fit their contents. Long lists scroll within a cap;
/// neither the controls nor the window receive surplus flexible height.
struct ContentFittingScrollView<Content: View>: View {
    @Environment(\.usageDetailUsesWindowWidth) private var usesWindowWidth
    var maximumHeight: CGFloat = MenuPopoverMetrics.detailMaximumHeight
    @ViewBuilder var content: Content
    @State private var contentHeight: CGFloat?

    var body: some View {
        if usesWindowWidth {
            // Settings already owns the scrolling viewport. Avoid a second,
            // popover-sized scrollbar inside its expanding detail column.
            content
                .frame(maxWidth: .infinity, alignment: .topLeading)
                .fixedSize(horizontal: false, vertical: true)
        } else {
            compactViewport
        }
    }

    private var compactViewport: some View {
        ScrollView {
            content
                .frame(maxWidth: .infinity, alignment: .topLeading)
                .fixedSize(horizontal: false, vertical: true)
                .background {
                    GeometryReader { geometry in
                        Color.clear.preference(key: ContentHeightKey.self, value: geometry.size.height)
                    }
                }
        }
        .defaultScrollAnchor(.top)
        .frame(height: Self.viewportHeight(contentHeight: contentHeight, maximumHeight: maximumHeight))
        .onPreferenceChange(ContentHeightKey.self) { height in
            guard height.isFinite, height >= 0,
                  contentHeight.map({ abs($0 - height) > 0.5 }) ?? true else { return }
            contentHeight = height
        }
    }

    static func viewportHeight(contentHeight: CGFloat?, maximumHeight: CGFloat) -> CGFloat {
        min(maximumHeight, max(1, contentHeight ?? maximumHeight))
    }
}

private struct ContentHeightKey: PreferenceKey {
    static var defaultValue: CGFloat { 0 }

    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}
