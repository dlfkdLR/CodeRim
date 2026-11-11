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
    private enum LoadFailure: Equatable { case vault, login }
    private var loadFailure: LoadFailure?

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
        do { accounts = try vault.load() }
        catch {
            fail(error)
            loadFailure = .vault
            return
        }
        do {
            let current = try readCurrentAccount()
            updateCurrentMetadata(current)
            clearLoadFailure()
        } catch {
            fail(error)
            loadFailure = .login
            // A failed explicit load must be retried even if its file date
            // matches the last successful background poll.
            hasReadPlan = false
        }
    }

    /// Re-read display metadata from the live login, without opening the vault.
    ///
    /// Separate from `load()` so a caller that only needs the plan — the notch,
    /// on every poll — does not also run a Keychain query, which is the
    /// expensive half and the half that can prompt. Skips a known unchanged
    /// modification date; an unknown date must be read again.
    func refreshCurrentPlanType() {
        guard !isBusy else { return }
        let stamp = login.lastModified
        // A missing modification date cannot prove that the login is unchanged.
        // A successful read with no plan claim can still be cached when it has a date.
        guard stamp == nil || stamp != lastPlanStamp || !hasReadPlan else { return }
        do {
            let account = try readCurrentAccount()
            // A failed read can be transient. Do not clear the last verified
            // identity or mark this stamp as checked before a successful read.
            lastPlanStamp = stamp
            hasReadPlan = true
            updateCurrentMetadata(account)
            if loadFailure == .login { clearLoadFailure() }
        } catch {
            // Retry on the next poll, even if the file's date did not change.
        }
    }

    private func updateCurrentMetadata(_ account: SavedCodexAccount?) {
        let externalChange = hasReadIdentity && currentID != account?.id
            && !AccountSwitchActivity.isSwitching
        if externalChange {
            AccountSwitchActivity.generation &+= 1
            onAccountWillChange()
        }
        hasReadIdentity = true
        if currentID != account?.id { currentID = account?.id }
        if currentAccountEmail != account?.email { currentAccountEmail = account?.email }
        if currentPlanType != account?.planType { currentPlanType = account?.planType }
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
                // Keep the last verified identity if the login cannot be read
                // while reporting a committed switch failure.
                do { updateCurrentMetadata(try readCurrentAccount()) } catch { }
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
        let current: SavedCodexAccount?
        do {
            current = try readCurrentAccount()
            updateCurrentMetadata(current)
        } catch {
            throw AccountSwitchError.cliVerificationFailed
        }
        guard verified, current?.id == account.id else { throw AccountSwitchError.cliVerificationFailed }
    }

    /// The saved login for `id`, read from the vault rather than the published list.
    func savedAccount(_ id: String) throws -> SavedCodexAccount? {
        try vault.load().first { $0.id == id }
    }

    enum RotationSaveResult { case saved, superseded, retry }

    /// Replaces only the login this probe read. Superseded results are discarded;
    /// lock or vault failures let the caller retain the renewed login for retry.
    func storeRotatedLogin(_ loginData: Data, for id: String, expecting base: Data) -> RotationSaveResult {
        guard !isBusy, !AccountSwitchActivity.isSwitching,
              let lease = try? acquireLock(),
              let account = try? SavedCodexAccount(loginData: loginData), account.id == id else { return .retry }
        return withExtendedLifetime(lease) {
            do {
                var saved = try vault.load()
                guard let index = saved.firstIndex(where: { $0.id == id }), saved[index].loginData == base else {
                    return .superseded
                }
                saved[index] = account
                try vault.save(saved)
                accounts = saved
                return .saved
            } catch { return .retry }
        }
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

    private func clearLoadFailure() {
        guard loadFailure != nil else { return }
        loadFailure = nil
        message = nil
        isError = false
    }

    private func succeed(_ text: String) { loadFailure = nil; message = text; isError = false }
    private func fail(_ error: Error) {
        loadFailure = nil
        isError = true
        // Never expose raw subprocess, OAuth, file, or Keychain errors.
        message = (error as? AccountSwitchError)?.errorDescription ?? AccountSwitchError.unavailable.errorDescription
    }
}
