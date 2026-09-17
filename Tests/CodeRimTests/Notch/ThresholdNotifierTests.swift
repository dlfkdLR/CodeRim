import XCTest
@testable import CodeRim

/// Guards the crossing rule: 80% and 100% are announced once as they are
/// crossed, never twice while they stay crossed, and again only after the
/// window has genuinely rolled over.
@MainActor
final class NotchThresholdNotifierTests: XCTestCase {
    private var alerts: [ThresholdAlert] = []
    private var muted: Set<String> = []

    // A fresh instance per test method, so `lazy` gives each test its own
    // notifier without a `setUp()` — which XCTest leaves nonisolated.
    private lazy var notifier = ThresholdNotifier(
        isMuted: { [weak self] in self?.muted.contains($0) ?? false },
        deliver: { [weak self] in self?.alerts.append($0) }
    )

    private func snapshot(_ id: String, _ name: String, _ fraction: Double,
                          label: String = "Current session") -> ProviderSnapshot {
        ProviderSnapshot(
            id: id, displayName: name, glyph: .claude, fidelity: .official, status: .ok,
            windows: [LimitWindow(id: "session", label: label, usedFraction: fraction)],
            headlineID: "session"
        )
    }

    func testCrossingEightyAlertsOnce() {
        notifier.observe([snapshot("claude", "Claude", 0.5)])
        XCTAssertNil(alerts.first, "parked under the threshold is nobody's business")

        notifier.observe([snapshot("claude", "Claude", 0.82)])
        notifier.observe([snapshot("claude", "Claude", 0.91)])
        XCTAssertEqual(alerts.count, 1, "still crossing, not crossing again")
        XCTAssertEqual(alerts[0].threshold, 80)
        XCTAssertEqual(alerts[0].usedPercent, 82)
        XCTAssertEqual(alerts[0].windowLabel, "Current session")
    }

    func testCrossingHundredAfterEightyAlertsAgain() {
        notifier.observe([snapshot("claude", "Claude", 0.85)])
        notifier.observe([snapshot("claude", "Claude", 1.02)])
        XCTAssertEqual(alerts.map(\.threshold), [80, 100])
    }

    /// A spent window that comes back is a new fact, not a re-announcement of
    /// the old one — so the memory clears and the next climb alerts again.
    func testARolledOverWindowAlertsAgain() {
        notifier.observe([snapshot("claude", "Claude", 0.9)])
        notifier.observe([snapshot("claude", "Claude", 0.1)])
        XCTAssertEqual(alerts.count, 1)
        notifier.observe([snapshot("claude", "Claude", 0.84)])
        XCTAssertEqual(alerts.count, 2)
        XCTAssertEqual(alerts[1].threshold, 80)
    }

    func testMutedProvidersAreSilentButRemembered() {
        muted = ["claude"]
        notifier.observe([snapshot("claude", "Claude", 0.85)])
        XCTAssertTrue(alerts.isEmpty)

        // Unmuting must not replay the crossing that happened in the silence.
        muted = []
        notifier.observe([snapshot("claude", "Claude", 0.86)])
        XCTAssertTrue(alerts.isEmpty, "an old crossing replayed is noise, not news")

        // But the next real crossing still reaches the user.
        notifier.observe([snapshot("claude", "Claude", 1.0)])
        XCTAssertEqual(alerts.map(\.threshold), [100])
    }

    func testProvidersWithoutARingAreIgnored() {
        // A remaining-count window has no fraction, so there is nothing to
        // compare against a threshold.
        let window = LimitWindow(id: "q", label: "Free queries", remaining: 12)
        let snapshot = ProviderSnapshot(id: "p", displayName: "P", glyph: .third,
                                        fidelity: .official, status: .ok, windows: [window])
        notifier.observe([snapshot, snapshot])
        XCTAssertTrue(alerts.isEmpty)
    }

    func testSeveralProvidersAlertIndependently() {
        notifier.observe([
            snapshot("claude", "Claude", 0.3),
            snapshot("cursor", "Cursor", 0.95)
        ])
        XCTAssertEqual(alerts.map(\.providerID), ["cursor"])
        notifier.observe([
            snapshot("claude", "Claude", 0.99),
            snapshot("cursor", "Cursor", 0.96)
        ])
        XCTAssertEqual(alerts.map(\.providerID), ["cursor", "claude"])
        XCTAssertEqual(alerts.last?.threshold, 80)
    }
}

/// The muted-set preference is stored as a joined id list; a provider added
/// later must not inherit somebody else's mute.
final class NotchAlertMuteStoreTests: XCTestCase {
    private var suiteName = ""
    private var defaults: UserDefaults!

    override func setUpWithError() throws {
        suiteName = "threshold-mute-\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
    }

    override func tearDownWithError() throws {
        defaults.removePersistentDomain(forName: suiteName)
    }

    func testMutingIsPerProviderAndRoundTrips() {
        XCTAssertFalse(AppPreferences.isAlertMuted("codex", in: defaults))

        AppPreferences.setAlertMuted(true, for: "codex", in: defaults)
        XCTAssertTrue(AppPreferences.isAlertMuted("codex", in: defaults))
        XCTAssertFalse(AppPreferences.isAlertMuted("claude", in: defaults))

        AppPreferences.setAlertMuted(false, for: "codex", in: defaults)
        XCTAssertTrue(AppPreferences.mutedAlertProviders(in: defaults).isEmpty)
    }
}
