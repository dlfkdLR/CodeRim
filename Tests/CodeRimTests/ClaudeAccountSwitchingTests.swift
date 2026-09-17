import XCTest
@testable import CodeRim

final class ClaudeLoginStoreTests: XCTestCase {
    func testSavedIdentityAndPlanUseAccountAndOrganization() throws {
        let account = try ClaudeAccountFixture.saved("one", plan: "max", tier: "default_claude_max_20x")
        XCTAssertEqual(account.planName, "Max 20x")
        XCTAssertNotEqual(account.id, try ClaudeAccountFixture.saved("two").id)
        XCTAssertThrowsError(try SavedClaudeAccount(oauthData: Data("{}".utf8), profileData: account.profileData))
    }

    func testSwitchPreservesUnrelatedCredentialsAndConfiguration() throws {
        let original = try ClaudeAccountFixture.snapshot("one")
        let credentials = MemoryClaudeCredentials(original.credentials)
        let profile = MemoryClaudeProfile(original.configuration)
        let store = ClaudeLoginStore(credentials: credentials, profile: profile)
        try store.replace(with: ClaudeAccountFixture.saved("two"), expecting: original)
        let result = try store.read()
        XCTAssertEqual(try result.account()?.email, "two@example.com")
        XCTAssertEqual(try object(result.credentials)["unrelated"] as? String, "keep-secret")
        XCTAssertEqual(try object(result.configuration)["theme"] as? String, "dark")
        XCTAssertEqual(try object(result.configuration)["numStartups"] as? Int, 42)
    }

    func testConcurrentLoginChangeRefusesBothWrites() throws {
        let old = try ClaudeAccountFixture.snapshot("one")
        let newer = try ClaudeAccountFixture.snapshot("changed")
        let credentials = MemoryClaudeCredentials(newer.credentials)
        let profile = MemoryClaudeProfile(old.configuration)
        let store = ClaudeLoginStore(credentials: credentials, profile: profile)
        XCTAssertThrowsError(try store.replace(with: ClaudeAccountFixture.saved("two"), expecting: old))
        XCTAssertEqual(credentials.writes, 0)
        XCTAssertEqual(profile.writes, 0)
    }

    func testProfileFailureRestoresDepartingCredential() throws {
        let original = try ClaudeAccountFixture.snapshot("one")
        let credentials = MemoryClaudeCredentials(original.credentials)
        let profile = MemoryClaudeProfile(original.configuration)
        profile.failure = .unsafeFile
        let store = ClaudeLoginStore(credentials: credentials, profile: profile)
        XCTAssertThrowsError(try store.replace(with: ClaudeAccountFixture.saved("two"), expecting: original))
        XCTAssertEqual(credentials.data, original.credentials)
        XCTAssertEqual(profile.data, original.configuration)
    }

    func testRollbackDoesNotOverwriteConcurrentVendorRefresh() throws {
        let original = try ClaudeAccountFixture.snapshot("one")
        let refreshed = try ClaudeAccountFixture.snapshot("newest").credentials
        let credentials = MemoryClaudeCredentials(original.credentials)
        let profile = MemoryClaudeProfile(original.configuration)
        profile.beforeWrite = { credentials.data = refreshed }
        profile.failure = .changedLogin
        let store = ClaudeLoginStore(credentials: credentials, profile: profile)
        XCTAssertThrowsError(try store.replace(with: ClaudeAccountFixture.saved("two"), expecting: original)) {
            XCTAssertEqual($0 as? ClaudeAccountError, .rollback)
        }
        XCTAssertEqual(credentials.data, refreshed)
    }

    func testIsolatedLoginRequiresExplicitSignedOutStatus() throws {
        for response in ["", "not supported", "{}", "{\"loggedIn\":true}", "{\"loggedIn\":0}", "{\"loggedIn\":\"false\"}"] {
            XCTAssertThrowsError(try LocalClaudeAccountRuntime.requireSignedOutProbe(Data(response.utf8)))
        }
        XCTAssertNoThrow(try LocalClaudeAccountRuntime.requireSignedOutProbe(Data("{\"loggedIn\":false}".utf8)))
    }

    func testTemporaryLoginHasDistinctKeychainService() {
        let primary = ClaudeLoginStore.keychain(configDirectory: nil)
        let isolated = ClaudeLoginStore.keychain(configDirectory: URL(fileURLWithPath: "/tmp/claude-test"))
        XCTAssertEqual(primary.service, "Claude Code-credentials")
        XCTAssertNotEqual(primary.service, isolated.service)
        XCTAssertEqual(isolated.service.count, primary.service.count + 9)
    }

    func testProfileAtomicWriteRejectsChangedFileAndSymlink() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent(".claude.json")
        let file = ClaudeProfileFile(url: url)
        let first = Data(#"{"theme":"dark"}"#.utf8)
        try file.replace(with: first, expecting: nil)
        XCTAssertEqual(try file.read(), first)
        XCTAssertThrowsError(try file.replace(with: Data("{}".utf8), expecting: nil))
        XCTAssertEqual(try file.read(), first)
        let link = directory.appendingPathComponent("linked.json")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: url)
        XCTAssertThrowsError(try ClaudeProfileFile(url: link).read())
        XCTAssertEqual(try file.read(), first)
    }

    private func object(_ data: Data?) throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(data)) as? [String: Any])
    }
}

@MainActor
final class ClaudeAccountStoreTests: XCTestCase {
    func testOpeningWindowDoesNotSaveOrSwitchLogin() throws {
        let fixture = try ClaudeAccountFixture()
        fixture.store.load()
        XCTAssertEqual(fixture.store.currentID, try fixture.login.value.account()?.id)
        XCTAssertEqual(fixture.vault.writes, 0)
        XCTAssertEqual(fixture.login.writes, 0)
    }

    func testFailedLiveIdentityRefreshClearsCurrentBadge() throws {
        let fixture = try ClaudeAccountFixture()
        fixture.store.load()
        XCTAssertNotNil(fixture.store.currentID)
        fixture.runtime.policyBlocked = true
        fixture.store.load()
        XCTAssertNil(fixture.store.currentID)
        XCTAssertTrue(fixture.store.isError)
        XCTAssertEqual(fixture.login.writes, 0)
    }

    func testSaveAndRemovePreserveLiveLogin() async throws {
        let fixture = try ClaudeAccountFixture()
        await fixture.store.saveCurrent()
        XCTAssertEqual(fixture.store.accounts.count, 1)
        fixture.store.remove(try XCTUnwrap(fixture.store.currentID))
        XCTAssertTrue(fixture.store.accounts.isEmpty)
        XCTAssertEqual(fixture.login.writes, 0)
    }

    func testSwitchSavesLatestDepartingTokenAndInvalidatesOnce() async throws {
        let fixture = try ClaudeAccountFixture()
        let destination = try ClaudeAccountFixture.saved("two")
        fixture.vault.accounts = [destination]
        var before = 0, after = 0
        fixture.store.onWillSwitch = { before += 1 }
        fixture.store.onDidSwitch = { after += 1 }
        await fixture.store.switchAccount(to: destination.id)
        XCTAssertEqual(fixture.store.currentID, destination.id)
        XCTAssertEqual(fixture.vault.accounts.count, 2)
        XCTAssertEqual(fixture.vault.accounts.first(where: { $0.email == "one@example.com" })?.oauthData,
                       try ClaudeAccountFixture.saved("one").oauthData)
        XCTAssertEqual(before, 1); XCTAssertEqual(after, 1)
        XCTAssertFalse(fixture.store.isBusy)
        XCTAssertFalse(fixture.store.isError)
    }

    func testRunningClaudeBlocksSwitchWithoutStoppingSessions() async throws {
        let fixture = try ClaudeAccountFixture()
        let destination = try ClaudeAccountFixture.saved("two")
        fixture.vault.accounts = [destination]
        fixture.runtime.running = true
        await fixture.store.switchAccount(to: destination.id)
        XCTAssertEqual(fixture.login.writes, 0)
        XCTAssertEqual(fixture.vault.writes, 0)
        XCTAssertTrue(fixture.store.isError)
    }

    func testAddKeepsCurrentLoginAndWaitsForExplicitSwitch() async throws {
        let fixture = try ClaudeAccountFixture()
        fixture.store.addAccount()
        for _ in 0..<100 where fixture.store.isBusy { await Task.yield() }
        XCTAssertFalse(fixture.store.isBusy)
        XCTAssertEqual(fixture.store.accounts.map(\.email), ["two@example.com"])
        XCTAssertEqual(fixture.login.writes, 0)
    }

    func testCancelledAddDoesNotSaveOrReplaceCurrentLogin() async throws {
        let fixture = try ClaudeAccountFixture()
        fixture.runtime.holdSignIn = true
        fixture.store.addAccount()
        fixture.store.cancelSignIn()
        for _ in 0..<100 where fixture.store.isBusy { await Task.yield() }
        XCTAssertFalse(fixture.store.isBusy)
        XCTAssertFalse(fixture.store.isError)
        XCTAssertEqual(fixture.vault.writes, 0)
        XCTAssertEqual(fixture.login.writes, 0)
    }

    func testManagedPolicyBlocksSaveAndSwitch() async throws {
        XCTAssertThrowsError(try LocalClaudeAccountRuntime.validatePolicy(["forceLoginOrgUUID": "org"]))
        XCTAssertThrowsError(try LocalClaudeAccountRuntime.validatePolicy(["apiKeyHelper": "command"]))
        XCTAssertNoThrow(try LocalClaudeAccountRuntime.validatePolicy(["theme": "dark"]))
        let fixture = try ClaudeAccountFixture()
        fixture.runtime.policyBlocked = true
        await fixture.store.saveCurrent()
        XCTAssertEqual(fixture.vault.writes, 0)
        XCTAssertEqual(fixture.login.writes, 0)
    }
}

@MainActor
final class ClaudeAccountFixture {
    let vault = MemoryClaudeVault()
    let login: MemoryClaudeLogin
    let runtime = TestClaudeAccountRuntime()
    let store: ClaudeAccountStore

    init() throws {
        login = MemoryClaudeLogin(try Self.snapshot("one"))
        store = ClaudeAccountStore(vault: vault, login: login, runtime: runtime, acquireLock: { nil })
    }

    nonisolated static func saved(_ identity: String, plan: String = "pro", tier: String = "default_claude_pro") throws -> SavedClaudeAccount {
        let oauth: [String: Any] = ["accessToken": "synthetic-access-\(identity)", "refreshToken": "synthetic-refresh-\(identity)",
            "expiresAt": 2_000_000_000_000, "scopes": ["user:inference", "user:profile"], "subscriptionType": plan, "rateLimitTier": tier]
        let profile = ["accountUuid": identity, "organizationUuid": "organization", "emailAddress": "\(identity)@example.com"]
        return try SavedClaudeAccount(oauthData: JSONSerialization.data(withJSONObject: oauth, options: .sortedKeys),
                                      profileData: JSONSerialization.data(withJSONObject: profile, options: .sortedKeys))
    }

    nonisolated static func snapshot(_ identity: String) throws -> ClaudeLoginSnapshot {
        let account = try saved(identity)
        return try ClaudeLoginSnapshot(
            credentials: JSONSerialization.data(withJSONObject: ["claudeAiOauth": JSONSerialization.jsonObject(with: account.oauthData), "unrelated": "keep-secret"], options: .sortedKeys),
            configuration: JSONSerialization.data(withJSONObject: ["oauthAccount": JSONSerialization.jsonObject(with: account.profileData), "theme": "dark", "numStartups": 42], options: .sortedKeys))
    }
}

final class MemoryClaudeCredentials: ClaudeCredentialStoring {
    var data: Data?
    var writes = 0
    init(_ data: Data?) { self.data = data }
    func read() throws -> Data? { data }
    func replace(_ data: Data?, expecting original: Data?) throws {
        guard self.data == original else { throw ClaudeAccountError.changedLogin }
        self.data = data; writes += 1
    }
}

final class MemoryClaudeProfile: ClaudeProfileStoring {
    var data: Data?
    var writes = 0
    var beforeWrite: () -> Void = {}
    var failure: ClaudeAccountError?
    init(_ data: Data?) { self.data = data }
    func read() throws -> Data? { data }
    func replace(with data: Data, expecting original: Data?) throws {
        beforeWrite()
        if let failure { throw failure }
        guard self.data == original else { throw ClaudeAccountError.changedLogin }
        self.data = data; writes += 1
    }
}

final class MemoryClaudeVault: ClaudeAccountVault {
    var accounts: [SavedClaudeAccount] = []
    var writes = 0
    func load() throws -> [SavedClaudeAccount] { accounts }
    func save(_ accounts: [SavedClaudeAccount]) throws {
        guard accounts.count <= 12 else { throw ClaudeAccountError.full }
        self.accounts = accounts; writes += 1
    }
}

final class MemoryClaudeLogin: ClaudeLoginStoring {
    var value: ClaudeLoginSnapshot
    var writes = 0
    init(_ value: ClaudeLoginSnapshot) { self.value = value }
    func read() throws -> ClaudeLoginSnapshot { value }
    func replace(with account: SavedClaudeAccount, expecting original: ClaudeLoginSnapshot) throws {
        guard original == value else { throw ClaudeAccountError.changedLogin }
        value = try ClaudeAccountFixture.snapshot(String(account.email.split(separator: "@")[0])); writes += 1
    }
}

@MainActor
final class TestClaudeAccountRuntime: ClaudeAccountRuntime {
    var holdSignIn = false
    var running = false
    var policyBlocked = false
    func checkPolicy() throws { if policyBlocked { throw ClaudeAccountError.policy } }
    func requireStopped() throws { if running { throw ClaudeAccountError.running } }
    func signIn() async throws -> SavedClaudeAccount {
        while holdSignIn { try await Task.sleep(for: .milliseconds(10)) }
        return try ClaudeAccountFixture.saved("two")
    }
}
