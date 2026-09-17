import Foundation

/// The monitoring list is independent of each tool's login and of ring order.
enum NotchProviderSelection {
    static let key = "notchSelectedProviders"

    static func load(defaults: UserDefaults = .standard, existing: Set<String>) -> Set<String> {
        let supported = Set(NotchProviderCatalog.all.map(\.id))
        if let saved = defaults.string(forKey: key) {
            return Set(saved.split(separator: ",").map(String.init)).intersection(supported)
        }
        // Adopt existing readings on the first launch after upgrading. An
        // explicitly empty saved list stays empty on every subsequent launch.
        let initial = existing.intersection(supported)
        save(initial, defaults: defaults)
        return initial
    }

    static func save(_ ids: Set<String>, defaults: UserDefaults = .standard) {
        defaults.set(ids.sorted().joined(separator: ","), forKey: key)
    }
}
