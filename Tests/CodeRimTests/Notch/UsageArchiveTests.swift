import XCTest
@testable import CodeRim

final class NotchUsageArchiveTests: XCTestCase {
    private var suite = ""
    private var defaults: UserDefaults!

    override func setUpWithError() throws {
        suite = "notch-archive-\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
    }

    override func tearDownWithError() throws {
        defaults.removePersistentDomain(forName: suite)
    }

    private func reading(_ id: String, fetchedAt: Date) -> (snapshot: ProviderSnapshot, fetchedAt: Date) {
        let snapshot = ProviderSnapshot(
            id: id, displayName: id.capitalized, glyph: .third,
            fidelity: .official, status: .ok,
            windows: [LimitWindow(id: "w", label: "W", usedFraction: 0.4)],
            headlineID: "w"
        )
        return (snapshot, fetchedAt)
    }

    /// The regression this exists for: every provider fetch calls `save`, and a
    /// write to `UserDefaults.standard` posts `didChangeNotification` app-wide —
    /// which `AccountLimitStore` reacts to by re-publishing, which re-fetched
    /// the notch, which called `save` again. An unchanged `save` must not write.
    func testAnUnchangedSaveDoesNotTouchDefaults() {
        let archive = UsageArchive(defaults: defaults)
        let now = Date()
        archive.save(["codex": reading("codex", fetchedAt: now)])
        let first = defaults.data(forKey: "notchLastGoodReadings")
        XCTAssertNotNil(first)

        // Same readings, same timestamps → identical bytes → no write.
        archive.save(["codex": reading("codex", fetchedAt: now)])
        XCTAssertEqual(defaults.data(forKey: "notchLastGoodReadings"), first)
    }

    /// Dictionary iteration order is unstable; the byte compare must survive it.
    func testKeyOrderDoesNotDefeatTheDedup() {
        let archive = UsageArchive(defaults: defaults)
        let now = Date()
        archive.save(["codex": reading("codex", fetchedAt: now),
                      "claude": reading("claude", fetchedAt: now)])
        let first = defaults.data(forKey: "notchLastGoodReadings")

        archive.save(["claude": reading("claude", fetchedAt: now),
                      "codex": reading("codex", fetchedAt: now)])
        XCTAssertEqual(defaults.data(forKey: "notchLastGoodReadings"), first)
    }

    func testAChangedReadingIsWritten() {
        let archive = UsageArchive(defaults: defaults)
        archive.save(["codex": reading("codex", fetchedAt: Date())])
        let first = defaults.data(forKey: "notchLastGoodReadings")

        archive.save(["codex": reading("codex", fetchedAt: Date().addingTimeInterval(120)),
                      "claude": reading("claude", fetchedAt: Date())])
        XCTAssertNotEqual(defaults.data(forKey: "notchLastGoodReadings"), first)
        XCTAssertEqual(archive.load().count, 2)
    }
}
