import AppKit
import CodexBarCore
import Darwin
import XCTest
@testable import CodeRim

/// Fifty-odd ways connecting a provider can go, ordered from plain routing up to races,
/// hostile input and misbehaving networks. Most of the ladder sits at the hard end.
@MainActor
final class ProviderConnectionLadderTests: XCTestCase {
    // MARK: Fixtures

    final class Fake: NotchProvider {
        let id: String
        var route: SignInRoute
        var result: () throws -> ProviderSnapshot
        var presented = 0
        init(_ id: String = "copilot", route: SignInRoute = .guidance("native"),
             result: @escaping () throws -> ProviderSnapshot = { throw NotchProviderError.needsAuth }) {
            self.id = id; self.route = route; self.result = result
        }
        var displayName: String { "Fake \(id)" }
        var glyph: ProviderGlyph { .copilot }
        func fetchSnapshot() async throws -> ProviderSnapshot { try result() }
        func account() -> ProviderAccount? { ProviderAccount(label: "native", plan: nil, source: "tool", manageURL: nil) }
        var signInRoute: SignInRoute { route }
        func signOut() async {}
        func presentSignIn() { presented += 1 }
        func forgetCachedCredential() {}
    }

    final class Box<T>: @unchecked Sendable { var value: T; init(_ value: T) { self.value = value } }

    nonisolated static func snapshot(_ status: ProviderStatus, id: String = "copilot") -> ProviderSnapshot {
        var s = ProviderSnapshot(id: id, displayName: "Upstream", glyph: .third, fidelity: .official, status: status,
                                 windows: [LimitWindow(id: "w", label: "Chat", displayValue: "1")], headlineID: "w", accountPlan: "Pro")
        s.block = nil
        return s
    }

    nonisolated static func usage(percent: Double = 40) -> ProviderFetchResult {
        ProviderFetchResult(usage: UsageSnapshot(primary: RateWindow(usedPercent: percent, windowMinutes: 300, resetsAt: nil, resetDescription: nil),
                                                 secondary: nil, updatedAt: Date()),
                            credits: nil, dashboard: nil, sourceLabel: "test", strategyID: "test.api", strategyKind: .apiToken)
    }

    func upstream(_ id: String = "copilot", _ fetch: @escaping ExtendedNotchProvider.Fetch) throws -> ExtendedNotchProvider {
        let descriptor = try XCTUnwrap(ExtendedProviderCatalog.descriptor(for: id))
        return ExtendedNotchProvider(descriptor: descriptor, configuration: { .init(providerID: descriptor.id) }, fetch: fetch)
    }

    func connector(_ provider: Fake?,
                   connects: @escaping (String) async -> Bool = { _ in false },
                   launch: @escaping (SignInRoute) -> Bool = { _ in true },
                   preflight: @escaping (SignInRoute) -> String? = { _ in nil },
                   openInstallPage: @escaping (SignInRoute) -> Void = { _ in },
                   runInApp: @escaping (InAppSignIn, @escaping @MainActor (String) -> Void) async -> InAppSignInRunner.Outcome = { _, _ in .signedIn },
                   allowBrowserSession: @escaping (String) -> Void = { _ in },
                   blocker: @escaping (String) -> String? = { _ in nil },
                   patience: TimeInterval = 5) -> ProviderConnector {
        ProviderConnector(provider: { id in provider?.id == id ? provider : nil }, connects: connects, launch: launch,
                          preflight: preflight, openInstallPage: openInstallPage, runInApp: runInApp,
                          allowBrowserSession: allowBrowserSession, blocker: blocker,
                          pollInterval: .milliseconds(5), patience: patience)
    }

    func wait(_ connector: ProviderConnector, _ id: String = "copilot", timeout: Double = 3,
              until done: (ProviderConnector.State?) -> Bool) async {
        let end = Date().addingTimeInterval(timeout)
        while Date() < end, !done(connector.states[id]) { try? await Task.sleep(for: .milliseconds(5)) }
    }

    func isFailed(_ s: ProviderConnector.State?) -> Bool { if case .failed = s { return true }; return false }
    func isWaiting(_ s: ProviderConnector.State?) -> Bool { if case .waiting = s { return true }; return false }
    func web(_ id: String = "copilot") -> SignInRoute {
        .guided(.init(name: "Tool", action: .browser(URL(string: "https://example.com")!), note: "Sign in.", importsBrowserSession: true))
    }

    // MARK: Level 1 — routing

    func test01_CopilotSignsInInsideTheApp() {
        guard case .guided(let g) = HybridNotchProvider.route(for: "copilot", native: .guidance("x")) else { return XCTFail() }
        XCTAssertEqual(g.action, .inApp(.githubDevice))
    }

    func test02_CursorWithoutTheEditorUsesItsWebsiteAndBrowserSession() {
        let native = SignInRoute.guided(.init(name: "Cursor", action: .terminal(command: "cursor-agent login"), note: "n"))
        guard case .guided(let g) = HybridNotchProvider.route(for: "cursor", native: native) else { return XCTFail() }
        XCTAssertEqual(g.action, .browser(URL(string: "https://cursor.com/dashboard")!))
        XCTAssertTrue(g.importsBrowserSession)
    }

    func test03_CursorWithTheEditorOpensIt() {
        let native = SignInRoute.openApp(bundleID: "com.todesktop.230313mzl4w4u92", name: "Cursor")
        XCTAssertEqual(HybridNotchProvider.route(for: "cursor", native: native), native)
    }

    func test04_GrokFollowsWhetherItsCLIIsInstalled() {
        let native = SignInRoute.guided(.init(name: "Grok", action: .terminal(command: "grok login"), note: "n"))
        let route = HybridNotchProvider.route(for: "grok", native: native)
        if SignInLauncher.installedTool("grok") != nil { XCTAssertEqual(route, native) }
        else { guard case .guided(let g) = route else { return XCTFail() }; XCTAssertTrue(g.importsBrowserSession) }
    }

    func test05_KeyProvidersAskForAKeyInCodeRim() {
        for id in ["glm", "ollama"] {
            guard case .guided(let g) = HybridNotchProvider.route(for: id, native: .guidance("x")) else { return XCTFail(id) }
            XCTAssertTrue(g.opensSettings, id)
        }
    }

    func test06_LocalOllamaIsNotWrapped() {
        let local = Fake("ollama-local")
        XCTAssertTrue(HybridNotchProvider.wrapping(local) as AnyObject === local)
    }

    func test07_AnIdWithoutAnUpstreamIsLeftAlone() {
        let unknown = Fake("not-a-provider")
        XCTAssertTrue(HybridNotchProvider.wrapping(unknown) as AnyObject === unknown)
        XCTAssertEqual(HybridNotchProvider.route(for: "not-a-provider", native: .guidance("keep")), .guidance("keep"))
    }

    func test08_APastedKeyLosesStrayWhitespace() {
        let configuration = ExtendedProviderConfiguration(providerID: .zai)
        XCTAssertEqual(configuration.environmentInput(" \tkey-123 \n", for: "Z_AI_API_KEY"), "key-123")
    }

    // MARK: Level 2 — fallback and states

    func test09_ANativeReadingNeverConsultsUpstream() async throws {
        let calls = Box(0)
        let hybrid = HybridNotchProvider(native: Fake(result: { Self.snapshot(.ok) }),
                                         upstream: try upstream { _, _ in calls.value += 1; return Self.usage() })
        _ = try await hybrid.fetchSnapshot()
        XCTAssertEqual(calls.value, 0)
    }

    func test10_ASignedOutStatusFromTheNativeReaderFallsBack() async throws {
        let hybrid = HybridNotchProvider(native: Fake(result: { Self.snapshot(.needsAuth) }), upstream: try upstream { _, _ in Self.usage() })
        let result = try await hybrid.fetchSnapshot()
        XCTAssertEqual(result.status, .ok)
        XCTAssertEqual(result.id, "copilot")
    }

    func test11_BothSignedOutStaysSignedOut() async throws {
        let hybrid = HybridNotchProvider(native: Fake(), upstream: try upstream { _, _ in throw ProviderFetchError.noAvailableStrategy(.copilot) })
        do { _ = try await hybrid.fetchSnapshot(); XCTFail() } catch NotchProviderError.needsAuth {}
    }

    func test12_ARefusedCredentialIsReportedWhenUpstreamCannotHelp() async throws {
        let hybrid = HybridNotchProvider(native: Fake(result: { throw NotchProviderError.accessDenied }),
                                         upstream: try upstream { _, _ in throw ProviderFetchError.noAvailableStrategy(.copilot) })
        do { _ = try await hybrid.fetchSnapshot(); XCTFail() } catch NotchProviderError.accessDenied {}
    }

    func test13_ARefusedCredentialIsRescuedByCodeRimsOwnSignIn() async throws {
        let hybrid = HybridNotchProvider(native: Fake(result: { throw NotchProviderError.accessDenied }),
                                         upstream: try upstream { _, _ in Self.usage() })
        let result = try await hybrid.fetchSnapshot()
        XCTAssertEqual(result.status, .ok)
    }

    func test14_AFallbackReadingKeepsTheCellsIdentity() async throws {
        let native = Fake()
        let hybrid = HybridNotchProvider(native: native, upstream: try upstream { _, _ in Self.usage() })
        let result = try await hybrid.fetchSnapshot()
        XCTAssertEqual(result.displayName, native.displayName)
        XCTAssertEqual(result.glyph, native.glyph)
        XCTAssertFalse(result.windows.isEmpty)
    }

    func test15_AnExistingSignInConnectsWithoutOpeningAnything() async {
        let launched = Box(0)
        let c = connector(Fake(route: web()), connects: { _ in true }, launch: { _ in launched.value += 1; return true })
        c.begin("copilot")
        await wait(c) { $0 == .connected }
        XCTAssertEqual(c.states["copilot"], .connected); XCTAssertEqual(launched.value, 0)
    }

    func test16_AKeyRouteWaitsForTheKey() async {
        let c = connector(Fake(route: .guided(.init(name: "Z", action: .settings, note: "Paste it."))))
        c.begin("copilot")
        await wait(c) { if case .needsKey = $0 { return true }; return false }
        XCTAssertEqual(c.states["copilot"], .needsKey("Paste it."))
    }

    func test17_GuidanceIsShownNotWaitedOn() async {
        let c = connector(Fake(route: .guidance("Do it yourself.")))
        c.begin("copilot")
        await wait(c, until: isFailed)
        XCTAssertEqual(c.states["copilot"], .failed("Do it yourself."))
    }

    func test18_AModalProviderShowsItsWindowOnce() async {
        let fake = Fake(route: .modal(name: "Tool"))
        let c = connector(fake)
        c.begin("copilot")
        await wait(c, until: isWaiting)
        XCTAssertEqual(fake.presented, 1)
        c.cancel("copilot")
    }

    // MARK: Level 3 — races, hostile input, broken networks

    func test19_CancellingWhileCheckingNeverLeavesAConnectedState() async {
        let gate = Box(false)
        let c = connector(Fake(route: web()), connects: { _ in
            while !gate.value { try? await Task.sleep(for: .milliseconds(2)) }
            return true
        })
        c.begin("copilot")
        try? await Task.sleep(for: .milliseconds(20))
        c.cancel("copilot")
        gate.value = true
        try? await Task.sleep(for: .milliseconds(80))
        XCTAssertNil(c.states["copilot"], "a cancelled check must not report Connected afterwards")
    }

    func test20_CancellingWhileWaitingNeverLeavesAConnectedState() async {
        let checks = Box(0), gate = Box(false)
        let c = connector(Fake(route: web()), connects: { _ in
            checks.value += 1
            if checks.value < 3 { return false }
            while !gate.value { try? await Task.sleep(for: .milliseconds(2)) }
            return true
        })
        c.begin("copilot")
        await wait(c) { _ in checks.value >= 3 }
        c.cancel("copilot")
        gate.value = true
        try? await Task.sleep(for: .milliseconds(80))
        XCTAssertNil(c.states["copilot"])
    }

    func test21_StartingTwiceOpensTheSignInOnce() async {
        let launched = Box(0), gate = Box(false)
        let c = connector(Fake(route: web()), connects: { _ in
            while !gate.value { try? await Task.sleep(for: .milliseconds(2)) }
            return false
        }, launch: { _ in launched.value += 1; return true })
        c.begin("copilot"); c.begin("copilot")
        gate.value = true
        await wait(c, until: isWaiting)
        try? await Task.sleep(for: .milliseconds(40))
        XCTAssertEqual(launched.value, 1)
        c.cancel("copilot")
    }

    func test22_AnOlderRunCannotOverwriteANewerOne() async {
        let first = Box(true)
        let c = connector(Fake(route: .guidance("old")), connects: { _ in
            if first.value { first.value = false; try? await Task.sleep(for: .milliseconds(60)); return true }
            return false
        })
        c.begin("copilot")
        try? await Task.sleep(for: .milliseconds(10))
        c.begin("copilot")
        await wait(c, until: isFailed)
        try? await Task.sleep(for: .milliseconds(120))
        XCTAssertEqual(c.states["copilot"], .failed("old"), "the superseded check finished later and must be ignored")
    }

    func test23_TryingAgainAfterAFailureStartsFresh() async {
        let fake = Fake(route: .guidance("first"))
        let c = connector(fake)
        c.begin("copilot"); await wait(c, until: isFailed)
        fake.route = .guided(.init(name: "Z", action: .settings, note: "key"))
        c.begin("copilot")
        await wait(c) { if case .needsKey = $0 { return true }; return false }
        XCTAssertEqual(c.states["copilot"], .needsKey("key"))
    }

    func test24_ABlockerEndsTheWaitWithItsReason() async {
        let c = connector(Fake(route: web()), blocker: { _ in "Plan does not include usage." })
        c.begin("copilot")
        await wait(c, until: isFailed)
        XCTAssertEqual(c.states["copilot"], .failed("Plan does not include usage."))
    }

    func test25_ALateBlockerLetsTheWaitRunUntilItAppears() async {
        let polls = Box(0)
        let c = connector(Fake(route: web()), connects: { _ in polls.value += 1; return false },
                          blocker: { _ in polls.value >= 6 ? "Refused." : nil })
        c.begin("copilot")
        await wait(c, until: isFailed)
        XCTAssertGreaterThanOrEqual(polls.value, 6)
        XCTAssertEqual(c.states["copilot"], .failed("Refused."))
    }

    func test26_PatienceRunsOutWithAPlainMessage() async {
        let c = connector(Fake(route: web()), patience: 0.05)
        c.begin("copilot")
        await wait(c, until: isFailed)
        guard case .failed(let reason) = c.states["copilot"] else { return XCTFail() }
        XCTAssertTrue(reason.contains("not detected"))
    }

    func test27_AnInAppSignInThatLandsLateStillConnects() async {
        let polls = Box(0)
        let c = connector(Fake(route: .guided(.init(name: "G", action: .inApp(.githubDevice), note: "n"))),
                          connects: { _ in polls.value += 1; return polls.value >= 4 })
        c.begin("copilot")
        await wait(c) { $0 == .connected }
        XCTAssertEqual(c.states["copilot"], .connected)
    }

    func test28_InAppStepsAreShownWhileTheyHappen() async {
        let release = Box(false)
        let c = connector(Fake(route: .guided(.init(name: "G", action: .inApp(.githubDevice), note: "n"))),
                          runInApp: { _, update in
                              update("Enter the code ZZZZ-9999")
                              while !release.value { try? await Task.sleep(for: .milliseconds(2)) }
                              return .signedIn
                          })
        c.begin("copilot")
        await wait(c) { $0 == .waiting("Enter the code ZZZZ-9999") }
        XCTAssertEqual(c.states["copilot"], .waiting("Enter the code ZZZZ-9999"))
        release.value = true
        c.cancel("copilot")
    }

    func test29_AStepReportedAfterCancelIsIgnored() async {
        let release = Box(false)
        let c = connector(Fake(route: .guided(.init(name: "G", action: .inApp(.githubDevice), note: "n"))),
                          runInApp: { _, update in
                              while !release.value { try? await Task.sleep(for: .milliseconds(2)) }
                              update("late step")
                              return .signedIn
                          })
        c.begin("copilot")
        try? await Task.sleep(for: .milliseconds(20))
        c.cancel("copilot")
        release.value = true
        try? await Task.sleep(for: .milliseconds(60))
        XCTAssertNil(c.states["copilot"])
    }

    func test30_BrowserImportIsAllowedBeforeTheBrowserOpens() async {
        let order = Box([String]())
        let c = connector(Fake(route: web()), launch: { _ in order.value.append("launch"); return true },
                          allowBrowserSession: { _ in order.value.append("allow") })
        c.begin("copilot")
        await wait(c, until: isWaiting)
        XCTAssertEqual(order.value, ["allow", "launch"])
        c.cancel("copilot")
    }

    func test31_AMissingToolOpensItsInstallPageInsteadOfAnEmptyTerminal() async {
        let pages = Box(0), launched = Box(0)
        let route = SignInRoute.guided(.init(name: "T", action: .terminal(command: "definitely-not-installed-xyz login"), note: "n",
                                             installURL: URL(string: "https://example.com/install")))
        let c = connector(Fake(route: route), launch: { _ in launched.value += 1; return true },
                          preflight: { SignInLauncher.problem(with: $0) }, openInstallPage: { _ in pages.value += 1 })
        c.begin("copilot")
        await wait(c, until: isFailed)
        guard case .failed(let reason) = c.states["copilot"] else { return XCTFail() }
        XCTAssertTrue(reason.contains("definitely-not-installed-xyz")); XCTAssertEqual(pages.value, 1); XCTAssertEqual(launched.value, 0)
    }

    func test32_ASignInThatCannotOpenSaysSo() async {
        let c = connector(Fake(route: web()), launch: { _ in false })
        c.begin("copilot")
        await wait(c, until: isFailed)
        guard case .failed(let reason) = c.states["copilot"] else { return XCTFail() }
        XCTAssertTrue(reason.contains("could not be opened"))
    }

    func test33_AnUnknownProviderIsANoOp() async {
        let c = connector(nil)
        c.begin("copilot")
        try? await Task.sleep(for: .milliseconds(20))
        XCTAssertNil(c.states["copilot"])
    }

    // MARK: Level 3 — the Google redirect server

    func test34_TheRedirectIgnoresTheFaviconAndSettlesOnTheCallback() async throws {
        let server = OAuthLoopbackServer(state: "s1")
        let base = try await server.start(); defer { server.stop() }
        let st1 = try await Self.status(URL(string: "/favicon.ico", relativeTo: base)!.absoluteURL); XCTAssertEqual(st1, 404)
        async let callback = server.waitForCallback()
        let st2 = try await Self.status(URL(string: base.absoluteString + "?code=C&state=s1")!); XCTAssertEqual(st2, 200)
        let value = try await callback
        XCTAssertEqual(value.code, "C")
    }

    func test35_AForgedStateIsRefused() async throws {
        let server = OAuthLoopbackServer(state: "real")
        let base = try await server.start(); defer { server.stop() }
        async let callback = server.waitForCallback()
        let st3 = try await Self.status(URL(string: base.absoluteString + "?code=C&state=fake")!); XCTAssertEqual(st3, 400)
        let value = try await callback
        XCTAssertNotNil(value.error)
    }

    func test36_ADeclinedConsentIsReported() async throws {
        let server = OAuthLoopbackServer(state: "s")
        let base = try await server.start(); defer { server.stop() }
        async let callback = server.waitForCallback()
        _ = try await Self.status(URL(string: base.absoluteString + "?error=access_denied&state=s")!)
        let value = try await callback
        XCTAssertEqual(value.error, "access_denied")
    }

    func test37_ACallbackWithoutStateIsNotTreatedAsASuccess() async throws {
        let server = OAuthLoopbackServer(state: "s")
        let base = try await server.start(); defer { server.stop() }
        async let callback = server.waitForCallback()
        let code = try await Self.status(URL(string: base.absoluteString + "?code=C")!)
        let value = try await callback
        XCTAssertNotNil(value.error, "a redirect missing its state cannot be trusted")
        XCTAssertEqual(code, 400, "and the browser must not be told it signed in")
    }

    func test38_ARequestDeliveredInTinyPiecesIsStillUnderstood() async throws {
        let server = OAuthLoopbackServer(state: "s")
        let base = try await server.start(); defer { server.stop() }
        let request = "GET /callback?code=PIECES&state=s HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"
        async let callback = server.waitForCallback()
        let reply = Self.raw(port: base.port!, chunks: request.map { Data(String($0).utf8) }, gap: 0.002)
        let value = try await callback
        XCTAssertEqual(value.code, "PIECES")
        XCTAssertTrue(reply?.hasPrefix("HTTP/1.1 200") == true)
    }

    func test39_AnEndlessHeaderIsCutOffInsteadOfHeldOpen() async throws {
        let server = OAuthLoopbackServer(state: "s")
        let base = try await server.start(); defer { server.stop() }
        let junk = "GET /callback?state=s HTTP/1.1\r\nX: " + String(repeating: "a", count: 200_000)
        let started = Date()
        let reply = Self.raw(port: base.port!, chunks: [Data(junk.utf8)], gap: 0, readTimeout: 3)
        XCTAssertLessThan(Date().timeIntervalSince(started), 2.5, "the server answered instead of waiting for more")
        XCTAssertTrue(reply?.hasPrefix("HTTP/1.1 431") == true, "got \(String(describing: reply?.prefix(40)))")
    }

    func test40_ASecondCallbackDoesNotReplaceTheFirst() async throws {
        let server = OAuthLoopbackServer(state: "s")
        let base = try await server.start(); defer { server.stop() }
        _ = try await Self.status(URL(string: base.absoluteString + "?code=FIRST&state=s")!)
        _ = try? await Self.status(URL(string: base.absoluteString + "?code=SECOND&state=s")!)
        let value = try await server.waitForCallback()
        XCTAssertEqual(value.code, "FIRST")
    }

    func test41_ACallbackBeforeAnyoneWaitsIsKept() async throws {
        let server = OAuthLoopbackServer(state: "s")
        let base = try await server.start(); defer { server.stop() }
        _ = try await Self.status(URL(string: base.absoluteString + "?code=EARLY&state=s")!)
        try? await Task.sleep(for: .milliseconds(30))
        let value = try await server.waitForCallback()
        XCTAssertEqual(value.code, "EARLY")
    }

    func test42_CancellingTheWaitUnblocksIt() async throws {
        let server = OAuthLoopbackServer(state: "s")
        _ = try await server.start()
        Task { try? await Task.sleep(for: .milliseconds(30)); server.cancelCallbackWait(with: CancellationError()) }
        do { _ = try await server.waitForCallback(); XCTFail() } catch is CancellationError {}
    }

    func test43_GarbageBytesNeitherCrashNorSettle() async throws {
        let server = OAuthLoopbackServer(state: "s")
        let base = try await server.start(); defer { server.stop() }
        _ = Self.raw(port: base.port!, chunks: [Data([0xFF, 0x00, 0x13, 0x37]) + Data("\r\n\r\n".utf8)], gap: 0, readTimeout: 1)
        async let callback = server.waitForCallback()
        _ = try await Self.status(URL(string: base.absoluteString + "?code=AFTER&state=s")!)
        let value = try await callback
        XCTAssertEqual(value.code, "AFTER", "junk must not have settled the sign-in")
    }

    func test44_TheRedirectOnlyListensOnLoopback() async throws {
        let server = OAuthLoopbackServer(state: "s")
        let base = try await server.start(); defer { server.stop() }
        XCTAssertEqual(base.host, "127.0.0.1")
        XCTAssertNil(Self.raw(host: Self.lanAddress(), port: base.port!, chunks: [Data("GET / HTTP/1.1\r\n\r\n".utf8)], gap: 0, readTimeout: 1),
                     "another machine on the network must not reach the redirect")
    }

    // MARK: Level 3 — GitHub device flow

    func test45_ADeviceCodeThatCannotBeRequestedFails() async {
        let outcome = await InAppSignInRunner.gitHubDevice(requestCode: { throw URLError(.timedOut) }, poll: { _, _ in "" },
                                                           save: { _ in }, open: { _ in }, update: { _ in })
        guard case .failed(let reason) = outcome else { return XCTFail() }
        XCTAssertTrue(reason.contains("GitHub"))
    }

    func test46_ACancelledDevicePollSaysCancelled() async {
        let outcome = await InAppSignInRunner.gitHubDevice(requestCode: { ("A-B", "d", nil, 1) }, poll: { _, _ in throw CancellationError() },
                                                           save: { _ in }, open: { _ in }, update: { _ in })
        XCTAssertEqual(outcome, .failed("Sign-in was cancelled."))
    }

    func test47_ATokenThatCannotBeSavedIsNotReportedAsSuccess() async {
        let outcome = await InAppSignInRunner.gitHubDevice(requestCode: { ("A-B", "d", nil, 1) }, poll: { _, _ in "tok" },
                                                           save: { _ in throw CocoaError(.fileWriteNoPermission) }, open: { _ in }, update: { _ in })
        guard case .failed = outcome else { return XCTFail("a lost token must not look like a sign-in") }
    }

    func test48_TheDeviceCodeIsCopiedAndItsPageOpened() async {
        let opened = Box<URL?>(nil)
        let previous = NSPasteboard.general.string(forType: .string)
        defer { if let previous { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(previous, forType: .string) } }
        _ = await InAppSignInRunner.gitHubDevice(requestCode: { ("QWER-5678", "d", URL(string: "https://github.com/login/device"), 1) },
                                                 poll: { _, _ in "tok" }, save: { _ in }, open: { opened.value = $0 }, update: { _ in })
        XCTAssertEqual(NSPasteboard.general.string(forType: .string), "QWER-5678")
        XCTAssertEqual(opened.value?.host, "github.com")
    }

    func test49_AntigravityWithoutItsAppAsksForTheInstall() async {
        let outcome = await InAppSignInRunner.antigravityGoogle(client: { nil }, update: { _ in })
        guard case .failed(let reason) = outcome else { return XCTFail() }
        XCTAssertTrue(reason.contains("Install the Antigravity app"))
    }

    func test49b_CancellingTheGoogleSignInEndsItAndClosesThePort() async throws {
        let client = AntigravityOAuthClient(clientID: "id.apps.googleusercontent.com", clientSecret: "GOCSPX-x")
        let opened = Box<URL?>(nil)
        let task = Task { @MainActor in
            await InAppSignInRunner.antigravityGoogle(client: { client }, open: { opened.value = $0; return true }, update: { _ in })
        }
        for _ in 0..<100 where opened.value == nil { try await Task.sleep(for: .milliseconds(20)) }
        let redirect = try XCTUnwrap(opened.value.flatMap { URLComponents(url: $0, resolvingAgainstBaseURL: false) }?
            .queryItems?.first { $0.name == "redirect_uri" }?.value.flatMap(URL.init(string:)))
        task.cancel()
        let outcome = await task.value
        guard case .failed(let reason) = outcome else { return XCTFail() }
        XCTAssertTrue(reason.contains("cancelled"))
        try await Task.sleep(for: .milliseconds(200))
        XCTAssertNil(Self.raw(port: try XCTUnwrap(redirect.port), chunks: [Data("GET /callback HTTP/1.1\r\n\r\n".utf8)], gap: 0, readTimeout: 1))
    }

    func test49c_AnUnansweredGoogleSignInTimesOut() async {
        let client = AntigravityOAuthClient(clientID: "id.apps.googleusercontent.com", clientSecret: "GOCSPX-x")
        let outcome = await InAppSignInRunner.antigravityGoogle(client: { client }, open: { _ in true },
                                                               timeout: .milliseconds(300), update: { _ in })
        guard case .failed(let reason) = outcome else { return XCTFail() }
        XCTAssertTrue(reason.contains("timed out"))
    }

    // MARK: Level 3 — hostile text and environment

    func test50_TerminalStepsCannotRunInjectedCommands() throws {
        let marker = FileManager.default.temporaryDirectory.appendingPathComponent("coderim-injected-\(UUID().uuidString)")
        let evil = "Step 1 '; touch \(marker.path); echo '\n`touch \(marker.path)`\n$(touch \(marker.path))\n\"; touch \(marker.path)"
        let script = SignInLauncher.hintBlock(evil).replacingOccurrences(of: "read -r '?Press Return to start. '", with: "true")
        let process = Process(); process.executableURL = URL(fileURLWithPath: "/bin/zsh"); process.arguments = ["-c", script]
        process.standardOutput = Pipe(); process.standardError = Pipe()
        try process.run(); process.waitUntilExit()
        XCTAssertFalse(FileManager.default.fileExists(atPath: marker.path), "the hint executed a command")
        XCTAssertEqual(process.terminationStatus, 0)
    }

    func test51_ATerminalCommandWithShellSyntaxIsRefused() {
        XCTAssertFalse(SignInLauncher.openTerminal(running: "gh auth login; rm -rf ~", title: "x"))
        XCTAssertFalse(SignInLauncher.openTerminal(running: "gh $(whoami)", title: "x"))
    }

    func test52_ToolLookupHandlesPathsAndBlanks() throws {
        XCTAssertNil(SignInLauncher.installedTool(""))
        XCTAssertNil(SignInLauncher.installedTool("/nonexistent/tool login"))
        XCTAssertEqual(SignInLauncher.installedTool("/bin/zsh -l"), "/bin/zsh")
        XCTAssertEqual(SignInLauncher.installedTool("zsh", searchPath: ["/bin"]), "/bin/zsh")
    }

    func test53_OnlyAllowedSettingsReachTheReader() {
        var configuration = ExtendedProviderConfiguration(providerID: .antigravity)
        configuration.environment["ANTIGRAVITY_OAUTH_CREDENTIALS_JSON"] = "{}"
        configuration.environment["PATH"] = "/evil"
        configuration.environment["ANTIGRAVITY_CLI_PATH"] = ""
        let env = configuration.fetchEnvironment(base: ["PATH": "/usr/bin"])
        XCTAssertEqual(env["ANTIGRAVITY_OAUTH_CREDENTIALS_JSON"], "{}")
        XCTAssertEqual(env["PATH"], "/usr/bin", "an unlisted key must not override the environment")
        XCTAssertNil(env["ANTIGRAVITY_CLI_PATH"], "an empty value is not passed on")
    }

    func test54_AnUnconfiguredKeyReadsAsSignedOutNotBroken() {
        XCTAssertEqual(ExtendedProviderFailureClassifier.status(for: SampleSettingsError.missingAPIKey, provider: "OpenCode"), .needsAuth)
        XCTAssertNil(ExtendedProviderFailureClassifier.status(for: SampleSettingsError.rateLimited, provider: "OpenCode"))
        XCTAssertNil(ExtendedProviderFailureClassifier.status(for: URLError(.cannotFindHost), provider: "OpenCode"))
    }

    func test55_ACachedOfflineReadingIsNotShownAsConnected() async throws {
        let offline = ProviderFetchResult(usage: Self.usage().usage, credits: nil, dashboard: nil, sourceLabel: "offline",
                                          strategyID: "antigravity.offline", strategyKind: .localProbe)
        let hybrid = HybridNotchProvider(native: Fake("gemini"), upstream: try upstream("gemini") { _, _ in offline })
        do { _ = try await hybrid.fetchSnapshot(); XCTFail("old numbers presented as live") } catch NotchProviderError.needsAuth {}
    }

    func test56_SwitchingBackToTheNativeSignInUsesItsAccount() async throws {
        let native = Fake()
        let hybrid = HybridNotchProvider(native: native, upstream: try upstream { _, _ in Self.usage() })
        _ = try await hybrid.fetchSnapshot()
        native.result = { Self.snapshot(.ok) }
        _ = try await hybrid.fetchSnapshot()
        XCTAssertEqual(hybrid.account()?.label, "native")
    }

    // MARK: Helpers

    enum SampleSettingsError: Error { case missingAPIKey, rateLimited }

    static func status(_ url: URL) async throws -> Int {
        var request = URLRequest(url: url); request.timeoutInterval = 3
        let (_, response) = try await URLSession(configuration: .ephemeral).data(for: request)
        return (response as? HTTPURLResponse)?.statusCode ?? -1
    }

    static func lanAddress() -> String {
        var pointer: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&pointer) == 0, let first = pointer else { return "10.255.255.1" }
        defer { freeifaddrs(pointer) }
        for item in sequence(first: first, next: { $0.pointee.ifa_next }) {
            guard let addr = item.pointee.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET) else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST)
            let text = String(decoding: host.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self)
            if text != "127.0.0.1" && !text.hasPrefix("169.254") { return text }
        }
        return "10.255.255.1"
    }

    /// Sends raw bytes and returns the reply, or nil when the connection could not be made.
    nonisolated static func raw(host: String = "127.0.0.1", port: Int, chunks: [Data], gap: TimeInterval, readTimeout: TimeInterval = 3) -> String? {
        let fd = socket(AF_INET, SOCK_STREAM, 0); guard fd >= 0 else { return nil }
        defer { close(fd) }
        var timeout = timeval(tv_sec: Int(readTimeout), tv_usec: Int32((readTimeout - floor(readTimeout)) * 1_000_000))
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        var noSigPipe: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &noSigPipe, socklen_t(MemoryLayout<Int32>.size))
        var address = sockaddr_in(); address.sin_len = UInt8(MemoryLayout<sockaddr_in>.stride)
        address.sin_family = sa_family_t(AF_INET); address.sin_port = in_port_t(UInt16(port)).bigEndian
        address.sin_addr = in_addr(s_addr: inet_addr(host))
        let flags = fcntl(fd, F_GETFL); _ = fcntl(fd, F_SETFL, flags | O_NONBLOCK)
        _ = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.stride)) } }
        var poller = pollfd(fd: fd, events: Int16(POLLOUT), revents: 0)
        guard poll(&poller, 1, Int32(readTimeout * 1000)) == 1 else { return nil }
        var error: Int32 = 0; var length = socklen_t(MemoryLayout<Int32>.size)
        getsockopt(fd, SOL_SOCKET, SO_ERROR, &error, &length)
        guard error == 0 else { return nil }
        _ = fcntl(fd, F_SETFL, flags)
        for chunk in chunks {
            _ = chunk.withUnsafeBytes { send(fd, $0.baseAddress, chunk.count, 0) }
            if gap > 0 { Thread.sleep(forTimeInterval: gap) }
        }
        var buffer = [UInt8](repeating: 0, count: 4096)
        let n = recv(fd, &buffer, buffer.count, 0)
        return n > 0 ? String(decoding: buffer[0..<n], as: UTF8.self) : ""
    }
}
