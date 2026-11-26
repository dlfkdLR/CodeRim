import SwiftUI
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting

private final class SettingsSessionHandle {
    let raw = cr_settings_navigation_create()!
    deinit { cr_settings_navigation_free(raw) }
}
#endif

/// Selection lives with the window controller so requests made before SwiftUI
/// subscribes or while the window is closed are retained.
@MainActor
final class SettingsNavigation: ObservableObject {
    struct Selection {
        let category: SettingsCategory?
        let providerDetailID: String?
    }

    @Published private(set) var selection = Selection(category: .usage, providerDetailID: nil)
    var category: SettingsCategory? { selection.category }
    var providerDetailID: String? { selection.providerDetailID }
    @Published var columnVisibility: NavigationSplitViewVisibility = .all

    enum WindowAction { case create, reattach, raise }

    #if CODERIM_RUST_ACCOUNTING
    private let session = SettingsSessionHandle()
    // Matches the stable C category identifiers. Swift owns the localized presentation.
    private static let categories: [SettingsCategory] = [.general, .usage, .providers, .notch, .advanced, .about]
    #endif

    func select(_ pane: SettingsPane) {
        switch pane {
        case .category(let category): update(category: category, provider: nil)
        case .provider(let provider): update(category: .providers, provider: provider.rawValue)
        case .notchProvider(let id): update(category: .providers, provider: id)
        }
    }

    func setProviderDetail(_ id: String?) {
        update(category: nil, provider: id)
    }

    private func update(category: SettingsCategory?, provider: String?) {
        #if CODERIM_RUST_ACCOUNTING
        let action = category == nil ? CR_SETTINGS_SET_DETAIL
            : provider == nil ? CR_SETTINGS_SELECT_CATEGORY : CR_SETTINGS_SELECT_PROVIDER
        let code = category.flatMap { Self.categories.firstIndex(of: $0) } ?? 0
        var text = provider ?? ""
        let selection = text.withUTF8 {
            cr_settings_navigation_select(session.raw, UInt32(action), UInt32(code), $0.baseAddress,
                                          $0.count, provider != nil)
        }
        guard selection.category_changed || selection.provider_changed else { return }
        // Publish a complete route. Observers never see a new detail under the old section.
        let detail: String?
        if selection.provider_changed {
            let bytes = UnsafeBufferPointer(start: selection.provider, count: selection.length)
            detail = selection.has_provider ? String(decoding: bytes, as: UTF8.self) : nil
        } else {
            detail = providerDetailID
        }
        self.selection = Selection(category: Self.categories[Int(selection.category)], providerDetailID: detail)
        #else
        selection = Selection(category: category ?? self.category, providerDetailID: provider)
        #endif
    }

    func prepareWindow(exists: Bool, hasContent: Bool) -> WindowAction {
        if columnVisibility != .all { columnVisibility = .all }
        #if CODERIM_RUST_ACCOUNTING
        switch cr_settings_navigation_present(session.raw, hasContent) {
        case UInt32(CR_SETTINGS_CREATE_WINDOW): return .create
        case UInt32(CR_SETTINGS_REATTACH_CONTENT): return .reattach
        default: return .raise
        }
        #else
        if !exists { return .create }
        return hasContent ? .raise : .reattach
        #endif
    }

    func closedWindow() {
        #if CODERIM_RUST_ACCOUNTING
        cr_settings_navigation_close(session.raw)
        #endif
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
