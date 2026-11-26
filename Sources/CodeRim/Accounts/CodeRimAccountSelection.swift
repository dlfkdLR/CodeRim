#if CODERIM_RUST_ACCOUNTING
import Foundation
import CryptoKit

/// Only an opaque ID is persisted here. Authentication material remains in the
/// existing CodeRim vault created by explicit account sign-in.
enum CodeRimAccountSelection {
    static let key = "codeRim.selectedSavedCodexAccount"

    static func marker(for id: String) -> String {
        SHA256.hash(data: Data(id.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    static func profileCredential(defaults: UserDefaults = .standard,
                                  vault: any AccountVault = KeychainAccountVault()) throws -> ProfileCredential {
        guard let id = defaults.string(forKey: key),
              let account = try vault.load().first(where: { marker(for: $0.id) == id }),
              try SavedCodexAccount(loginData: account.loginData).id == account.id else {
            throw ProfileUsageError.credentialsUnavailable
        }
        return try CodexAuthCredentialLoader.decode(account.loginData)
    }
}

/// The official app-server remains the public-interface reader when no saved
/// account is selected. Saved reads reuse the existing isolated account reader,
/// including its compare-and-swap handling for a rotated refresh token.
@MainActor
final class SelectedAccountLimitProvider: AccountLimitProviding {
    private let follow: any AccountLimitProviding
    private let saved: ViewedCodexAccountLimits
    private let accounts: CodexAccountStore

    init(follow: any AccountLimitProviding = AppServerLimitProvider(),
         saved: ViewedCodexAccountLimits = ViewedCodexAccountLimits(), accounts: CodexAccountStore = .shared) {
        self.follow = follow
        self.saved = saved
        self.accounts = accounts
    }

    func readLimits() async throws -> AccountLimitsSnapshot {
        guard let marker = accounts.selectedOwnedMarker else { return try await follow.readLimits() }
        guard let id = try accounts.selectedSavedID(for: marker) else { throw AccountSwitchError.invalidLogin }
        if saved.accountID == id {
            await saved.refresh(accounts: accounts)
        } else {
            await saved.selectSaved(id, accounts: accounts)
        }
        try Task.checkCancellation()
        guard accounts.selectedOwnedMarker == marker, saved.accountID == id, saved.status == .ready,
              let snapshot = saved.snapshot else { throw AccountLimitError.processLaunchFailed }
        return snapshot
    }
}
#endif
