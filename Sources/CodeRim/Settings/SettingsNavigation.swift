import SwiftUI

/// Selection lives with the window controller so requests made before SwiftUI
/// subscribes or while the window is closed are retained.
@MainActor
final class SettingsNavigation: ObservableObject {
    @Published var category: SettingsCategory? = .usage
    @Published var providerDetailID: String?
    @Published var columnVisibility: NavigationSplitViewVisibility = .all

    func select(_ pane: SettingsPane) {
        switch pane {
        case .category(let category):
            providerDetailID = nil
            self.category = category
        case .provider(let provider):
            providerDetailID = provider.rawValue
            category = .providers
        case .notchProvider(let id):
            providerDetailID = id
            category = .providers
        }
    }
}

/// Shared settings that are not tied to one service. Per-provider settings
/// (account, limits, local data, analytics options) live in `SettingsPane.provider`.
enum SettingsCategory: String, CaseIterable, Identifiable, Hashable {
    case general
    case usage
    case providers
    case notch
    case advanced
    case about

    var id: Self { self }

    var title: String {
        switch self {
        case .general: "General"
        case .usage: "Usage"
        case .providers: "Providers"
        case .notch: "Notch"
        case .advanced: "Diagnostics"
        case .about: "Information"
        }
    }

    var systemImage: String {
        switch self {
        case .general: "gearshape"
        case .usage: "chart.bar.xaxis"
        case .providers: "person.crop.circle.fill"
        case .notch: "inset.filled.topthird.rectangle"
        case .advanced: "slider.horizontal.3"
        case .about: "info.circle"
        }
    }

    var chipTint: Color {
        switch self {
        case .general: .gray
        case .usage: .blue
        case .providers: .indigo
        case .notch: .teal
        case .advanced: .pink
        case .about: .gray
        }
    }
}

/// One selectable entry in the settings sidebar: a shared category, one of the
/// two fully-metered providers (Codex, Claude — local token accounting), or one
/// of the notch's borrowed-credential providers (limits only).
enum SettingsPane: Hashable, Identifiable {
    case category(SettingsCategory)
    case provider(UsageProvider)
    case notchProvider(id: String)

    var id: String {
        switch self {
        case .category(let category): "category.\(category.rawValue)"
        case .provider(let provider): "provider.\(provider.rawValue)"
        case .notchProvider(let id): "notchProvider.\(id)"
        }
    }

    var title: String {
        switch self {
        case .category(let category): category.title
        case .provider(let provider): provider.title
        case .notchProvider(let id):
            NotchProviderCatalog.all.first { $0.id == id }?.name ?? id.capitalized
        }
    }

    var systemImage: String {
        switch self {
        case .category(let category): category.systemImage
        case .provider(let provider): provider.symbol
        case .notchProvider: "circle.dotted"
        }
    }

    var chipTint: Color {
        switch self {
        case .category(let category): category.chipTint
        case .provider(let provider): provider == .codex ? .green : .orange
        case .notchProvider: .secondary
        }
    }

    /// The notch providers that are not already covered by a `.provider` pane.
    static var notchOnly: [SettingsPane] {
        NotchProviderCatalog.all
            .filter { $0.id != "codex" && $0.id != "claude" }
            .map { SettingsPane.notchProvider(id: $0.id) }
    }

    static var allCases: [SettingsPane] {
        SettingsCategory.allCases.map(SettingsPane.category)
            + UsageProvider.allCases.map(SettingsPane.provider)
            + notchOnly
    }

    func matches(_ query: String) -> Bool {
        let trimmed = query.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return true }
        if title.localizedCaseInsensitiveContains(trimmed) { return true }
        if case .provider(let provider) = self {
            return provider.tabTitle.localizedCaseInsensitiveContains(trimmed)
        }
        return false
    }
}
