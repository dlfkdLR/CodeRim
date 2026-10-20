import CodexBarCore
import XCTest
@testable import CodeRim

@MainActor
final class HybridNotchProviderTests: XCTestCase {
    private final class Native: NotchProvider {
        var result: Result<ProviderSnapshot, Error>
        init(_ result: Result<ProviderSnapshot, Error>) { self.result = result }
        var id: String { "copilot" }
        var displayName: String { "GitHub Copilot" }
        var glyph: ProviderGlyph { .copilot }
        func fetchSnapshot() async throws -> ProviderSnapshot { try result.get() }
        func account() -> ProviderAccount? { ProviderAccount(label: "native", plan: nil, source: "gh", manageURL: nil) }
        var signInRoute: SignInRoute { .guidance("native") }
        func signOut() async {}
        func presentSignIn() {}
        func forgetCachedCredential() {}
    }

    private func snapshot(_ status: ProviderStatus) -> ProviderSnapshot {
        ProviderSnapshot(id: "copilot", displayName: "GitHub Copilot", glyph: .copilot, fidelity: .official, status: status,
                         windows: [LimitWindow(id: "w", label: "Chat", displayValue: "1")])
    }

    private func upstream(_ result: Result<ProviderFetchResult, Error>) throws -> ExtendedNotchProvider {
        let descriptor = try XCTUnwrap(ExtendedProviderCatalog.descriptor(for: "copilot"))
        return ExtendedNotchProvider(descriptor: descriptor, configuration: { .init(providerID: descriptor.id) },
                                     fetch: { _, _ in try result.get() })
    }

    func testTheNativeReadingWinsWhenThereIsOne() async throws {
        let hybrid = HybridNotchProvider(native: Native(.success(snapshot(.ok))),
                                         upstream: try upstream(.failure(ProviderFetchError.noAvailableStrategy(.copilot))))
        let result = try await hybrid.fetchSnapshot()
        XCTAssertEqual(result.status, .ok)
        XCTAssertEqual(hybrid.account()?.label, "native")
    }

    func testWithoutABorrowedSignInItStaysSignedOutWhenCodeRimHasNoneEither() async throws {
        let hybrid = HybridNotchProvider(native: Native(.failure(NotchProviderError.needsAuth)),
                                         upstream: try upstream(.failure(ProviderFetchError.noAvailableStrategy(.copilot))))
        do { _ = try await hybrid.fetchSnapshot(); XCTFail("expected needsAuth") }
        catch NotchProviderError.needsAuth {}
    }

    func testRoutesSignInWithoutCommandLineTools() {
        guard case .guided(let copilot) = HybridNotchProvider.route(for: "copilot", native: .guidance("x")) else { return XCTFail() }
        XCTAssertEqual(copilot.action, .inApp(.githubDevice))
        guard case .guided(let commandCode) = HybridNotchProvider.route(for: "commandcode", native: .guidance("x")) else { return XCTFail() }
        XCTAssertTrue(commandCode.importsBrowserSession)
        guard case .guided(let glm) = HybridNotchProvider.route(for: "glm", native: .guidance("x")) else { return XCTFail() }
        XCTAssertTrue(glm.opensSettings)
        XCTAssertEqual(HybridNotchProvider.route(for: "cursor", native: .openApp(bundleID: "a", name: "Cursor")),
                       .openApp(bundleID: "a", name: "Cursor"))
    }
}
