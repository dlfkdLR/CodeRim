import CodexBarCore
import XCTest
@testable import CodeRim

/// The ways connecting a provider goes wrong in practice, each expected to end in a state the
/// user can act on rather than an endless "Waiting for sign-in…".
@MainActor
final class ProviderEdgeCaseTests: XCTestCase {
    private final class Native: NotchProvider {
        var result: () throws -> ProviderSnapshot
        init(_ result: @escaping () throws -> ProviderSnapshot) { self.result = result }
        var id: String { "copilot" }
        var displayName: String { "GitHub Copilot" }
        var glyph: ProviderGlyph { .copilot }
        func fetchSnapshot() async throws -> ProviderSnapshot { try result() }
        func account() -> ProviderAccount? { nil }
        var route: SignInRoute = .guidance("native")
        var signInRoute: SignInRoute { route }
        func signOut() async {}
        func presentSignIn() {}
        func forgetCachedCredential() {}
    }

    private final class Counter: @unchecked Sendable { var value = 0 }

    private func upstream(_ fetch: @escaping ExtendedNotchProvider.Fetch) throws -> ExtendedNotchProvider {
        let descriptor = try XCTUnwrap(ExtendedProviderCatalog.descriptor(for: "copilot"))
        return ExtendedNotchProvider(descriptor: descriptor, configuration: { .init(providerID: descriptor.id) }, fetch: fetch)
    }

    // 1. macOS refused the borrowed credential, but CodeRim's own sign-in can still read.
    func testARefusedBorrowedCredentialStillTriesCodeRimsOwnSignIn() async throws {
        let upstreamCalls = Counter()
        let hybrid = HybridNotchProvider(native: Native { throw NotchProviderError.accessDenied },
            upstream: try upstream { _, _ in upstreamCalls.value += 1; throw ProviderFetchError.noAvailableStrategy(.copilot) })
        do { _ = try await hybrid.fetchSnapshot(); XCTFail("expected the original refusal") }
        catch NotchProviderError.accessDenied {}
        XCTAssertEqual(upstreamCalls.value, 1, "the upstream reader is consulted before giving up")
    }

    // 2. A network failure of the borrowed reader is reported, not masked by "sign in".
    func testANetworkFailureIsNotTurnedIntoASignInPrompt() async throws {
        let upstreamCalls = Counter()
        let hybrid = HybridNotchProvider(native: Native { throw URLError(.notConnectedToInternet) },
            upstream: try upstream { _, _ in upstreamCalls.value += 1; throw ProviderFetchError.noAvailableStrategy(.copilot) })
        do { _ = try await hybrid.fetchSnapshot(); XCTFail("expected the network error") }
        catch is URLError {}
        XCTAssertEqual(upstreamCalls.value, 0)
    }

    // 3. A sign-in that succeeds but cannot be read (plan, permission, Keychain) stops waiting and says why.
    func testAReadableProblemEndsTheWaitWithItsReason() async {
        let route = SignInRoute.guided(.init(name: "Tool", action: .browser(URL(string: "https://example.com")!), note: "Sign in."))
        let provider = Native { throw NotchProviderError.needsAuth }
        provider.route = route
        let connector = ProviderConnector(provider: { _ in provider }, connects: { _ in false },
            launch: { _ in true }, preflight: { _ in nil }, openInstallPage: { _ in },
            runInApp: { _, _ in .signedIn }, allowBrowserSession: { _ in },
            blocker: { _ in "macOS refused CodeRim access to your browser's saved data. Choose Always Allow, then Try again." },
            pollInterval: .milliseconds(5), patience: 5)
        _ = route
        connector.begin("copilot")
        for _ in 0..<200 where { if case .failed = connector.states["copilot"] { return false }; return true }() {
            try? await Task.sleep(for: .milliseconds(10))
        }
        guard case .failed(let reason) = connector.states["copilot"] else { return XCTFail("still \(String(describing: connector.states["copilot"]))") }
        XCTAssertTrue(reason.contains("Always Allow"))
    }

    // 4. Cancelling while an in-app sign-in is still waiting leaves nothing behind.
    func testCancellingAnInAppSignInLeavesNoState() async {
        let provider = Native { throw NotchProviderError.needsAuth }
        var finished = false
        let connector = ProviderConnector(provider: { _ in provider }, connects: { _ in false },
            launch: { _ in true }, preflight: { _ in nil }, openInstallPage: { _ in },
            runInApp: { _, update in
                update("Enter the code")
                try? await Task.sleep(for: .seconds(5))
                finished = true
                return Task.isCancelled ? .failed("Sign-in was cancelled.") : .signedIn
            }, allowBrowserSession: { _ in }, pollInterval: .milliseconds(5), patience: 5)
        _ = finished
        provider.route = .guided(.init(name: "GitHub", action: .inApp(.githubDevice), note: "Sign in."))
        // Route the fake provider through an in-app sign-in.
        let hybridRoute = HybridNotchProvider.route(for: "copilot", native: .guidance("x"))
        guard case .guided(let guided) = hybridRoute else { return XCTFail() }
        XCTAssertEqual(guided.action, .inApp(.githubDevice))
        connector.begin("copilot")
        try? await Task.sleep(for: .milliseconds(30))
        connector.cancel("copilot")
        try? await Task.sleep(for: .milliseconds(60))
        XCTAssertNil(connector.states["copilot"])
    }

    // 5. The loopback redirect ignores the browser's favicon request and settles on the real callback.
    func testTheGoogleRedirectSettlesOnlyOnItsCallback() async throws {
        let server = OAuthLoopbackServer(state: "abc")
        let base = try await server.start()
        let favicon = URL(string: "/favicon.ico", relativeTo: base)!.absoluteURL
        let (_, faviconResponse) = try await URLSession.shared.data(from: favicon)
        XCTAssertEqual((faviconResponse as? HTTPURLResponse)?.statusCode, 404)
        async let callback = server.waitForCallback()
        let (_, ok) = try await URLSession.shared.data(from: URL(string: base.absoluteString + "?code=XYZ&state=abc")!)
        XCTAssertEqual((ok as? HTTPURLResponse)?.statusCode, 200)
        let result = try await callback
        XCTAssertEqual(result.code, "XYZ"); XCTAssertNil(result.error)
        server.stop()
    }

    // 6. A forged or stale redirect is refused, and a user who declines consent is told so.
    func testAForgedOrDeclinedRedirectFails() async throws {
        let forged = OAuthLoopbackServer(state: "expected")
        let base = try await forged.start()
        async let first = forged.waitForCallback()
        _ = try? await URLSession.shared.data(from: URL(string: base.absoluteString + "?code=X&state=other")!)
        let mismatch = try await first
        XCTAssertNotNil(mismatch.error); forged.stop()

        let declined = OAuthLoopbackServer(state: "s")
        let url = try await declined.start()
        async let second = declined.waitForCallback()
        _ = try? await URLSession.shared.data(from: URL(string: url.absoluteString + "?error=access_denied&state=s")!)
        let denied = try await second
        XCTAssertEqual(denied.error, "access_denied"); declined.stop()
    }

    // 7. GitHub's device flow failing (expired or denied code) becomes a reason, not a hang.
    func testAFailedDeviceCodeBecomesAReason() async {
        let outcome = await InAppSignInRunner.gitHubDevice(
            requestCode: { throw URLError(.timedOut) }, poll: { _, _ in "never" }, save: { _ in }, open: { _ in }, update: { _ in })
        guard case .failed(let reason) = outcome else { return XCTFail("expected failure") }
        XCTAssertTrue(reason.contains("GitHub"))
    }

    // 8. A successful device flow stores the token and shows the code it asked for.
    func testASuccessfulDeviceCodeStoresTheToken() async {
        var saved: String?, shown: [String] = []
        let outcome = await InAppSignInRunner.gitHubDevice(
            requestCode: { ("ABCD-1234", "dev", URL(string: "https://github.com/login/device")!, 1) },
            poll: { _, _ in "gho_test" }, save: { saved = $0 }, open: { _ in }, update: { shown.append($0) })
        XCTAssertEqual(outcome, InAppSignInRunner.Outcome.signedIn)
        XCTAssertEqual(saved, "gho_test")
        XCTAssertTrue(shown.first?.contains("ABCD-1234") == true)
    }

    // 9. Antigravity without its app explains the install step instead of opening a dead sign-in.
    func testAntigravityWithoutItsAppAsksForTheInstall() async {
        let outcome = await InAppSignInRunner.antigravityGoogle(client: { nil }, update: { _ in })
        guard case .failed(let reason) = outcome else { return XCTFail("expected failure") }
        XCTAssertTrue(reason.contains("Install the Antigravity app"))
    }

    // 10. A key pasted with stray whitespace or a newline is stored clean.
    func testAPastedKeyIsTrimmed() {
        var configuration = ExtendedProviderConfiguration(providerID: .zai)
        XCTAssertEqual(configuration.environmentInput("  sk-abc\n", for: "Z_AI_API_KEY"), "sk-abc")
        configuration.provider.apiKey = "sk-abc"
        XCTAssertEqual(configuration.provider.apiKey, "sk-abc")
    }
}
