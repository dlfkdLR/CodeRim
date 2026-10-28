import Foundation

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
        let snapshot = try await provider.readLimits()
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
    private let reader = SavedAccountLimitReader()

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
        do {
            guard let saved = try accounts.savedAccount(id) else { throw AccountSwitchError.invalidLogin }
            let result = try await reader.read(loginData: saved.loginData)
            guard generation == operation else { return }
            if let rotated = result.rotatedLogin { accounts.storeRotatedLogin(rotated, for: id) }
            snapshot = result.snapshot
            status = .ready
            statusMessage = LimitFreshness.text(fetchedAt: result.snapshot.fetchedAt, now: Date())
        } catch {
            guard generation == operation else { return }
            status = snapshot == nil ? .unavailable : .stale
            statusMessage = snapshot == nil ? "Account limits unavailable" : "Showing last known limits"
        }
    }
}
