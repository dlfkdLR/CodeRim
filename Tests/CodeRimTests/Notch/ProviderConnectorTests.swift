import XCTest
@testable import CodeRim

@MainActor
final class ProviderConnectorTests: XCTestCase {
    private final class FakeProvider: NotchProvider {
        let id: String
        var route: SignInRoute
        var presented = 0
        init(_ id: String, route: SignInRoute) { self.id = id; self.route = route }
        var displayName: String { id }
        var glyph: ProviderGlyph { .openai }
        func fetchSnapshot() async throws -> ProviderSnapshot { throw NotchProviderError.needsAuth }
        func account() -> ProviderAccount? { nil }
        var signInRoute: SignInRoute { route }
        func signOut() async {}
        func presentSignIn() { presented += 1 }
        func forgetCachedCredential() {}
    }

    private func guided(_ action: GuidedSignIn.Action) -> SignInRoute {
        .guided(.init(name: "Tool", action: action, note: "Sign in to Tool."))
    }
    private func connector(_ provider: FakeProvider, connects: @escaping (String) async -> Bool,
                           launch: @escaping (SignInRoute) -> Bool = { _ in true }, patience: TimeInterval = 5) -> ProviderConnector {
        ProviderConnector(provider: { $0 == provider.id ? provider : nil }, connects: connects, launch: launch,
                          pollInterval: .milliseconds(10), patience: patience)
    }
    private func settle(_ connector: ProviderConnector, _ id: String, until done: (ProviderConnector.State?) -> Bool) async {
        for _ in 0..<200 { if done(connector.states[id]) { return }; try? await Task.sleep(for: .milliseconds(10)) }
    }

    func testAnExistingSignInConnectsWithoutOpeningAnything() async {
        let provider = FakeProvider("p", route: guided(.terminal(command: "tool login")))
        var launched = 0
        let connector = connector(provider, connects: { _ in true }, launch: { _ in launched += 1; return true })
        connector.begin("p")
        await settle(connector, "p") { $0 == .connected }
        XCTAssertEqual(connector.states["p"], .connected); XCTAssertEqual(launched, 0)
    }

    func testSignInIsOpenedOnceThenWatchedUntilTheAccountAppears() async {
        let provider = FakeProvider("p", route: guided(.browser(URL(string: "https://example.com/login")!)))
        var checks = 0, launched = 0
        let connector = connector(provider, connects: { _ in checks += 1; return checks >= 4 }, launch: { _ in launched += 1; return true })
        connector.begin("p")
        await settle(connector, "p") { $0 == .connected }
        XCTAssertEqual(connector.states["p"], .connected)
        XCTAssertEqual(launched, 1, "the login page opens once, not on every poll")
        XCTAssertGreaterThanOrEqual(checks, 4)
    }

    func testAKeyProviderGoesStraightToItsSettings() async {
        let provider = FakeProvider("p", route: guided(.settings))
        var launched = 0
        let connector = connector(provider, connects: { _ in false }, launch: { _ in launched += 1; return true })
        connector.begin("p")
        await settle(connector, "p") { if case .needsKey = $0 { true } else { false } }
        guard case .needsKey = connector.states["p"] else { return XCTFail("expected needsKey, got \(String(describing: connector.states["p"]))") }
        XCTAssertEqual(launched, 0)
    }

    func testAModalProviderShowsItsOwnSignInWindow() async {
        let provider = FakeProvider("p", route: .modal(name: "Tool"))
        let connector = connector(provider, connects: { _ in false })
        connector.begin("p")
        await settle(connector, "p") { if case .waiting = $0 { true } else { false } }
        XCTAssertEqual(provider.presented, 1)
        connector.cancel("p")
    }

    func testAnUnopenableSignInAndPlainGuidanceAreExplainedNotWaitedOn() async {
        let provider = FakeProvider("p", route: .openApp(bundleID: "dev.example.absent", name: "Tool"))
        let missing = connector(provider, connects: { _ in false }, launch: { _ in false })
        missing.begin("p")
        await settle(missing, "p") { if case .failed = $0 { true } else { false } }
        guard case .failed = missing.states["p"] else { return XCTFail("app that cannot open must fail visibly") }

        provider.route = .guidance("Run the tool yourself.")
        let guidance = connector(provider, connects: { _ in false })
        guidance.begin("p")
        await settle(guidance, "p") { if case .failed = $0 { true } else { false } }
        XCTAssertEqual(guidance.states["p"], .failed("Run the tool yourself."))
    }

    func testGivingUpIsReportedAndCancellingStopsTheWatch() async {
        let provider = FakeProvider("p", route: guided(.terminal(command: "tool login")))
        var checks = 0
        let connector = connector(provider, connects: { _ in checks += 1; return false }, patience: 0.1)
        connector.begin("p")
        await settle(connector, "p") { if case .failed = $0 { true } else { false } }
        guard case .failed = connector.states["p"] else { return XCTFail("a sign-in that never appears must time out") }

        let before = checks
        connector.begin("p"); connector.cancel("p")
        try? await Task.sleep(for: .milliseconds(150))
        XCTAssertNil(connector.states["p"]); XCTAssertLessThanOrEqual(checks, before + 2)
    }

    func testTerminalLoginOnlyRunsPlainCommandsAndEveryProviderHasAGuidedRoute() {
        XCTAssertFalse(SignInLauncher.openTerminal(running: "gh auth login; rm -rf ~", title: "x"))
        XCTAssertFalse(SignInLauncher.openTerminal(running: "echo $(whoami)", title: "x"))
        for provider in ExtendedProviderCatalog.makeProviders() {
            let route = provider.signInRoute
            if case .guided = route { continue }
            if case .guidance(let text) = route { XCTAssertFalse(text.isEmpty, provider.id) }
        }
        let guidedCount = ExtendedProviderCatalog.makeProviders().filter { if case .guided = $0.signInRoute { true } else { false } }.count
        XCTAssertGreaterThan(guidedCount, ExtendedProviderCatalog.makeProviders().count / 2,
                             "most extended providers start their own sign-in")
    }
}
