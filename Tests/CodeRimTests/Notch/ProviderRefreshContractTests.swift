import Foundation
import XCTest
@testable import CodeRim

@MainActor
final class ProviderRefreshContractTests: XCTestCase {
    func testSharedAuthenticationAndRefreshContract() async throws {
        struct Contract: Decodable {
            let name: String; let provider: String; let failure: String
            let hiddenWhenAbsent: Bool; let retainsReading: Bool
        }
        let path = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("Fixtures/provider-refresh-v1.json")
        let contracts = try JSONDecoder().decode([Contract].self, from: Data(contentsOf: path))
        XCTAssertFalse(contracts.isEmpty)
        for contract in contracts {
            let suite = "ProviderRefreshContractTests.\(UUID())"
            let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
            defer { defaults.removePersistentDomain(forName: suite) }
            let provider = Stub(contract.provider, hidden: contract.hiddenWhenAbsent)
            let store = NotchUsageStore(providers: [provider], staleAfter: 0, archive: UsageArchive(defaults: defaults))
            await store.refresh()
            XCTAssertEqual(store.snapshots.first?.windows.first?.usedFraction, 0.42, contract.name)
            switch contract.failure {
            case "needsAuth": provider.failure = .needsAuth
            case "unsupported": provider.failure = .nothingMetered("unsupported")
            case "error", "unavailable": provider.failure = .badResponse(status: 503)
            default: return XCTFail("Unknown contract failure")
            }
            await store.refresh()
            XCTAssertEqual(store.snapshots.first?.windows.isEmpty == false, contract.retainsReading, contract.name)
            if contract.retainsReading {
                XCTAssertTrue(store.snapshots.first?.status.isStale == true, contract.name)
                XCTAssertEqual(store.snapshots.first?.windows.first?.usedFraction, 0.42, contract.name)
            }
        }
    }

    private final class Stub: NotchProvider {
        let id: String
        let isVisibleWhenAbsent: Bool
        var failure: NotchProviderError?
        init(_ id: String, hidden: Bool) { self.id = id; isVisibleWhenAbsent = !hidden }
        var displayName: String { id }
        var glyph: ProviderGlyph { .openai }
        func fetchSnapshot() async throws -> ProviderSnapshot {
            if let failure { throw failure }
            return ProviderSnapshot(id: id, displayName: id, glyph: glyph, fidelity: .official, status: .ok,
                windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.42)])
        }
        func account() -> ProviderAccount? { nil }
        var signInRoute: SignInRoute { .guidance("") }
        func signOut() async {}
        func presentSignIn() {}
        func forgetCachedCredential() {}
    }
}
