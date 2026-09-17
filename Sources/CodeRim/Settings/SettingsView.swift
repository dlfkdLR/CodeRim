import SwiftUI

/// The settings window: a sidebar of subjects beside one scrolling pane.
/// Every detail must provide a scrollable viewport so its content's minimum
/// height cannot push the split view outside the window.
struct SettingsView: View {
    @StateObject private var navigation: SettingsNavigation

    init(navigation: SettingsNavigation = SettingsNavigation()) {
        _navigation = StateObject(wrappedValue: navigation)
    }

    var body: some View {
        NavigationSplitView(columnVisibility: $navigation.columnVisibility) {
            List(SettingsCategory.allCases, selection: Binding(
                get: { navigation.category },
                set: { if let category = $0 { navigation.select(.category(category)) } }
            )) { category in
                Label {
                    Text(category.title)
                } icon: {
                    SidebarIcon(systemName: category.systemImage, tint: category.chipTint)
                }
                .padding(.vertical, 3)
                .tag(category)
            }
            .listStyle(.sidebar)
            .navigationSplitViewColumnWidth(min: 200, ideal: 216, max: 260)
            .accessibilityLabel("Settings sections")
            .accessibilityHint("Choose a section to change its settings.")
        } detail: {
            pane
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                .clipped()
        }
        .navigationSplitViewStyle(.balanced)
        .frame(
            minWidth: 0, idealWidth: 960, maxWidth: .infinity,
            minHeight: 0, idealHeight: 640, maxHeight: .infinity
        )
    }

    @ViewBuilder
    private var pane: some View {
        switch navigation.category {
        case .usage: UsageSettingsView()
        case .providers: ProvidersSettingsView(detailSelection: $navigation.providerDetailID)
        case .notch: NotchSettingsView()
        case .general: GeneralSettingsView()
        case .advanced: AdvancedSettingsView()
        case .about: AboutSettingsView()
        case nil:
            ContentUnavailableView(
                "Choose a Section",
                systemImage: "sidebar.left",
                description: Text("Select a section from the sidebar to view its settings.")
            )
        }
    }
}
