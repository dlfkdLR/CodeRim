import Foundation

// Ported from vinzdg/codenotch (MIT) — Model/UsageArchive.swift. Trimmed:
// Codex token activity is dropped for now. The per-provider request-backoff
// came back with the OpenCode provider (Phase 5), which meters a rate-limited
// endpoint.

/// The last good reading for each provider, remembered across launches.
///
/// Without this, a cold start that cannot read a provider shows nothing at all,
/// which is the least useful thing the notch could do. A remembered reading is
/// dimmed and dated, but a dated number you can see beats a blank ring.
struct UsageArchive {
    private struct Entry: Codable {
        let id: String
        let displayName: String
        let glyph: ProviderGlyph
        let fidelity: Fidelity
        let windows: [LimitWindow]
        let fetchedAt: Date
        /// Optional so archives written before this field still decode.
        let headlineID: String?
        /// Whose reading this is (`NotchProvider.accountIdentity()`); nil for providers that cannot
        /// tell accounts apart and for archives written before the field existed.
        let accountIdentity: String?
    }

    private let defaults: UserDefaults
    private let key = "notchLastGoodReadings"
    private let backoffKey = "notchBackoffUntil"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    /// When a rate-limited account may be tried again. Kept per provider *and* account — the limit
    /// is per account, and a switch to another account must not inherit the wait — and on disk, so a
    /// penalty in progress survives a relaunch rather than being spent hammering the endpoint.
    func loadBackoffUntil(providerID: String, account: String? = nil) -> Date? {
        guard let date = defaults.object(forKey: backoffStorageKey(providerID, account)) as? Date,
              date > Date()
        else { return nil }
        return date
    }

    func saveBackoffUntil(_ date: Date?, providerID: String, account: String? = nil) {
        let key = backoffStorageKey(providerID, account)
        if let date {
            defaults.set(date, forKey: key)
        } else {
            defaults.removeObject(forKey: key)
        }
        // Waits written before they were per account could belong to anyone; drop them.
        if account != nil { defaults.removeObject(forKey: backoffStorageKey(providerID, nil)) }
    }

    private func backoffStorageKey(_ providerID: String, _ account: String?) -> String {
        account.map { "\(backoffKey).\(providerID).\($0)" } ?? "\(backoffKey).\(providerID)"
    }

    /// Readings with the account each was taken for, so a restore can drop one that belongs to an
    /// account signed out while CodeRim was not running.
    func loadOwned() -> [String: (snapshot: ProviderSnapshot, fetchedAt: Date, accountIdentity: String?)] {
        guard let data = defaults.data(forKey: key),
              let entries = try? JSONDecoder().decode([Entry].self, from: data)
        else { return [:] }
        var owners: [String: String?] = [:]
        for entry in entries { owners[entry.id] = entry.accountIdentity }
        return load().reduce(into: [:]) { result, item in
            result[item.key] = (item.value.snapshot, item.value.fetchedAt, owners[item.key] ?? nil)
        }
    }

    func load() -> [String: (snapshot: ProviderSnapshot, fetchedAt: Date)] {
        guard let data = defaults.data(forKey: key),
              let entries = try? JSONDecoder().decode([Entry].self, from: data)
        else { return [:] }

        var result: [String: (snapshot: ProviderSnapshot, fetchedAt: Date)] = [:]
        for entry in entries {
            var snapshot = ProviderSnapshot(
                id: entry.id,
                displayName: entry.displayName,
                // Early extended-provider archives used the shared placeholder.
                glyph: entry.glyph == .third ? NotchProviderCatalog.glyph(for: entry.id) : entry.glyph,
                fidelity: entry.fidelity,
                status: .stale(since: entry.fetchedAt),
                windows: entry.windows,
                headlineID: entry.headlineID
            )
            snapshot.accountIdentity = entry.accountIdentity
            result[entry.id] = (snapshot, entry.fetchedAt)
        }
        return result
    }

    func save(_ readings: [String: (snapshot: ProviderSnapshot, fetchedAt: Date)], owners: [String: String] = [:]) {
        // Sorted so an unchanged set of readings encodes to identical bytes —
        // `Dictionary.values` has no stable order, and the byte compare below
        // relies on it.
        let entries = readings
            .sorted { $0.key < $1.key }
            .map { _, reading in
                Entry(
                    id: reading.snapshot.id,
                    displayName: reading.snapshot.displayName,
                    glyph: reading.snapshot.glyph,
                    fidelity: reading.snapshot.fidelity,
                    windows: reading.snapshot.windows,
                    fetchedAt: reading.fetchedAt,
                    headlineID: reading.snapshot.headlineID,
                    accountIdentity: owners[reading.snapshot.id]
                )
            }
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys   // byte-stable, for the compare below
        guard let data = try? encoder.encode(entries) else { return }
        // Skip an identical write. Every provider fetch calls this, and writing
        // to `UserDefaults.standard` posts `didChangeNotification` app-wide —
        // which `AccountLimitStore` reacts to. An unconditional write here is
        // one side of a feedback loop.
        if defaults.data(forKey: key) == data { return }
        defaults.set(data, forKey: key)
    }

    /// Drop what we remember about one provider.
    func forget(_ providerID: String) {
        var readings = loadOwned()
        readings.removeValue(forKey: providerID)
        save(readings.mapValues { ($0.snapshot, $0.fetchedAt) },
             owners: readings.compactMapValues { $0.accountIdentity })
    }
}
