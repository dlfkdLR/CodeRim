import Foundation
import Security

/// A tiny generic-password store for the one credential the notch owns rather
/// than borrows: an Ollama Cloud API key the user pastes into Settings.
///
/// The login Keychain's own ACL is the protection model here, the same as
/// `KeychainAccountVault` — no iOS accessibility class is set.
enum NotchKeychain {
    private static func query(service: String, account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account,
         kSecAttrSynchronizable as String: false]
    }

    static func read(service: String, account: String) -> String? {
        var request = query(service: service, account: account)
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(request as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data,
              let value = String(data: data, encoding: .utf8),
              !value.isEmpty
        else { return nil }
        return value
    }

    @discardableResult
    static func store(_ value: String, service: String, account: String) -> Bool {
        let base = query(service: service, account: account)
        let changes = [kSecValueData as String: Data(value.utf8)]
        let update = SecItemUpdate(base as CFDictionary, changes as CFDictionary)
        if update == errSecItemNotFound {
            return SecItemAdd(base.merging(changes) { _, new in new } as CFDictionary, nil) == errSecSuccess
        }
        return update == errSecSuccess
    }

    @discardableResult
    static func delete(service: String, account: String) -> Bool {
        let status = SecItemDelete(query(service: service, account: account) as CFDictionary)
        return status == errSecSuccess || status == errSecItemNotFound
    }
}
