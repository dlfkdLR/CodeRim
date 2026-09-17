import CryptoKit
import Foundation
import Security

enum ClaudeAccountError: Error, LocalizedError, Equatable {
    case invalidLogin, keychain, unsafeFile, changedLogin, running, policy, loginFailed, cancelled, full, rollback

    var errorDescription: String? {
        switch self {
        case .invalidLogin: "A complete Claude subscription login was not found. Sign in to Claude Code, then save it again."
        case .keychain: "Claude accounts could not be accessed. Unlock your macOS login Keychain and try again."
        case .unsafeFile: "Claude’s account configuration could not be updated safely. Check its file permissions and try again."
        case .changedLogin: "Claude’s login changed during this operation. Refresh the account list and try again."
        case .running: "Close your Claude Code sessions before switching, then try again. Your sessions were not stopped."
        case .policy: "This Claude configuration uses managed or external authentication. Switch accounts through Claude Code."
        case .loginFailed: "Sign-in did not finish. Try Add Account again."
        case .cancelled: "Sign-in cancelled. Your current Claude account is unchanged."
        case .full: "You can save up to 12 Claude accounts. Remove an unused account first."
        case .rollback: "Claude’s login changed while recovering the switch. Sign in again through Claude Code before continuing."
        }
    }
}

/// Only this provider's OAuth record and identity are kept in the private vault.
/// Never put this type, its encoded data, or credentials in logs or preferences.
struct SavedClaudeAccount: Codable, Identifiable, Equatable, Sendable {
    let id: String
    let email: String
    let organizationID: String
    let accountID: String
    let oauthData: Data
    let profileData: Data

    init(oauthData: Data, profileData: Data) throws {
        guard oauthData.count <= 131_072, profileData.count <= 65_536,
              let oauth = try? JSONSerialization.jsonObject(with: oauthData) as? [String: Any],
              let profile = try? JSONSerialization.jsonObject(with: profileData) as? [String: Any],
              Self.token(oauth["accessToken"]), Self.token(oauth["refreshToken"]),
              let expiry = oauth["expiresAt"] as? Double, expiry.isFinite, expiry > 0,
              let scopes = oauth["scopes"] as? [String], scopes.contains("user:inference"),
              let email = Self.label(profile["emailAddress"]),
              let organization = Self.label(profile["organizationUuid"]),
              let account = Self.label(profile["accountUuid"])
        else { throw ClaudeAccountError.invalidLogin }
        self.email = email
        organizationID = organization
        accountID = account
        id = SHA256.hash(data: Data("\(organization.utf8.count):\(organization)\(account)".utf8))
            .map { String(format: "%02x", $0) }.joined()
        self.oauthData = oauthData
        self.profileData = profileData
    }

    var planName: String? {
        guard let oauth = try? JSONSerialization.jsonObject(with: oauthData) as? [String: Any] else { return nil }
        return ClaudeAccount(email: email, subscriptionType: oauth["subscriptionType"] as? String,
            authenticationMethod: "claude.ai", rateLimitTier: oauth["rateLimitTier"] as? String).planName
    }

    var integrationIdentity: String {
        ClaudeAccount(email: email, subscriptionType: nil, authenticationMethod: "claude.ai",
                      stableIdentity: "\(organizationID)\u{1F}\(email)").linkIdentifier
    }

    private static func token(_ value: Any?) -> Bool {
        guard let value = value as? String, !value.isEmpty, value.utf8.count <= 65_536 else { return false }
        return !value.unicodeScalars.contains { CharacterSet.whitespacesAndNewlines.contains($0) || CharacterSet.controlCharacters.contains($0) }
    }

    private static func label(_ value: Any?) -> String? {
        guard let text = value as? String, !text.isEmpty, text.utf8.count <= 320,
              !text.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains) else { return nil }
        return text
    }
}

protocol ClaudeAccountVault {
    func load() throws -> [SavedClaudeAccount]
    func save(_ accounts: [SavedClaudeAccount]) throws
}

/// Exact service/account queries; never enumerate the user's Keychain.
struct ClaudeKeychainItem {
    let service: String
    let account: String
    private var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
         kSecAttrAccount as String: account, kSecAttrSynchronizable as String: false]
    }

    func read() throws -> Data? {
        var query = query
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data, data.count <= 4_194_304 else { throw ClaudeAccountError.keychain }
        return data
    }

    func replace(_ data: Data?, expecting original: Data?) throws {
        guard try read() == original else { throw ClaudeAccountError.changedLogin }
        guard let data else {
            let result = SecItemDelete(query as CFDictionary)
            guard result == errSecSuccess || result == errSecItemNotFound else { throw ClaudeAccountError.keychain }
            return
        }
        guard data.count <= 4_194_304 else { throw ClaudeAccountError.full }
        let values = [kSecValueData as String: data]
        let status = SecItemUpdate(query as CFDictionary, values as CFDictionary)
        if status == errSecItemNotFound {
            guard original == nil,
                  SecItemAdd(query.merging(values) { _, value in value } as CFDictionary, nil) == errSecSuccess else { throw ClaudeAccountError.keychain }
        } else if status != errSecSuccess { throw ClaudeAccountError.keychain }
    }
}

struct KeychainClaudeAccountVault: ClaudeAccountVault {
    var item = ClaudeKeychainItem(service: "dev.codexmeter.saved-claude-accounts.v1", account: "accounts")

    func load() throws -> [SavedClaudeAccount] {
        guard let data = try item.read() else { return [] }
        guard let accounts = try? JSONDecoder().decode([SavedClaudeAccount].self, from: data), accounts.count <= 12,
              Set(accounts.map(\.id)).count == accounts.count else { throw ClaudeAccountError.keychain }
        let validated = try accounts.map { try SavedClaudeAccount(oauthData: $0.oauthData, profileData: $0.profileData) }
        guard validated == accounts else { throw ClaudeAccountError.keychain }
        return accounts
    }

    func save(_ accounts: [SavedClaudeAccount]) throws {
        guard accounts.count <= 12 else { throw ClaudeAccountError.full }
        let original = try item.read()
        let data = accounts.isEmpty ? nil : try JSONEncoder().encode(accounts)
        try item.replace(data, expecting: original)
    }
}
