import AppKit
import Foundation
import XCTest
@testable import CodeRim

final class FullAuditBoundaryTests: XCTestCase {
    func testLimitIntegerBoundariesDoNotTrapOrTruncate() throws {
        let parser = AccountLimitsResponseParser()
        for (number, expected) in [
            ("9223372036854775807", Int.max as Int?),
            ("9223372036854775808", nil),
            ("1e100", nil),
            ("2.5", nil),
            ("2.0", 2),
            ("true", nil)
        ] {
            let json = "{\"id\":2,\"result\":{\"rateLimitResetCredits\":{\"availableCount\":\(number)}}}"
            let snapshot = try parser.parse(Data(json.utf8))
            XCTAssertEqual(snapshot.resetCredits?.availableCount, expected, number)
        }
    }

    func testCopilotRejectsUnrepresentableCountsAndNonNumericQuotas() {
        for quota in [#"{"remaining":1e30}"#, #"{"used":1e30}"#, #"{"remaining":true}"#,
                      #"{"entitlement":1e-300,"used":1e300}"#] {
            let json = "{\"quota_snapshots\":{\"chat\":\(quota)}}"
            XCTAssertThrowsError(try GitHubCopilotUsage.windows(from: Data(json.utf8)), quota)
        }
    }

    func testPercentFormattingRejectsInvalidAndUnrepresentableFractions() {
        for fraction in [Double.nan, .infinity, -.infinity, -1, 1e300, Double(Int.max)] {
            XCTAssertEqual(Percent.text(for: fraction), "—")
            XCTAssertEqual(Percent.halves(for: fraction).used, "—")
            XCTAssertEqual(Percent.halves(for: fraction).left, "—")
        }
        XCTAssertEqual(Percent.text(for: 1.04), "104")
        XCTAssertEqual(Percent.halves(for: 1.04).left, "0")
    }

    func testExtremeDatesDoNotTrapFormatters() {
        let now = Date(timeIntervalSince1970: 0)
        for interval in [1e308, -1e308, Double.infinity, -Double.infinity] {
            let date = Date(timeIntervalSince1970: interval)
            XCTAssertEqual(ResetCopy.text(for: date, now: now), "Reset time unavailable")
            XCTAssertEqual(ResetCopy.text(for: date, now: now, format: .remaining), "Reset time unavailable")
        }
        for interval in [-1e308, -Double.infinity] {
            let date = Date(timeIntervalSince1970: interval)
            XCTAssertEqual(ElapsedCopy.text(since: date, now: now), "unknown")
            XCTAssertEqual(LimitFreshness.text(fetchedAt: date, now: now), "Update time unavailable")
        }
    }

    func testFractionalRPCResponseIDCannotStandInForRequestedID() {
        let json = #"{"id":2.5,"result":{}}"#
        XCTAssertThrowsError(try AccountLimitsResponseParser().parse(Data(json.utf8)))
    }

    func testGrokIssuerMustEndAtTheIssuerClientBoundary() {
        for key in ["https://auth.x.ai.example.invalid::cli", "https://auth.x.ai@evil.invalid::cli",
                    "https://auth.x.ai/path::cli", "https://auth.x.ai?next=evil::cli"] {
            XCTAssertFalse(GrokCredentials.isTrusted(key: key, entry: ["key": "synthetic"]), key)
        }
        XCTAssertTrue(GrokCredentials.isTrusted(key: "https://auth.x.ai", entry: [:]))
        XCTAssertTrue(GrokCredentials.isTrusted(key: "https://auth.x.ai::cli", entry: [:]))
        XCTAssertTrue(GrokCredentials.isTrusted(key: "legacy", entry: ["oidc_issuer": "https://auth.x.ai"]))
    }
}

@MainActor
final class FullAuditRefreshRaceTests: XCTestCase {
    func testOldAccountResponseCannotRemoveTheNewAccountPlaceholder() async throws {
        let suite = "dev.coderim.audit-race.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let provider = SuspendedProvider()
        let store = NotchUsageStore(providers: [provider], archive: UsageArchive(defaults: defaults))
        let task = Task { await store.refresh() }
        for _ in 0..<100 where provider.resume == nil { await Task.yield() }
        XCTAssertNotNil(provider.resume)
        store.invalidateAccount(providerID: "codex")
        provider.resume?.resume()
        await task.value
        XCTAssertEqual(store.snapshots.map(\.id), ["codex"])
        XCTAssertTrue(store.snapshots.first?.windows.isEmpty == true)
        XCTAssertNil(store.lastUpdatedAt(providerID: "codex"))
    }

    private final class SuspendedProvider: NotchProvider {
        let id = "codex"
        let displayName = "Codex"
        var glyph: ProviderGlyph { .openai }
        var resume: CheckedContinuation<Void, Never>?
        func fetchSnapshot() async throws -> ProviderSnapshot {
            await withCheckedContinuation { resume = $0 }
            return ProviderSnapshot(id: id, displayName: displayName, glyph: glyph, fidelity: .official,
                                    status: .ok, windows: [LimitWindow(id: "old", label: "Old account", usedFraction: 0.7)])
        }
        func account() -> ProviderAccount? { nil }
        var signInRoute: SignInRoute { .guidance("") }
        func signOut() async {}
        func presentSignIn() {}
        func forgetCachedCredential() {}
    }
}


@MainActor
final class FullAuditEdgeTransitionTests: XCTestCase {
    func testReturningToCurrentEdgeCancelsAnInFlightMove() throws {
        _ = NSApplication.shared
        var completions: [@MainActor () -> Void] = []
        let controller = NotchWindowController(animateEdge: { _, finish in completions.append(finish) },
                                               reduceMotion: { false })
        controller.show()
        defer { controller.stop() }
        XCTAssertNotNil(controller.panelContentViewForTesting)
        let original = controller.model.edge
        controller.apply(edge: .top)
        controller.apply(edge: original)
        XCTAssertEqual(completions.count, 2)
        for finish in completions.reversed() { finish() }
        XCTAssertEqual(controller.model.edge, original)
        XCTAssertEqual(controller.panelAlphaForTesting, 1)
    }

    func testVisibilityChangesDuringAndAfterEdgeCompletionWin() async throws {
        _ = NSApplication.shared
        for initialOpen in [false, true] {
            for setting in [NotchVisibility.alwaysShow, .onHover, .hidden] {
                for afterCompletion in [false, true] {
                    var finish: (@MainActor () -> Void)?
                    let controller = NotchWindowController(animateEdge: { _, callback in finish = callback },
                                                           reduceMotion: { false })
                    controller.show()
                    controller.model.isExpanded = initialOpen
                    controller.apply(edge: .top)
                    if !afterCompletion { controller.apply(setting) }
                    finish?()
                    if afterCompletion { controller.apply(setting) }
                    try await Task.sleep(for: .milliseconds(100))
                    XCTAssertEqual(controller.model.edge, .top)
                    XCTAssertEqual(controller.model.isExpanded, setting == .alwaysShow,
                                   "\(initialOpen), \(setting), after completion: \(afterCompletion)")
                    controller.stop()
                }
            }
        }
    }

    func testMissingAnimationCompletionStillCommitsTheRequestedEdgeOnce() async throws {
        _ = NSApplication.shared
        var finish: (@MainActor () -> Void)?
        let controller = NotchWindowController(animateEdge: { _, callback in finish = callback },
                                               reduceMotion: { false })
        controller.show()
        defer { controller.stop() }
        controller.model.isExpanded = true
        controller.apply(edge: .top)
        try await Task.sleep(for: .milliseconds(600))
        XCTAssertEqual(controller.model.edge, .top)
        XCTAssertTrue(controller.model.isExpanded)
        XCTAssertEqual(controller.panelAlphaForTesting, 1)
        finish?()
        XCTAssertTrue(controller.model.isExpanded, "A late completion folded the already settled notch")
    }

    func testReducedMotionMovesImmediatelyWithoutFadingOrCollapsing() throws {
        _ = NSApplication.shared
        let controller = NotchWindowController(animateEdge: { _, _ in XCTFail("Reduced motion started an animation") },
                                               reduceMotion: { true })
        controller.show()
        defer { controller.stop() }
        controller.model.isExpanded = true
        controller.apply(edge: .bottom)
        XCTAssertEqual(controller.model.edge, .bottom)
        XCTAssertTrue(controller.model.isExpanded)
        XCTAssertEqual(controller.panelAlphaForTesting, 1)
    }
}
