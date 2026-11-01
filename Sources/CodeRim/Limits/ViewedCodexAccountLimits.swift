import Foundation

/// A read that failed after the probe had already renewed the login. The
/// renewed one is the only copy that still works, so it travels with the error.
struct SavedAccountReadError: Error {
    let underlying: Error
    let rotatedLogin: Data
}

/// Reads one saved account's limits in a private Codex home, so the Limits view
/// can look at an account without making it the signed-in one.
struct SavedAccountLimitReader: Sendable {
    /// `rotatedLogin` is set when the probe renewed the login and the saved copy
    /// must be replaced to stay valid.
    func read(loginData: Data) async throws -> (snapshot: AccountLimitsSnapshot, rotatedLogin: Data?) {
        let home = FileManager.default.temporaryDirectory
            .appendingPathComponent("coderim-limits-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        defer { try? FileManager.default.removeItem(at: home) }
        let authFile = home.appendingPathComponent("auth.json")
        guard FileManager.default.createFile(atPath: authFile.path, contents: loginData,
                                             attributes: [.posixPermissions: 0o600]) else {
            throw AccountLimitError.processLaunchFailed
        }
        let provider = AppServerLimitProvider(
            runner: AppServerProcessRunner(workingDirectory: home, codexHome: home),
            arguments: ["-c", "cli_auth_credentials_store=\"file\"", "app-server"]
        )
        let snapshot: AccountLimitsSnapshot
        do {
            snapshot = try await provider.readLimits()
        } catch {
            if let after = try? Data(contentsOf: authFile), after != loginData {
                throw SavedAccountReadError(underlying: error, rotatedLogin: after)
            }
            throw error
        }
        let after = try? Data(contentsOf: authFile)
        return (snapshot, after != loginData ? after : nil)
    }
}

/// Which saved Codex account the Limits view shows. `nil` follows the signed-in
/// account (the shared store); choosing another only changes what is displayed.
@MainActor
final class ViewedCodexAccountLimits: ObservableObject {
    @Published private(set) var accountID: String?
    @Published private(set) var snapshot: AccountLimitsSnapshot?
    @Published private(set) var status: AccountLimitStatus = .loading
    @Published private(set) var statusMessage = "Reading account limits…"
    @Published private(set) var isRefreshing = false

    private var generation = 0
    private let reader: (Data) async throws -> (snapshot: AccountLimitsSnapshot, rotatedLogin: Data?)
    /// A login the probe renewed that could not be saved yet, with the saved
    /// copy it replaces. Until it is saved the old copy may no longer work, so
    /// reads use this one, and it is saved at the next chance.
    private var unsaved: [String: (base: Data, renewed: Data)] = [:]

    init(reader: @escaping (Data) async throws -> (snapshot: AccountLimitsSnapshot, rotatedLogin: Data?)
            = { try await SavedAccountLimitReader().read(loginData: $0) }) {
        self.reader = reader
    }

    /// Saves any renewed login that is still waiting, unless the saved copy has
    /// changed since (the account was signed in again), when the new one wins.
    private func saveWaiting(accounts: CodexAccountStore) {
        for (id, pending) in unsaved {
            guard let saved = try? accounts.savedAccount(id), saved.loginData == pending.base else {
                unsaved[id] = nil
                continue
            }
            if accounts.storeRotatedLogin(pending.renewed, for: id) { unsaved[id] = nil }
        }
    }
    func select(_ id: String?, accounts: CodexAccountStore) async {
        let target = id == accounts.currentID ? nil : id
        guard target != accountID else { return }
        generation += 1
        accountID = target
        snapshot = nil
        status = .loading
        statusMessage = "Reading account limits…"
        isRefreshing = false
        await refresh(accounts: accounts)
    }

    func refresh(accounts: CodexAccountStore) async {
        guard let id = accountID else { return }
        let operation = generation
        isRefreshing = true
        defer { if generation == operation { isRefreshing = false } }
        var base: Data?
        // A renewed login is kept whichever account is on screen by now:
        // dropping it would leave a saved copy that no longer works.
        func keep(_ rotated: Data) {
            guard let base else { return }
            if accounts.storeRotatedLogin(rotated, for: id) { unsaved[id] = nil }
            else { unsaved[id] = (base, rotated) }
        }
        do {
            saveWaiting(accounts: accounts)
            guard let saved = try accounts.savedAccount(id) else { throw AccountSwitchError.invalidLogin }
            base = unsaved[id]?.base ?? saved.loginData
            let result = try await reader(unsaved[id]?.renewed ?? saved.loginData)
            if let rotated = result.rotatedLogin { keep(rotated) }
            guard generation == operation else { return }
            snapshot = result.snapshot
            status = .ready
            statusMessage = LimitFreshness.text(fetchedAt: result.snapshot.fetchedAt, now: Date())
        } catch {
            if let failed = error as? SavedAccountReadError { keep(failed.rotatedLogin) }
            guard generation == operation else { return }
            status = snapshot == nil ? .unavailable : .stale
            statusMessage = snapshot == nil ? "Account limits unavailable" : "Showing last known limits"
        }
    }
}
