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
    }

    private let defaults: UserDefaults
    private let key = "notchLastGoodReadings"
    private let backoffKey = "notchBackoffUntil"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    /// When a rate-limited provider may be tried again. Kept per provider —
    /// the limit is per account — and on disk, so a penalty in progress
    /// survives a relaunch rather than being spent hammering the endpoint.
    func loadBackoffUntil(providerID: String) -> Date? {
        guard let date = defaults.object(forKey: "\(backoffKey).\(providerID)") as? Date,
              date > Date()
        else { return nil }
        return date
    }

    func saveBackoffUntil(_ date: Date?, providerID: String) {
        let key = "\(backoffKey).\(providerID)"
        if let date {
            defaults.set(date, forKey: key)
        } else {
            defaults.removeObject(forKey: key)
        }
    }

    func load() -> [String: (snapshot: ProviderSnapshot, fetchedAt: Date)] {
        guard let data = defaults.data(forKey: key),
              let entries = try? JSONDecoder().decode([Entry].self, from: data)
        else { return [:] }

        var result: [String: (snapshot: ProviderSnapshot, fetchedAt: Date)] = [:]
        for entry in entries {
            let snapshot = ProviderSnapshot(
                id: entry.id,
                displayName: entry.displayName,
                glyph: entry.glyph,
                fidelity: entry.fidelity,
                status: .stale(since: entry.fetchedAt),
                windows: entry.windows,
                headlineID: entry.headlineID
            )
            result[entry.id] = (snapshot, entry.fetchedAt)
        }
        return result
    }

    func save(_ readings: [String: (snapshot: ProviderSnapshot, fetchedAt: Date)]) {
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
                    headlineID: reading.snapshot.headlineID
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
        var readings = load()
        readings.removeValue(forKey: providerID)
        save(readings)
    }
}
