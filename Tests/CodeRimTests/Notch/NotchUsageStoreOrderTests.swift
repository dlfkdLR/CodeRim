import Combine
import XCTest
@testable import CodeRim

@MainActor
final class NotchUsageStoreOrderTests: XCTestCase {
    private var defaults: UserDefaults!
    private var suiteName: String!

    override func setUp() async throws {
        suiteName = "NotchUsageStoreOrderTests.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suiteName)!
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
    }

    func testRingRefreshPreservesDefaultOrderWithoutPublishingAMissingRing() async throws {
        let providers = [StubProvider("claude"), StubProvider("codex"), StubProvider("cursor")]
        let store = makeStore(providers)
        try await assertRefresh(store, providerID: "claude", expected: ["claude", "codex", "cursor"])
        XCTAssertEqual(providers.map(\.fetchCount), [1, 0, 0])
        XCTAssertEqual(store.snapshots.first?.windows.first?.usedFraction, 0.01)
    }

    func testRingRefreshPreservesPartialSavedOrderAndUnlistedProviders() async throws {
        let store = makeStore([StubProvider("claude"), StubProvider("codex"), StubProvider("cursor")],
                              order: ["missing-profile", "codex"])
        try await assertRefresh(store, providerID: "claude", expected: ["codex", "claude", "cursor"])
    }

    func testRingRefreshPreservesCompleteSavedOrderWithoutPublishingAMissingRing() async throws {
        let store = makeStore([StubProvider("claude"), StubProvider("codex"), StubProvider("cursor")],
                              order: ["cursor", "codex", "claude"])
        try await assertRefresh(store, providerID: "codex", expected: ["cursor", "codex", "claude"])
    }

    func testAutomaticRefreshKeepsOrderAfterEveryProviderResponse() async {
        let expected = ["claude", "codex", "cursor"]
        let providers = expected.map { StubProvider($0) }
        let store = makeStore(providers)
        var observed: [[String]] = []
        let subscription = store.$snapshots.sink { observed.append($0.map(\.id)) }
        await store.refresh()
        XCTAssertEqual(providers.map(\.fetchCount), [1, 1, 1])
        XCTAssertFalse(observed.isEmpty)
        XCTAssertTrue(observed.allSatisfy { $0 == expected }, "Published orders: \(observed)")
        XCTAssertEqual(store.providerSummaries.map(\.id), expected)
        withExtendedLifetime(subscription) {}
    }

    func testChangingAndClearingSavedOrderUsesRegistrationOrderForUnlistedProviders() {
        let store = makeStore([StubProvider("claude"), StubProvider("codex"), StubProvider("cursor")],
                              order: ["cursor", "codex", "claude"])
        store.order = ["codex"]
        XCTAssertEqual(store.snapshots.map(\.id), ["codex", "claude", "cursor"])
        XCTAssertEqual(store.snapshots.map(\.id), store.providerSummaries.map(\.id))
        store.order = []
        XCTAssertEqual(store.snapshots.map(\.id), ["claude", "codex", "cursor"])
        XCTAssertEqual(store.snapshots.map(\.id), store.providerSummaries.map(\.id))
    }

    func testReturningHiddenProviderRegainsItsConfiguredPosition() async throws {
        let local = StubProvider("local", visibleWhenAbsent: false)
        let store = makeStore([StubProvider("claude"), local, StubProvider("codex")])
        XCTAssertEqual(store.snapshots.map(\.id), ["claude", "codex"])
        store.refresh(providerID: "local")
        try await waitForRefresh(store, providerID: "local")
        XCTAssertEqual(store.snapshots.map(\.id), ["claude", "local", "codex"])
        local.failure = .badResponse(status: 503)
        store.refresh(providerID: "local")
        try await waitForRefresh(store, providerID: "local")
        XCTAssertEqual(store.snapshots.map(\.id), ["claude", "codex"])
        local.failure = nil
        store.refresh(providerID: "local")
        try await waitForRefresh(store, providerID: "local")
        XCTAssertEqual(store.snapshots.map(\.id), ["claude", "local", "codex"])
    }

    func testFailedRingRefreshPreservesOrder() async throws {
        let claude = StubProvider("claude")
        claude.failure = .badResponse(status: 503)
        let store = makeStore([claude, StubProvider("codex"), StubProvider("cursor")])
        try await assertRefresh(store, providerID: "claude", expected: ["claude", "codex", "cursor"])
    }

    private func makeStore(_ providers: [StubProvider], order: [String] = []) -> NotchUsageStore {
        NotchUsageStore(providers: providers, archive: UsageArchive(defaults: defaults), order: order)
    }

    private func assertRefresh(_ store: NotchUsageStore, providerID: String, expected: [String],
                               file: StaticString = #filePath, line: UInt = #line) async throws {
        XCTAssertEqual(store.snapshots.map(\.id), expected, file: file, line: line)
        var observed: [[String]] = []
        let subscription = store.$snapshots.sink { observed.append($0.map(\.id)) }
        store.refresh(providerID: providerID)
        try await waitForRefresh(store, providerID: providerID)
        XCTAssertEqual(store.snapshots.map(\.id), expected, file: file, line: line)
        XCTAssertTrue(observed.allSatisfy { $0 == expected }, "Published orders: \(observed)", file: file, line: line)
        XCTAssertEqual(store.providerSummaries.map(\.id), expected, file: file, line: line)
        withExtendedLifetime(subscription) {}
    }

    private func waitForRefresh(_ store: NotchUsageStore, providerID: String) async throws {
        let deadline = Date().addingTimeInterval(3)
        while store.refreshing.contains(providerID), Date() < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertFalse(store.refreshing.contains(providerID), "Refresh did not finish")
    }

    private final class StubProvider: NotchProvider {
        let id: String
        var displayName: String { id }
        var glyph: ProviderGlyph { .openai }
        let isVisibleWhenAbsent: Bool
        var fetchCount = 0
        var failure: NotchProviderError?

        init(_ id: String, visibleWhenAbsent: Bool = true) {
            self.id = id
            isVisibleWhenAbsent = visibleWhenAbsent
        }

        func fetchSnapshot() async throws -> ProviderSnapshot {
            fetchCount += 1
            if let failure { throw failure }
            return ProviderSnapshot(id: id, displayName: displayName, glyph: glyph,
                                    fidelity: .official, status: .ok,
                                    windows: [LimitWindow(id: "weekly", label: "Weekly",
                                                          usedFraction: Double(fetchCount) / 100)])
        }

        func account() -> ProviderAccount? { nil }
        var signInRoute: SignInRoute { .guidance("") }
        func signOut() async {}
        func presentSignIn() {}
        func forgetCachedCredential() {}
    }
}
