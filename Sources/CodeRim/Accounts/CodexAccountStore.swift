import Foundation

/// Account-scoped fetches must discard results crossing this generation boundary.
@MainActor
enum AccountSwitchActivity {
    static var isSwitching = false
    static var generation: UInt64 = 0
}

@MainActor
final class CodexAccountStore: ObservableObject {
    static let shared = CodexAccountStore()
    @Published private(set) var accounts: [SavedCodexAccount] = []
    @Published private(set) var currentID: String?
    @Published private(set) var currentAccountEmail: String?
    var currentAccountDisplayName: String? {
        guard let currentAccountEmail else { return nil }
        return accounts.first(where: { $0.id == currentID })?.menuTitle(in: accounts) ?? currentAccountEmail
    }
    /// The plan the signed-in login is on ("pro", "plus", …), from the live
    /// `auth.json` rather than the saved vault — most people never save an
    /// account here, and the plan decides which limits are worth drawing.
    @Published private(set) var currentPlanType: String?
    /// Display labels follow ChatGPT's plan identifiers. Keep the raw value
    /// above for limit filtering and account metadata.
    var currentPlanName: String? {
        guard let plan = currentPlanType?.trimmingCharacters(in: .whitespacesAndNewlines),
              !plan.isEmpty else { return nil }
        switch plan.lowercased() {
        case "prolite": return "Pro 5x"
        case "pro": return "Pro 20x"
        default: return plan.replacingOccurrences(of: "_", with: " ").capitalized
        }
    }
    @Published private(set) var isBusy = false
    @Published private(set) var isSigningIn = false
    @Published private(set) var message: String?
    @Published private(set) var isError = false
    var onAccountWillChange: () -> Void = {}
    var onAccountOperationFinished: () -> Void = {}

    private let vault: any AccountVault
    private let login: any CodexLoginStoring
    private let runtime: any CodexAccountRuntime
    private let acquireLock: () throws -> CodexAccountOperationLock?
    private var loginTask: Task<Void, Never>?
    /// The login's modification date at the last plan read.
    private var lastPlanStamp: Date?
    /// Whether a plan read has happened at all, so a login with no plan claim
    /// is read once rather than on every poll.
    private var hasReadPlan = false
    private var hasReadIdentity = false

    init(vault: any AccountVault = KeychainAccountVault(),
         login: any CodexLoginStoring = CodexLoginFile(directory: CodexLoginFile.defaultDirectory),
         runtime: any CodexAccountRuntime = LocalCodexAccountRuntime(),
         acquireLock: @escaping () throws -> CodexAccountOperationLock? = { try CodexAccountOperationLock.acquire() }) {
        self.vault = vault
        self.login = login
        self.runtime = runtime
        self.acquireLock = acquireLock
    }

    func load() {
        guard !isBusy else { return }
        do {
            accounts = try vault.load()
            let current = try? readCurrentAccount()
            updateCurrentMetadata(current)
        } catch { fail(error) }
    }

    /// Re-read display metadata from the live login, without opening the vault.
    ///
    /// Separate from `load()` so a caller that only needs the plan — the notch,
    /// on every poll — does not also run a Keychain query, which is the
    /// expensive half and the half that can prompt. Reads nothing when the
    /// login file has not changed since the last look.
    func refreshCurrentPlanType() {
        guard !isBusy else { return }
        let stamp = login.lastModified
        // Re-read when the login changed, or when nothing has been read yet.
        // Keyed on having read at all rather than on `currentPlanType`, which
        // is legitimately nil for a login whose token carries no plan claim —
        // asking again every poll would never produce a different answer.
        guard stamp != lastPlanStamp || !hasReadPlan else { return }
        lastPlanStamp = stamp
        hasReadPlan = true
        updateCurrentMetadata(try? readCurrentAccount())
    }

    private func updateCurrentMetadata(_ account: SavedCodexAccount?) {
        let externalChange = hasReadIdentity && currentID != account?.id
            && !AccountSwitchActivity.isSwitching
        if externalChange {
            AccountSwitchActivity.generation &+= 1
            onAccountWillChange()
        }
        hasReadIdentity = true
        currentID = account?.id
        currentAccountEmail = account?.email
        currentPlanType = account?.planType
        if externalChange { onAccountOperationFinished() }
    }

    func saveCurrent() async {
        guard !isBusy else { return }
        isBusy = true
        defer { isBusy = false }
        do {
            let lease = try acquireLock()
            defer { withExtendedLifetime(lease) {} }
            guard let account = try readCurrentAccount() else { throw AccountSwitchError.invalidLogin }
            try await runtime.checkPolicy(for: account.workspaceID)
            try upsert(account)
            updateCurrentMetadata(account)
            succeed("Current account saved.")
        } catch { fail(error) }
    }

    func addAccount() {
        guard !isBusy else { return }
        isBusy = true
        isSigningIn = true
        succeed("Complete sign-in in your browser. Your current account stays signed in.")
        loginTask = Task {
            defer { isBusy = false; isSigningIn = false; loginTask = nil }
            do {
                let lease = try acquireLock()
                defer { withExtendedLifetime(lease) {} }
                try await runtime.checkPolicy(for: nil)
                let account = try await runtime.signIn()
                try Task.checkCancellation()
                try await runtime.checkPolicy(for: account.workspaceID)
                try upsert(account)
                succeed("Account added. Select Switch to use it in Codex.")
            } catch { fail(error) }
        }
    }

    func cancelSignIn() { loginTask?.cancel() }

    func openCodex() async {
        guard !isBusy else { return }
        isBusy = true
        defer { isBusy = false }
        do { try await runtime.openCodex(); message = nil }
        catch { fail(error) }
    }

    func remove(_ id: String) {
        guard !isBusy else { return }
        do {
            let lease = try acquireLock()
            defer { withExtendedLifetime(lease) {} }
            let remaining = try vault.load().filter { $0.id != id }
            try vault.save(remaining)
            accounts = remaining
            succeed("Saved account removed. Codex is still signed in.")
        } catch { fail(error) }
    }

    /// Called only after the user confirms that Codex may quit and reopen.
    func switchAccount(to id: String) async {
        guard !isBusy, !AccountSwitchActivity.isSwitching else { return }
        isBusy = true
        AccountSwitchActivity.isSwitching = true
        AccountSwitchActivity.generation &+= 1
        onAccountWillChange()
        defer {
            AccountSwitchActivity.isSwitching = false
            AccountSwitchActivity.generation &+= 1
            isBusy = false
            onAccountOperationFinished()
        }
        var committed = false
        var didQuit = false
        var didReopen = false
        do {
            let lease = try acquireLock()
            defer { withExtendedLifetime(lease) {} }
            // Reload Keychain instead of trusting a stale view or a row's credential.
            guard let saved = try vault.load().first(where: { $0.id == id }) else {
                throw AccountSwitchError.invalidLogin
            }
            let selected = try SavedCodexAccount(loginData: saved.loginData)
            guard selected.id == id else { throw AccountSwitchError.invalidLogin }
            try await runtime.checkPolicy(for: selected.workspaceID)
            let beforeQuit = try readCurrentAccount()
            if beforeQuit?.id == id {
                updateCurrentMetadata(beforeQuit)
                try await verifyCLIAccount(selected)
                succeed("This account is already active for Codex and new CLI sessions.")
                return
            }
            // Do not refresh a copied credential in a disposable process. Official
            // Codex owns renewal after restart, in its canonical auth.json; a failed
            // preflight RPC must never discard the only rotated refresh token.
            succeed("Waiting for Codex to close…")
            try await runtime.quitCodex()
            didQuit = true
            try Task.checkCancellation()
            let original = try login.read()
            let current = try original.map { try SavedCodexAccount(loginData: $0) }
            guard current?.id == beforeQuit?.id else { throw AccountSwitchError.changedLogin }
            // Capture the departing account's latest refresh token, not an old snapshot.
            if let current { try upsert(current) }
            try await runtime.waitForStopped()
            try login.replace(with: selected.loginData, expecting: original)
            committed = true
            updateCurrentMetadata(selected)
            try await runtime.openCodex()
            didReopen = true
            try await verifyCLIAccount(selected)
            succeed("Account switched for Codex and new CLI sessions. Restart existing CLI sessions to use it.")
        } catch {
            if committed {
                updateCurrentMetadata(try? readCurrentAccount())
                isError = true
                message = didReopen ? AccountSwitchError.cliVerificationFailed.errorDescription
                    : "The shared Codex login was changed, but Codex could not reopen. Open Codex from Applications and restart your CLI sessions."
            } else {
                if didQuit { try? await runtime.openCodex() }
                fail(error)
            }
        }
    }

    private func verifyCLIAccount(_ account: SavedCodexAccount) async throws {
        let verified: Bool
        do { try await runtime.verifyCLIAccount(account); verified = true }
        catch { verified = false }
        // A failed probe can also race an external sign-in. Refresh the badge
        // even when this operation did not write (the account was already active).
        let current = try? readCurrentAccount()
        updateCurrentMetadata(current)
        guard verified, current?.id == account.id else { throw AccountSwitchError.cliVerificationFailed }
    }

    private func upsert(_ account: SavedCodexAccount) throws {
        var saved = try vault.load()
        if let index = saved.firstIndex(where: { $0.id == account.id }) { saved[index] = account }
        else { saved.append(account) }
        guard saved.count <= 12 else { throw AccountSwitchError.tooManyAccounts }
        try vault.save(saved)
        accounts = saved
    }

    private func readCurrentAccount() throws -> SavedCodexAccount? {
        try login.read().map { try SavedCodexAccount(loginData: $0) }
    }

    private func succeed(_ text: String) { message = text; isError = false }
    private func fail(_ error: Error) {
        isError = true
        // Never expose raw subprocess, OAuth, file, or Keychain errors.
        message = (error as? AccountSwitchError)?.errorDescription ?? AccountSwitchError.unavailable.errorDescription
    }
}
