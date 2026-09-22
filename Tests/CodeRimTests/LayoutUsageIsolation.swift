import Foundation
import XCTest
@testable import CodeRim

/// UI tasks can request analytics even when polling is off. Always inject an
/// empty source set and a private database; never fall back to the user's data.
@MainActor
extension XCTestCase {
    func isolatedLayoutUsageStore(provider: UsageProvider = .codex,
                                  analyticsSnapshots: [AnalyticsRange: AnalyticsSnapshot] = [:],
                                  initialSnapshot: UsageSnapshot? = nil,
                                  automaticallyRefresh: Bool = false,
                                  collector: CodexUsageCollector? = nil,
                                  defaults: UserDefaults? = nil) -> UsageStore {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("layout-usage-\(UUID())")
        let suite = "dev.coderim.layout.\(UUID())"
        do {
            let localDefaults = defaults ?? UserDefaults(suiteName: suite)!
            let database = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
            let localCollector = collector ?? CodexUsageCollector(database: database, roots: [], provider: provider)
            let store = UsageStore(provider: provider, analyticsSnapshots: analyticsSnapshots,
                                   initialSnapshot: initialSnapshot, automaticallyRefresh: false,
                                   collector: localCollector, defaults: localDefaults)
            addTeardownBlock {
                UserDefaults().removePersistentDomain(forName: suite)
                try? FileManager.default.removeItem(at: root)
            }
            return store
        } catch {
            fatalError("Could not create an isolated layout fixture: \(error)")
        }
    }
}
