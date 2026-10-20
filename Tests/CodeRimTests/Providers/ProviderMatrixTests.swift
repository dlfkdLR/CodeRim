import CodexBarCore
import Foundation
import XCTest
@testable import CodeRim

/// Every additional provider through every way a reading can go wrong or right: no network, a
/// timeout, DNS or TLS failure, a refused or expired credential, a missing permission, a throttled
/// or broken service, an unreadable answer, impossible percentages and cancellation. Each case is
/// its own test so a failure names the provider and the situation.
final class ProviderFetchMatrixTests: XCTestCase {
    enum Scenario: String, CaseIterable {
        case offline, timedOut, hostNotFound, tlsFailure, connectionLost, cannotConnect, badServerResponse
        case authenticationExpired, missingCredential, permissionDenied, rateLimited, providerUnavailable
        case parseFailure, networkFailure, apiFailure
        case secretInError, decodingFailure, noStrategy
        case success, notANumber, overLimit, negative, cancelled
    }

    private var providerID: CodexBarCore.UsageProvider?
    private var scenario: Scenario?

    override class var defaultTestSuite: XCTestSuite {
        let suite = XCTestSuite(name: "ProviderFetchMatrixTests")
        for descriptor in ExtendedProviderCatalog.additions {
            for scenario in Scenario.allCases {
                let test = ProviderFetchMatrixTests(selector: #selector(runCase))
                test.providerID = descriptor.id; test.scenario = scenario
                suite.addTest(test)
            }
        }
        return suite
    }

    override var name: String {
        "-[ProviderFetchMatrixTests \(providerID?.rawValue ?? "?")_\(scenario?.rawValue ?? "?")]"
    }

    private static let secret = "sk-live-SECRET-0123456789abcdef"

    private static func usage(_ percent: Double) -> ProviderFetchResult {
        ProviderFetchResult(usage: UsageSnapshot(primary: RateWindow(usedPercent: percent, windowMinutes: 300, resetsAt: Date().addingTimeInterval(3600), resetDescription: nil),
                                                 secondary: nil, updatedAt: Date()),
                            credits: nil, dashboard: nil, sourceLabel: "test", strategyID: "test.api", strategyKind: .apiToken)
    }

    private static func outcome(_ scenario: Scenario, provider: CodexBarCore.UsageProvider) throws -> ProviderFetchResult {
        func url(_ code: URLError.Code) -> Error { URLError(code) }
        func classified(_ kind: ProviderFetchClassifiedError.Kind) -> Error { ProviderFetchClassifiedError(kind: kind, message: "upstream says \(secret)") }
        switch scenario {
        case .offline: throw url(.notConnectedToInternet)
        case .timedOut: throw url(.timedOut)
        case .hostNotFound: throw url(.cannotFindHost)
        case .tlsFailure: throw url(.secureConnectionFailed)
        case .connectionLost: throw url(.networkConnectionLost)
        case .cannotConnect: throw url(.cannotConnectToHost)
        case .badServerResponse: throw url(.badServerResponse)
        case .authenticationExpired: throw classified(.authenticationExpired)
        case .missingCredential: throw classified(.missingCredential)
        case .permissionDenied: throw classified(.permissionDenied)
        case .rateLimited: throw classified(.rateLimited)
        case .providerUnavailable: throw classified(.providerUnavailable)
        case .parseFailure: throw classified(.parseFailure)
        case .networkFailure: throw classified(.networkFailure)
        case .apiFailure: throw classified(.apiFailure)
        case .secretInError: throw NSError(domain: "Upstream", code: 7, userInfo: [NSLocalizedDescriptionKey: "Bearer \(secret) rejected"])
        case .decodingFailure: throw DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: "token \(secret)"))
        case .noStrategy: throw ProviderFetchError.noAvailableStrategy(provider)
        case .success: return usage(40)
        case .notANumber: return usage(.nan)
        case .overLimit: return usage(150)
        case .negative: return usage(-20)
        case .cancelled: throw CancellationError()
        }
    }

    @objc func runCase() {
        guard let providerID, let scenario else { return XCTFail("matrix case was not configured") }
        let done = expectation(description: "fetch")
        Task { @MainActor in
            defer { done.fulfill() }
            do { try await Self.check(providerID, scenario) } catch { XCTFail("\(providerID.rawValue)/\(scenario): \(error)") }
        }
        wait(for: [done], timeout: 30)
    }

    @MainActor private static func check(_ providerID: CodexBarCore.UsageProvider, _ scenario: Scenario) async throws {
        let descriptor = try XCTUnwrap(ExtendedProviderCatalog.descriptor(for: ExtendedProviderCatalog.localID(providerID)))
        var configuration = ExtendedProviderConfiguration(providerID: descriptor.id)
        configuration.allowBillableRequests = true
        if descriptor.id == .fireworks { configuration.environment["FIREWORKS_ACCOUNT_SLUG"] = "account-test" }
        let provider = ExtendedNotchProvider(descriptor: descriptor, configuration: { configuration },
                                             fetch: { descriptor, _ in try Self.outcome(scenario, provider: descriptor.id) })
        let label = "\(providerID.rawValue)/\(scenario.rawValue)"
        let snapshot: ProviderSnapshot
        do { snapshot = try await provider.fetchSnapshot() }
        catch is CancellationError {
            XCTAssertEqual(scenario, .cancelled, "\(label) was cancelled unexpectedly"); return
        }
        XCTAssertNotEqual(scenario, .cancelled, "\(label) swallowed a cancellation and reported a reading")
        let message: String? = switch snapshot.status {
        case .error(let text), .unsupported(let text): text
        default: nil
        }

        // A secret from an upstream error or response never reaches the screen.
        if let message { XCTAssertFalse(message.contains(Self.secret), "\(label) displayed a secret: \(message)") }
        // Messages are sentences, not internal codes.
        if let message {
            XCTAssertNil(message.range(of: #"\b[a-z]+-[a-z]+(-[a-z]+)?\b\."#, options: .regularExpression), "\(label) shows a raw code: \(message)")
            XCTAssertNil(message.range(of: #"error -?\d{3,}"#, options: .regularExpression), "\(label) shows a bare error number: \(message)")
            XCTAssertFalse(message.trimmingCharacters(in: .whitespaces).isEmpty, "\(label) has an empty message")
        }
        // Mirrors ExtendedNotchProvider.snapshot: these services report balances or spend as text.
        let reportsTextOnly = descriptor.metadata.balanceOnly || [.azureopenai, .groq, .crof, .venice, .ibmbob].contains(descriptor.id)
        for window in snapshot.windows {
            if let fraction = window.usedFraction { XCTAssertTrue(fraction.isFinite && fraction >= 0, "\(label) shows an impossible fraction \(fraction)") }
        }

        switch scenario {
        case .offline, .timedOut, .hostNotFound, .tlsFailure, .connectionLost, .cannotConnect, .badServerResponse,
             .rateLimited, .providerUnavailable, .networkFailure:
            // A network or service problem must not send the user off to sign in again.
            XCTAssertNotEqual(snapshot.status, .needsAuth, "\(label) asked to sign in for a network/service failure")
            XCTAssertNotEqual(snapshot.status, .ok, "\(label) reported a reading after a failure")
            XCTAssertTrue(snapshot.windows.isEmpty, "\(label) kept a quota after a failure")
        case .authenticationExpired, .missingCredential, .noStrategy:
            XCTAssertEqual(snapshot.status, .needsAuth, "\(label) did not ask to reconnect")
        case .permissionDenied:
            guard case .unsupported = snapshot.status else { return XCTFail("\(label) did not explain the missing permission: \(snapshot.status)") }
        case .parseFailure, .apiFailure, .secretInError, .decodingFailure:
            XCTAssertNotEqual(snapshot.status, .ok, "\(label) reported a reading after a failure")
            XCTAssertTrue(snapshot.windows.isEmpty, "\(label) kept a quota after a failure")
        case .success where reportsTextOnly:
            // Balance and spend services show the words they report, never a synthesized gauge.
            XCTAssertEqual(snapshot.status, .ok, "\(label) did not show a valid reading")
            XCTAssertNil(snapshot.windows.first(where: { $0.usedFraction != nil }), "\(label) turned a balance into a gauge")
        case .overLimit where reportsTextOnly:
            XCTAssertNil(snapshot.windows.first(where: { $0.usedFraction != nil }), "\(label) turned a balance into a gauge")
        case .success:
            XCTAssertEqual(snapshot.status, .ok, "\(label) did not show a valid reading")
            XCTAssertEqual(snapshot.windows.first?.usedFraction ?? -1, 0.4, accuracy: 0.0001, label)
        case .notANumber, .negative:
            XCTAssertNil(snapshot.windows.first(where: { $0.usedFraction != nil }), "\(label) turned an invalid percentage into a gauge")
        case .overLimit:
            XCTAssertEqual(snapshot.windows.first?.usedFraction ?? -1, 1.5, accuracy: 0.0001, "\(label) hid an over-limit reading")
        case .cancelled:
            break
        }
    }
}

/// Adding each provider, with its real sign-in route, through every way the first sign-in can go.
final class ProviderAddFlowMatrixTests: XCTestCase {
    enum Flow: String, CaseIterable { case alreadySignedIn, signsInLater, cancelled, cannotOpen, toolMissing, neverSignsIn, superseded }

    private var providerID: String?
    private var flow: Flow?

    @MainActor static let providers: [any NotchProvider] = ([
        CopilotNotchProvider(), CursorNotchProvider(), GrokNotchProvider(), OpenCodeNotchProvider(), CommandCodeNotchProvider(),
        GLMNotchProvider(), OllamaNotchProvider(), AntigravityNotchProvider(), OllamaLocalProvider(),
    ] as [any NotchProvider]).map { HybridNotchProvider.wrapping($0) } + ExtendedProviderCatalog.makeProviders()

    override class var defaultTestSuite: XCTestSuite {
        let suite = XCTestSuite(name: "ProviderAddFlowMatrixTests")
        let ids = MainActor.assumeIsolated { providers.map(\.id) }
        for id in ids {
            for flow in Flow.allCases {
                let test = ProviderAddFlowMatrixTests(selector: #selector(runCase))
                test.providerID = id; test.flow = flow
                suite.addTest(test)
            }
            let route = ProviderAddFlowMatrixTests(selector: #selector(routeCase)); route.providerID = id
            suite.addTest(route)
        }
        return suite
    }

    override var name: String {
        "-[ProviderAddFlowMatrixTests \(providerID ?? "?")_\(flow?.rawValue ?? "route")]"
    }

    @MainActor private static func route(for id: String) -> SignInRoute? { providers.first { $0.id == id }?.signInRoute }

    /// Every route can actually be followed: a readable explanation, a plain command with somewhere
    /// to install it, an https page.
    @objc func routeCase() {
        guard let id = providerID else { return XCTFail("route case was not configured") }
        MainActor.assumeIsolated {
            guard let route = Self.route(for: id) else { return XCTFail("\(id) has no provider") }
            XCTAssertFalse(route.explanation.trimmingCharacters(in: .whitespaces).isEmpty, "\(id) has no explanation")
            XCTAssertLessThan(route.explanation.count, 600, "\(id) explanation is too long for the banner")
            if case .guided(let guided) = route {
                switch guided.action {
                case .terminal(let command):
                    XCTAssertTrue(command.allSatisfy { $0.isLetter || $0.isNumber || " -_./".contains($0) }, "\(id) command is not plain: \(command)")
                case .browser(let url):
                    XCTAssertEqual(url.scheme, "https", "\(id) opens a non-https page")
                default: break
                }
            }
        }
    }

    @objc func runCase() {
        guard let id = providerID, let flow else { return XCTFail("matrix case was not configured") }
        let done = expectation(description: "flow")
        Task { @MainActor in
            defer { done.fulfill() }
            await Self.check(id, flow)
        }
        wait(for: [done], timeout: 30)
    }

    @MainActor private static func check(_ id: String, _ flow: Flow) async {
        guard let realRoute = Self.route(for: id) else { return XCTFail("\(id) has no provider") }
        // In-app sign-ins need the network; the connector's handling of them is covered by the ladder.
        let route: SignInRoute = if case .guided(let guided) = realRoute, case .inApp = guided.action {
            .guided(.init(name: guided.name, action: .browser(URL(string: "https://example.invalid")!), note: guided.note))
        } else { realRoute }
        let fake = ProviderConnectionLadderTests.Fake(id, route: route)
        var launches = 0, installs = 0, checks = 0
        let signedInAfter = switch flow { case .alreadySignedIn: 0; case .signsInLater, .superseded: 3; default: Int.max }
        let connector = ProviderConnector(provider: { $0 == id ? fake : nil },
            connects: { _ in checks += 1; return checks > signedInAfter },
            launch: { _ in launches += 1; return flow != .cannotOpen },
            preflight: { route in
                guard flow == .toolMissing, case .guided(let guided) = route, case .terminal = guided.action else { return nil }
                return "\(guided.name) needs a command that is not installed on this Mac."
            },
            openInstallPage: { _ in installs += 1 },
            runInApp: { _, _ in .signedIn }, allowBrowserSession: { _ in },
            pollInterval: .milliseconds(5), patience: flow == .neverSignsIn ? 0.15 : 3)
        connector.begin(id)
        if flow == .cancelled { try? await Task.sleep(for: .milliseconds(30)); connector.cancel(id) }
        if flow == .superseded { try? await Task.sleep(for: .milliseconds(10)); connector.begin(id) }
        let end = Date().addingTimeInterval(5)
        func settled(_ state: ProviderConnector.State?) -> Bool {
            switch state { case .connected?, .failed?, .needsKey?: return true; case nil: return flow == .cancelled; default: return false }
        }
        while Date() < end, !settled(connector.states[id]) { try? await Task.sleep(for: .milliseconds(5)) }
        let state = connector.states[id]
        let label = "\(id)/\(flow.rawValue)"
        let opensSettings = if case .guided(let guided) = route { guided.opensSettings } else { false }
        let isGuidance = if case .guidance = route { true } else { false }
        let isTerminal = if case .guided(let guided) = route, case .terminal = guided.action { true } else { false }

        switch flow {
        case .alreadySignedIn:
            XCTAssertEqual(state, .connected, label); XCTAssertEqual(launches, 0, "\(label) opened a sign-in for an account already present")
        case .cancelled:
            XCTAssertNil(state, "\(label) kept a state after Cancel")
        case .signsInLater, .superseded:
            if opensSettings { guard case .needsKey = state else { return XCTFail("\(label) ended \(String(describing: state))") } }
            else if isGuidance { guard case .failed = state else { return XCTFail("\(label) ended \(String(describing: state))") } }
            else { XCTAssertEqual(state, .connected, label) }
            XCTAssertLessThanOrEqual(launches, flow == .superseded ? 2 : 1, "\(label) opened its sign-in \(launches) times")
        case .cannotOpen:
            if !opensSettings && !isGuidance, case .failed(let text)? = state { XCTAssertTrue(text.contains("could not be opened"), label) }
        case .toolMissing:
            if isTerminal {
                guard case .failed(let text)? = state else { return XCTFail("\(label) ended \(String(describing: state))") }
                XCTAssertTrue(text.contains("not installed"), label); XCTAssertEqual(launches, 0, "\(label) opened an empty Terminal")
                XCTAssertEqual(installs, 1, "\(label) did not open the install page")
            }
        case .neverSignsIn:
            if opensSettings { guard case .needsKey = state else { return XCTFail("\(label) ended \(String(describing: state))") } }
            else { guard case .failed = state else { return XCTFail("\(label) waited forever: \(String(describing: state))") } }
        }
        switch state {
        case .failed(let text)?, .needsKey(let text)?, .waiting(let text)?:
            XCTAssertFalse(text.trimmingCharacters(in: .whitespaces).isEmpty, "\(label) left no instruction")
        default: break
        }
    }
}
