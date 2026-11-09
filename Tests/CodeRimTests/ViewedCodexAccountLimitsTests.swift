import XCTest
@testable import CodeRim

/// Looking at another saved account must never cost that account its login:
/// the probe may renew the refresh token, and the renewed one is then the only
/// copy that works.
@MainActor
final class ViewedCodexAccountLimitsTests: XCTestCase {
    private var directory: URL!
    private var lockAvailable = true
    private var onLockAcquired: (() throws -> Void)?
    private var reads: [Data] = []

    override func setUp() async throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("viewed-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        lockAvailable = true
        onLockAcquired = nil
        reads = []
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: directory)
    }

    private func login(workspace: String, revision: String) throws -> Data {
        let claims: [String: Any] = ["sub": "subject-\(workspace)", "email": "\(workspace)@example.test",
                                     "https://api.openai.com/auth": ["chatgpt_account_id": workspace]]
        let payload = try JSONSerialization.data(withJSONObject: claims, options: [.sortedKeys])
            .base64EncodedString().replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        return try JSONSerialization.data(withJSONObject: [
            "auth_mode": "chatgpt", "OPENAI_API_KEY": NSNull(),
            "tokens": ["access_token": "access-\(revision)", "refresh_token": "refresh-\(revision)",
                       "id_token": "e30.\(payload).signature", "account_id": workspace]
        ], options: [.sortedKeys])
    }

    private struct Fixture {
        let store: CodexAccountStore
        let vault: AccountLayoutVault
        let signedIn: SavedCodexAccount
        let other: SavedCodexAccount
    }

    private func fixture() throws -> Fixture {
        let signedIn = try SavedCodexAccount(loginData: login(workspace: "a", revision: "1"))
        let other = try SavedCodexAccount(loginData: login(workspace: "b", revision: "1"))
        let vault = AccountLayoutVault(accounts: [signedIn, other])
        let store = CodexAccountStore(
            vault: vault, login: AccountLayoutLogin(data: signedIn.loginData), runtime: AccountLayoutRuntime(),
            acquireLock: { [unowned self] in
                guard self.lockAvailable else { return nil }
                let lease = try CodexAccountOperationLock.acquire(directory: self.directory)
                try self.onLockAcquired?()
                self.onLockAcquired = nil
                return lease
            })
        store.load()
        return Fixture(store: store, vault: vault, signedIn: signedIn, other: other)
    }

    private func snapshot() -> AccountLimitsSnapshot {
        AccountLimitsSnapshot(windows: [], resetCredits: nil, fetchedAt: Date())
    }

    func testARenewedLoginReplacesTheSavedOneAndTheSignedInAccountIsUntouched() async throws {
        let f = try fixture()
        let renewed = try login(workspace: "b", revision: "2")
        let viewed = ViewedCodexAccountLimits { [unowned self] data in
            self.reads.append(data)
            return (self.snapshot(), renewed)
        }
        await viewed.select(f.other.id, accounts: f.store)
        XCTAssertEqual(viewed.status, .ready)
        XCTAssertEqual(reads, [f.other.loginData])
        XCTAssertEqual(try f.vault.load().first { $0.id == f.other.id }?.loginData, renewed)
        XCTAssertEqual(try f.vault.load().first { $0.id == f.signedIn.id }?.loginData, f.signedIn.loginData)
    }

    func testARenewedLoginThatCannotBeSavedYetIsKeptAndUsedUntilItCan() async throws {
        let f = try fixture()
        let renewed = try login(workspace: "b", revision: "2")
        let viewed = ViewedCodexAccountLimits { [unowned self] data in
            self.reads.append(data)
            return (self.snapshot(), self.reads.count == 1 ? renewed : nil)
        }
        lockAvailable = false
        await viewed.select(f.other.id, accounts: f.store)
        XCTAssertEqual(try f.vault.load().first { $0.id == f.other.id }?.loginData, f.other.loginData,
                       "nothing could be saved while another account operation held the lock")

        // The saved copy is stale now, so the next read uses the renewed one, and saves it.
        lockAvailable = true
        await viewed.refresh(accounts: f.store)
        XCTAssertEqual(reads, [f.other.loginData, renewed])
        XCTAssertEqual(try f.vault.load().first { $0.id == f.other.id }?.loginData, renewed)
    }

    func testAReadThatFailsAfterTheLoginWasRenewedStillKeepsTheRenewedLogin() async throws {
        let f = try fixture()
        let renewed = try login(workspace: "b", revision: "2")
        let viewed = ViewedCodexAccountLimits { _ in
            throw SavedAccountReadError(underlying: AccountLimitError.timedOut, rotatedLogin: renewed)
        }
        await viewed.select(f.other.id, accounts: f.store)
        XCTAssertEqual(viewed.status, .unavailable)
        XCTAssertEqual(try f.vault.load().first { $0.id == f.other.id }?.loginData, renewed)
    }

    func testASavedCopyReplacedByASignInWinsOverAWaitingRenewedLogin() async throws {
        let f = try fixture()
        let renewed = try login(workspace: "b", revision: "2")
        let signedAgain = try SavedCodexAccount(loginData: login(workspace: "b", revision: "3"))
        let viewed = ViewedCodexAccountLimits { [unowned self] data in
            self.reads.append(data)
            return (self.snapshot(), self.reads.count == 1 ? renewed : nil)
        }
        lockAvailable = false
        await viewed.select(f.other.id, accounts: f.store)

        try f.vault.save([f.signedIn, signedAgain])
        lockAvailable = true
        await viewed.refresh(accounts: f.store)
        XCTAssertEqual(reads.last, signedAgain.loginData)
        XCTAssertEqual(try f.vault.load().first { $0.id == f.other.id }?.loginData, signedAgain.loginData)
    }

    func testChoosingTheSignedInAccountFollowsTheSharedLimitsAgain() async throws {
        let f = try fixture()
        let viewed = ViewedCodexAccountLimits { [unowned self] _ in (self.snapshot(), nil) }
        await viewed.select(f.other.id, accounts: f.store)
        XCTAssertEqual(viewed.accountID, f.other.id)
        await viewed.select(f.signedIn.id, accounts: f.store)
        XCTAssertNil(viewed.accountID)
        XCTAssertNil(viewed.snapshot)
    }

    func testWaitingRotationChecksItsBaseAfterAcquiringTheLock() async throws {
        let f = try fixture()
        let renewed = try login(workspace: "b", revision: "2")
        let signedAgain = try SavedCodexAccount(loginData: login(workspace: "b", revision: "3"))
        let viewed = ViewedCodexAccountLimits { [unowned self] data in
            self.reads.append(data)
            return (self.snapshot(), self.reads.count == 1 ? renewed : nil)
        }
        lockAvailable = false
        await viewed.select(f.other.id, accounts: f.store)
        lockAvailable = true
        onLockAcquired = { try f.vault.save([f.signedIn, signedAgain]) }
        await viewed.refresh(accounts: f.store)
        XCTAssertEqual(try f.store.savedAccount(f.other.id)?.loginData, signedAgain.loginData)
        XCTAssertEqual(reads.last, signedAgain.loginData)
    }

    func testRemovalDuringASuspendedReadWinsOnSuccessAndRotatedFailure() async throws {
        for fails in [false, true] {
            let f = try fixture()
            let renewed = try login(workspace: "b", revision: "2")
            let probe = PausedSavedAccountProbe()
            let viewed = ViewedCodexAccountLimits { data in try await probe.read(data) }
            let read = Task { await viewed.select(f.other.id, accounts: f.store) }
            await probe.waitUntilRequested()
            f.store.remove(f.other.id)
            XCTAssertNil(try f.store.savedAccount(f.other.id))
            probe.finish(snapshot: snapshot(), renewed: renewed, fails: fails)
            await read.value
            XCTAssertNil(try f.store.savedAccount(f.other.id), "a stale probe must not recreate a removed account")
            await viewed.refresh(accounts: f.store)
            XCTAssertEqual(probe.reads, [f.other.loginData], "a removed login must not be retried")
        }
    }

    func testNewSignInDuringASuspendedReadWinsOnSuccessAndRotatedFailure() async throws {
        for fails in [false, true] {
            let f = try fixture()
            let renewed = try login(workspace: "b", revision: "2")
            let signedAgain = try SavedCodexAccount(loginData: login(workspace: "b", revision: "3"))
            let probe = PausedSavedAccountProbe()
            let viewed = ViewedCodexAccountLimits { data in try await probe.read(data) }
            let read = Task { await viewed.select(f.other.id, accounts: f.store) }
            await probe.waitUntilRequested()
            try f.vault.save([f.signedIn, signedAgain])
            probe.finish(snapshot: snapshot(), renewed: renewed, fails: fails)
            await read.value
            XCTAssertEqual(try f.store.savedAccount(f.other.id)?.loginData, signedAgain.loginData)
            probe.nextSnapshot = snapshot()
            await viewed.refresh(accounts: f.store)
            XCTAssertEqual(probe.reads.last, signedAgain.loginData, "the rejected rotation must not become a retry credential")
        }
    }
}

@MainActor
private final class PausedSavedAccountProbe {
    private var continuation: CheckedContinuation<(snapshot: AccountLimitsSnapshot, rotatedLogin: Data?), Error>?
    private(set) var reads: [Data] = []
    var nextSnapshot: AccountLimitsSnapshot?

    func read(_ data: Data) async throws -> (snapshot: AccountLimitsSnapshot, rotatedLogin: Data?) {
        reads.append(data)
        if let nextSnapshot { return (nextSnapshot, nil) }
        return try await withCheckedThrowingContinuation { continuation = $0 }
    }

    func waitUntilRequested() async {
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while continuation == nil, ContinuousClock.now < deadline { await Task.yield() }
        XCTAssertNotNil(continuation)
    }

    func finish(snapshot: AccountLimitsSnapshot, renewed: Data, fails: Bool) {
        if fails { continuation?.resume(throwing: SavedAccountReadError(underlying: AccountLimitError.timedOut, rotatedLogin: renewed)) }
        else { continuation?.resume(returning: (snapshot, renewed)) }
        continuation = nil
    }
}
