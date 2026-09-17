import Foundation
import Security

enum AccountSwitchError: Error, LocalizedError, Equatable {
    case invalidLogin, unsafeFile, changedLogin, keychain, unsupportedStorage, managedAccount
    case codexRunning, quitCancelled, loginFailed, loginCancelled, tooManyAccounts, vaultFull, unavailable, busy, openCodexFirst
    case processInspectionFailed

    var errorDescription: String? {
        switch self {
        case .invalidLogin: "A complete ChatGPT login was not found. Sign in to Codex, then save the account again."
        case .unsafeFile: "The Codex login file is not a private, user-owned regular file. It was not changed."
        case .changedLogin: "Codex changed its login during the switch. Nothing was overwritten. Try again."
        case .keychain: "Saved accounts could not be accessed. Unlock your macOS login Keychain and try again."
        case .unsupportedStorage: "Switching currently requires Codex’s default home and file-based login storage. Your configuration was not changed."
        case .managedAccount: "Your managed Codex login policy does not allow this account."
        case .codexRunning: "A Codex process is using the login file right now. Wait a moment and try again; if it persists, close other Codex sessions (CLI or editor extensions)."
        case .processInspectionFailed: "Could not verify whether Codex processes are closed. Your login was not changed. Try again."
        case .quitCancelled: "Codex did not quit. Finish or save your work, then try again."
        case .loginFailed: "Sign-in did not finish. Try Add Account again."
        case .loginCancelled: "Sign-in cancelled. Your current Codex login was not changed."
        case .tooManyAccounts: "You can save up to 12 accounts. Remove an unused account first."
        case .vaultFull: "Saved logins exceed the storage limit. Remove an unused account and try again."
        case .unavailable: "A verified Codex installation could not be used. Update Codex and try again."
        case .busy: "Another account operation is in progress. Wait for it to finish, then try again."
        case .openCodexFirst: "Open one Codex desktop app first so its login location can be verified, then try again."
        }
    }
}

/// Secrets are encoded only for the private Keychain item or Codex's own auth file.
/// Do not add CustomStringConvertible, diagnostic logging, or a UserDefaults mirror.
struct SavedCodexAccount: Codable, Identifiable, Equatable, Sendable {
    let id: String
    let email: String
    let workspaceID: String
    let loginData: Data

    /// The ChatGPT plan this login is on ("pro", "plus", "team", …), read from
    /// the id token's own claim. Display and shaping metadata only — it decides
    /// which limits are worth drawing, never what the app is allowed to do.
    /// Optional so an older saved account, or a token without the claim, simply
    /// has no plan rather than a wrong one.
    var planType: String? {
        guard let root = try? JSONSerialization.jsonObject(with: loginData) as? [String: Any],
              let tokens = root["tokens"] as? [String: Any],
              let idToken = tokens["id_token"] as? String,
              let claims = Self.claims(idToken),
              let auth = claims["https://api.openai.com/auth"] as? [String: Any],
              let plan = auth["chatgpt_plan_type"] as? String,
              Self.validLabel(plan)
        else { return nil }
        return plan
    }

    init(loginData: Data) throws {
        guard loginData.count <= 262_144,
              let root = try? JSONSerialization.jsonObject(with: loginData) as? [String: Any],
              root["OPENAI_API_KEY"] == nil || root["OPENAI_API_KEY"] is NSNull,
              let tokens = root["tokens"] as? [String: Any],
              let access = tokens["access_token"] as? String, Self.validToken(access),
              let refresh = tokens["refresh_token"] as? String, Self.validToken(refresh),
              let idToken = tokens["id_token"] as? String, Self.validToken(idToken),
              let accountID = tokens["account_id"] as? String, Self.validLabel(accountID),
              let claims = Self.claims(idToken),
              let subject = claims["sub"] as? String, Self.validLabel(subject),
              let email = claims["email"] as? String, Self.validLabel(email)
        else { throw AccountSwitchError.invalidLogin }
        if let mode = root["auth_mode"], !(mode is NSNull), mode as? String != "chatgpt" {
            throw AccountSwitchError.invalidLogin
        }
        let auth = claims["https://api.openai.com/auth"] as? [String: Any]
        if let claimedID = auth?["chatgpt_account_id"] as? String, claimedID != accountID {
            throw AccountSwitchError.invalidLogin
        }
        // JWT claims are display/selection metadata, not an assertion of authorization.
        // Length-prefixed components avoid identity collisions; email is never the key.
        id = "\(accountID.utf8.count):\(accountID)\(subject)"
        self.email = email
        workspaceID = accountID
        self.loginData = loginData
    }

    private static func validToken(_ value: String) -> Bool {
        !value.isEmpty && value.utf8.count <= 65_536
            && !value.unicodeScalars.contains { CharacterSet.whitespacesAndNewlines.contains($0) || CharacterSet.controlCharacters.contains($0) }
    }

    private static func validLabel(_ value: String) -> Bool {
        !value.isEmpty && value.utf8.count <= 256
            && !value.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) }
    }

    private static func claims(_ token: String) -> [String: Any]? {
        let parts = token.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        var payload = String(parts[1]).replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        payload += String(repeating: "=", count: (4 - payload.count % 4) % 4)
        guard let data = Data(base64Encoded: payload) else { return nil }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }
}

protocol AccountVault {
    func load() throws -> [SavedCodexAccount]
    func save(_ accounts: [SavedCodexAccount]) throws
}

struct KeychainAccountVault: AccountVault {
    private static let maximumEncodedBytes = 4_194_304
    let service: String

    init(service: String = "com.hecholp.codexmeter.saved-codex-accounts.v1") {
        self.service = service
    }

    private var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: "accounts",
         kSecAttrSynchronizable as String: false]
    }

    func load() throws -> [SavedCodexAccount] {
        var request = query
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return [] }
        guard status == errSecSuccess, let data = result as? Data, data.count <= Self.maximumEncodedBytes,
              let accounts = try? JSONDecoder().decode([SavedCodexAccount].self, from: data),
              accounts.count <= 12
        else { throw AccountSwitchError.keychain }
        let validated = try accounts.map { try SavedCodexAccount(loginData: $0.loginData) }
        guard validated == accounts, Set(accounts.map(\.id)).count == accounts.count else {
            throw AccountSwitchError.keychain
        }
        return accounts
    }

    func save(_ accounts: [SavedCodexAccount]) throws {
        guard accounts.count <= 12 else { throw AccountSwitchError.tooManyAccounts }
        if accounts.isEmpty {
            let status = SecItemDelete(query as CFDictionary)
            guard status == errSecSuccess || status == errSecItemNotFound else { throw AccountSwitchError.keychain }
            return
        }
        let data = try Self.encodedForStorage(accounts)
        let changes = [kSecValueData as String: data]
        let status = SecItemUpdate(query as CFDictionary, changes as CFDictionary)
        if status == errSecItemNotFound {
            // This is the macOS login Keychain, whose native ACL controls access.
            // iOS accessibility classes are not the protection model for this backend.
            let item = query.merging(changes) { _, new in new }
            guard SecItemAdd(item as CFDictionary, nil) == errSecSuccess else { throw AccountSwitchError.keychain }
        } else if status != errSecSuccess {
            throw AccountSwitchError.keychain
        }
    }

    static func encodedForStorage(_ accounts: [SavedCodexAccount]) throws -> Data {
        guard accounts.count <= 12 else { throw AccountSwitchError.tooManyAccounts }
        let data = try JSONEncoder().encode(accounts)
        // Base64 plus account metadata can exceed the reader's bound even when
        // every individual login fits. Reject before updating the existing item.
        guard data.count <= maximumEncodedBytes else { throw AccountSwitchError.vaultFull }
        return data
    }
}
