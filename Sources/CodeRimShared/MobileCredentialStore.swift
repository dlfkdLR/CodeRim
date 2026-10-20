import Foundation
import Security

public struct MobileCredential: Codable, Sendable {
    public var endpoint: URL
    public var token: String
    public var expiresAt: Double
    public var appleUserID: String?
    public init(endpoint: URL, session: MobileSessionToken, appleUserID: String? = nil) {
        self.endpoint = endpoint; token = session.token; expiresAt = session.expiresAt; self.appleUserID = appleUserID
    }
}

/// The endpoint and token are stored together to prevent a settings edit sending a token to another server.
public protocol MobileCredentialStoring: Sendable {
    func load() async throws -> MobileCredential?
    func save(_ credential: MobileCredential) async throws
    func delete() async throws
}

public actor MobileCredentialStore: MobileCredentialStoring {
    public static let shared = MobileCredentialStore()
    private let service = "dev.coderim.mobile-relay"
    public init() {}
    private var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
         kSecAttrAccount as String: "device-connection"]
    }
    public func load() throws -> MobileCredential? {
        var query = query
        query[kSecReturnData as String] = true; query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else { throw MobileRelayError.keychain }
        return try JSONDecoder().decode(MobileCredential.self, from: data)
    }
    public func save(_ credential: MobileCredential) throws {
        let data = try JSONEncoder().encode(credential)
        let values: [String: Any] = [kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly]
        let status = SecItemUpdate(query as CFDictionary, values as CFDictionary)
        if status == errSecItemNotFound {
            guard SecItemAdd(query.merging(values) { _, new in new } as CFDictionary, nil) == errSecSuccess else { throw MobileRelayError.keychain }
        } else if status != errSecSuccess { throw MobileRelayError.keychain }
    }
    public func delete() throws {
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw MobileRelayError.keychain }
    }
}
