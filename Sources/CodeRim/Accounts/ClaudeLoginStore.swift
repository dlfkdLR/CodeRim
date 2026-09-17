import CryptoKit
import Foundation

protocol ClaudeCredentialStoring {
    func read() throws -> Data?
    func replace(_ data: Data?, expecting original: Data?) throws
}

extension ClaudeKeychainItem: ClaudeCredentialStoring {}

struct ClaudeLoginSnapshot: Equatable {
    let credentials: Data?
    let configuration: Data?

    func account() throws -> SavedClaudeAccount? {
        guard let credentials else { return nil }
        guard let secure = try JSONSerialization.jsonObject(with: credentials) as? [String: Any] else { throw ClaudeAccountError.invalidLogin }
        guard let oauth = secure["claudeAiOauth"], !(oauth is NSNull) else { return nil }
        guard let configuration,
              let config = try JSONSerialization.jsonObject(with: configuration) as? [String: Any],
              let profile = config["oauthAccount"] as? [String: Any] else { throw ClaudeAccountError.invalidLogin }
        return try SavedClaudeAccount(oauthData: JSONSerialization.data(withJSONObject: oauth, options: .sortedKeys),
                                     profileData: JSONSerialization.data(withJSONObject: profile, options: .sortedKeys))
    }
}

protocol ClaudeLoginStoring {
    func read() throws -> ClaudeLoginSnapshot
    func replace(with account: SavedClaudeAccount, expecting original: ClaudeLoginSnapshot) throws
}

struct ClaudeLoginStore: ClaudeLoginStoring {
    let credentials: any ClaudeCredentialStoring
    let profile: any ClaudeProfileStoring

    static func local(configDirectory: URL? = nil) -> ClaudeLoginStore {
        ClaudeLoginStore(credentials: keychain(configDirectory: configDirectory),
            profile: ClaudeProfileFile(url: configDirectory?.appendingPathComponent(".claude.json")
                ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude.json")))
    }

    /// Claude Code derives the macOS item from the explicitly set config home.
    /// An unset CLAUDE_CONFIG_DIR has no suffix, even for the default directory.
    static func keychain(configDirectory: URL?) -> ClaudeKeychainItem {
        let suffix = configDirectory.map { directory in
            "-" + SHA256.hash(data: Data(directory.path.precomposedStringWithCanonicalMapping.utf8))
                .map { String(format: "%02x", $0) }.joined().prefix(8)
        } ?? ""
        return ClaudeKeychainItem(service: "Claude Code-credentials\(suffix)", account: NSUserName())
    }

    func read() throws -> ClaudeLoginSnapshot {
        let before = try credentials.read()
        let configuration = try profile.read()
        guard try credentials.read() == before else { throw ClaudeAccountError.changedLogin }
        return ClaudeLoginSnapshot(credentials: before, configuration: configuration)
    }

    func replace(with account: SavedClaudeAccount, expecting original: ClaudeLoginSnapshot) throws {
        _ = try SavedClaudeAccount(oauthData: account.oauthData, profileData: account.profileData)
        guard try read() == original else { throw ClaudeAccountError.changedLogin }
        var secure = try Self.object(original.credentials)
        var config = try Self.object(original.configuration)
        secure["claudeAiOauth"] = try Self.object(account.oauthData)
        config["oauthAccount"] = try Self.object(account.profileData)
        let nextCredentials = try JSONSerialization.data(withJSONObject: secure, options: .sortedKeys)
        let nextConfig = try JSONSerialization.data(withJSONObject: config, options: .sortedKeys)
        try credentials.replace(nextCredentials, expecting: original.credentials)
        do {
            try profile.replace(with: nextConfig, expecting: original.configuration)
        } catch {
            // Roll back only our own credential write. A concurrent vendor
            // refresh wins; its newest refresh token must never be overwritten.
            do { try credentials.replace(original.credentials, expecting: nextCredentials) }
            catch { throw ClaudeAccountError.rollback }
            throw error
        }
    }

    private static func object(_ data: Data?) throws -> [String: Any] {
        guard let data else { return [:] }
        guard data.count <= 8_388_608,
              let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw ClaudeAccountError.invalidLogin }
        return object
    }
}
