import Foundation

@MainActor
final class ClaudeAccountStore: ObservableObject {
    static let shared = ClaudeAccountStore()
    @Published private(set) var accounts: [SavedClaudeAccount] = []
    @Published private(set) var currentID: String?
    @Published private(set) var isBusy = false
    @Published private(set) var isSigningIn = false
    @Published private(set) var message: String?
    @Published private(set) var isError = false
    var onWillSwitch: () -> Void = {}
    var onDidSwitch: () async -> Void = {}

    private let vault: any ClaudeAccountVault
    private let login: any ClaudeLoginStoring
    private let runtime: any ClaudeAccountRuntime
    private let acquireLock: () throws -> CodexAccountOperationLock?
    private var signInTask: Task<Void, Never>?

    init(vault: any ClaudeAccountVault = KeychainClaudeAccountVault(),
         login: any ClaudeLoginStoring = ClaudeLoginStore.local(),
         runtime: any ClaudeAccountRuntime = LocalClaudeAccountRuntime(),
         acquireLock: @escaping () throws -> CodexAccountOperationLock? = {
             try CodexAccountOperationLock.acquire(directory: FileManager.default.homeDirectoryForCurrentUser)
         }) {
        self.vault = vault; self.login = login; self.runtime = runtime; self.acquireLock = acquireLock
    }

    func load() {
        guard !isBusy else { return }
        do {
            accounts = try vault.load()
            try runtime.checkPolicy()
            currentID = try login.read().account()?.id
        } catch { currentID = nil; fail(error) }
    }

    func saveCurrent() async {
        guard !isBusy else { return }
        isBusy = true
        defer { isBusy = false }
        do {
            let lease = try acquireLock()
            defer { withExtendedLifetime(lease) {} }
            try runtime.checkPolicy()
            guard let account = try login.read().account() else { throw ClaudeAccountError.invalidLogin }
            try upsert(account)
            currentID = account.id
            succeed("Current account saved.")
        } catch { fail(error) }
    }

    func addAccount() {
        guard !isBusy else { return }
        isBusy = true; isSigningIn = true
        succeed("Complete sign-in in your browser. Your current account stays signed in.")
        signInTask = Task {
            defer { isBusy = false; isSigningIn = false; signInTask = nil }
            do {
                let lease = try acquireLock()
                defer { withExtendedLifetime(lease) {} }
                try runtime.checkPolicy()
                guard try vault.load().count < 12 else { throw ClaudeAccountError.full }
                let account = try await runtime.signIn()
                try Task.checkCancellation()
                try runtime.checkPolicy()
                try upsert(account)
                succeed("Account added. Select Switch to use it in Claude Code.")
            } catch { fail(error) }
        }
    }

    func cancelSignIn() { signInTask?.cancel() }

    func remove(_ id: String) {
        guard !isBusy else { return }
        do {
            let lease = try acquireLock()
            defer { withExtendedLifetime(lease) {} }
            let remaining = try vault.load().filter { $0.id != id }
            try vault.save(remaining)
            accounts = remaining
            succeed("Saved account removed. Claude Code is still signed in.")
        } catch { fail(error) }
    }

    /// The view asks for confirmation; never close or kill the user's sessions.
    func switchAccount(to id: String) async {
        guard !isBusy else { return }
        isBusy = true
        defer { isBusy = false }
        var notified = false
        do {
            let lease = try acquireLock()
            defer { withExtendedLifetime(lease) {} }
            try runtime.checkPolicy()
            guard let selected = try vault.load().first(where: { $0.id == id }) else { throw ClaudeAccountError.invalidLogin }
            let original = try login.read()
            let current = try original.account()
            if current?.id == id { currentID = id; succeed("This account is already active."); return }
            try runtime.requireStopped()
            // Preserve any refresh-token rotation before leaving the account.
            if let current { try upsert(current) }
            onWillSwitch(); notified = true
            try runtime.requireStopped()
            try login.replace(with: selected, expecting: original)
            currentID = selected.id
            succeed("Account switched. Start Claude Code to use it.")
        } catch { fail(error) }
        if notified { await onDidSwitch() }
    }

    private func upsert(_ account: SavedClaudeAccount) throws {
        var updated = try vault.load()
        if let index = updated.firstIndex(where: { $0.id == account.id }) { updated[index] = account }
        else { updated.append(account) }
        try vault.save(updated)
        accounts = updated
    }

    private func succeed(_ text: String) { isError = false; message = text }
    private func fail(_ error: Error) {
        isError = !(error is CancellationError) && error as? ClaudeAccountError != .cancelled
        message = error is CancellationError ? ClaudeAccountError.cancelled.errorDescription
            : (error as? ClaudeAccountError)?.errorDescription
                ?? (error as? ClaudeIntegrationError)?.errorDescription
                ?? "Claude accounts could not be updated. Try again."
    }
}
