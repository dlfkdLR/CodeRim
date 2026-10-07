import XCTest
@testable import CodeRim

/// A successful answer is only the current account's answer if the account did not change while it was
/// in flight; remembered readings and rate-limit waits belong to the account that produced them.
@MainActor
final class NotchAccountBoundaryTests: XCTestCase {
    private var defaults: UserDefaults!
    private var suiteName: String!

    override func setUp() async throws {
        suiteName = "NotchAccountBoundaryTests.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suiteName)!
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
    }

    func testAnAnswerForTheAccountSignedOutDuringTheRequestIsReadAgain() async throws {
        let provider = SwitchingProvider()
        // Account A is signed in when the read starts; another tool signs in B while it is in flight.
        provider.onFetch = { count in if count == 1 { provider.identity = "b" } }
        let store = NotchUsageStore(providers: [provider], archive: UsageArchive(defaults: defaults))
        await store.refresh()
        XCTAssertEqual(provider.fetchCount, 2, "the answer for A was not kept; B was read")
        let snapshot = try XCTUnwrap(store.snapshots.first)
        XCTAssertEqual(snapshot.accountIdentity, "b")
        XCTAssertEqual(snapshot.windows.first?.usedFraction, provider.fraction(for: "b"))
    }

    /// The app wraps Grok, GLM, OpenCode, Command Code and the others in `HybridNotchProvider`;
    /// the check has to work through that wrapper, not only on a provider handed to the store directly.
    func testTheAppsHybridWrapperCarriesTheAccount() async throws {
        let native = SwitchingProvider(id: "grok")
        native.onFetch = { count in if count == 1 { native.identity = "b" } }
        let wrapped = HybridNotchProvider.wrapping(native)
        XCTAssertTrue(wrapped is HybridNotchProvider)
        XCTAssertEqual(wrapped.accountIdentity(), "a")
        let store = NotchUsageStore(providers: [wrapped], archive: UsageArchive(defaults: defaults))
        await store.refresh()
        XCTAssertEqual(native.fetchCount, 2)
        XCTAssertEqual(store.snapshots.first?.accountIdentity, "b")
        XCTAssertEqual(store.snapshots.first?.windows.first?.usedFraction, native.fraction(for: "b"))
    }

    func testBuiltInProvidersNameTheirAccount() {
        XCTAssertNotNil(GitHubCopilotCredentials.identity(environment: [:], hosts: "github.com:\n  user: octo\n  oauth_token: gho_x\n"))
        XCTAssertEqual(GitHubCopilotCredentials.identity(environment: [:], hosts: "github.com:\n  user: Octo\n"),
                       GitHubCopilotCredentials.identity(environment: [:], hosts: "github.com:\n  user: octo\n"))
        XCTAssertNil(GitHubCopilotCredentials.identity(environment: [:], hosts: nil))
    }

    func testARememberedReadingForAnotherAccountIsNotRestored() async throws {
        let provider = SwitchingProvider()
        let archive = UsageArchive(defaults: defaults)
        do {
            let store = NotchUsageStore(providers: [provider], archive: archive)
            await store.refresh()
            XCTAssertNotNil(archive.loadOwned()["switching"]?.accountIdentity)
        }
        provider.identity = "b"   // signed in elsewhere while CodeRim was not running
        let relaunched = NotchUsageStore(providers: [provider], archive: archive)
        XCTAssertNil(relaunched.snapshots.first?.windows.first?.usedFraction,
                     "A's numbers must not appear, not even dimmed, for B")
        provider.identity = "a"
        let sameAccount = NotchUsageStore(providers: [provider], archive: UsageArchive(defaults: defaults))
        XCTAssertNil(sameAccount.snapshots.first?.windows.first, "B's relaunch already discarded A's reading")
    }

    func testRateLimitWaitsBelongToTheAccountThatEarnedThem() {
        let archive = UsageArchive(defaults: defaults)
        var backoff = AccountBackoff(providerID: "glm", archive: archive)
        XCTAssertNil(backoff.remainingWait(for: "a"))
        backoff.recordLimit(retryAfter: 900, account: "a")
        XCTAssertEqual(backoff.consecutiveLimits, 1)
        XCTAssertGreaterThan(backoff.remainingWait(for: "a") ?? 0, 800)
        XCTAssertNil(backoff.remainingWait(for: "b"), "B is read straight away after a switch")
        XCTAssertEqual(backoff.consecutiveLimits, 0, "the doubling starts over for B")
        // Survives a relaunch, per account.
        var relaunched = AccountBackoff(providerID: "glm", archive: archive)
        XCTAssertNotNil(relaunched.remainingWait(for: "a"))
        relaunched.recordSuccess(account: "a")
        XCTAssertNil(relaunched.remainingWait(for: "a"))
    }

    func testAWaitStoredBeforeWaitsWereScopedIsDropped() {
        let archive = UsageArchive(defaults: defaults)
        archive.saveBackoffUntil(Date().addingTimeInterval(900), providerID: "opencode")
        archive.saveBackoffUntil(nil, providerID: "opencode", account: "a")
        XCTAssertNil(archive.loadBackoffUntil(providerID: "opencode"))
    }

    func testIdentitiesAreStableAndDoNotContainTheSecret() {
        let one = AccountIdentity.fingerprint("glm", "https://api.z.ai", "secret-key")
        XCTAssertEqual(one, AccountIdentity.fingerprint("glm", "https://api.z.ai", "secret-key"))
        XCTAssertNotEqual(one, AccountIdentity.fingerprint("glm", "https://open.bigmodel.cn", "secret-key"))
        XCTAssertFalse(one.contains("secret"))
        XCTAssertEqual(one.count, 24)
    }

    private final class SwitchingProvider: NotchProvider {
        let id: String
        init(id: String = "switching") { self.id = id }
        var displayName: String { "Switching" }
        var glyph: ProviderGlyph { .openai }
        var identity = "a"
        var fetchCount = 0
        var onFetch: (Int) -> Void = { _ in }

        func fraction(for account: String) -> Double { account == "a" ? 0.9 : 0.2 }

        func fetchSnapshot() async throws -> ProviderSnapshot {
            let account = identity
            fetchCount += 1
            onFetch(fetchCount)
            await Task.yield()
            return ProviderSnapshot(id: id, displayName: displayName, glyph: glyph, fidelity: .official, status: .ok,
                                    windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: fraction(for: account))])
        }

        func account() -> ProviderAccount? { ProviderAccount(label: identity, plan: nil, source: "test", manageURL: nil) }
        func accountIdentity() -> String? { identity }
        var signInRoute: SignInRoute { .guidance("") }
        func signOut() async {}
        func presentSignIn() {}
        func forgetCachedCredential() {}
    }
}
