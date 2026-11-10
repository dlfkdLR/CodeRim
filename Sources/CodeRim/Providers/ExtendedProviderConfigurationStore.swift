import Foundation
import Security
import LocalAuthentication
import CodexBarCore

/// A separate, exact Keychain item per provider. No secrets in UserDefaults, logs or CodexBar's config.
enum ExtendedProviderConfigurationStore {
    /// The shipping bundle identifier, pinned rather than read.
    ///
    /// A Keychain service name is an address, not a version stamp: every saved
    /// provider credential lives under it, and a service name derived from
    /// `Bundle.main.bundleIdentifier` moves the moment the identifier does —
    /// silently, taking every configured provider with it, with no error and
    /// nothing to migrate back from. The rename to CodeRim already kept this
    /// identifier for exactly that reason; pinning it here means a *future*
    /// rename cannot undo that decision by accident.
    ///
    /// `KeychainAccountVault` has always pinned its service string
    /// (`com.hecholp.codexmeter.saved-codex-accounts.v1`), which is why saved
    /// accounts survived the rename while these would not have. This is the
    /// same rule applied to the same kind of data.
    ///
    /// Unconditional, deliberately: a build with a different identifier is
    /// still this app on this Mac for this user, and letting it read the
    /// providers the user configured is not a boundary worth drawing. macOS
    /// draws the real one — the Keychain item's own ACL prompts when a
    /// differently-signed binary asks for it.
    static let service = "dev.codexmeter.CodexMeter.extended-providers"

    private static func query(_ id: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
         kSecAttrAccount as String: id, kSecAttrSynchronizable as String: false]
    }

    static func load(_ descriptor: ProviderDescriptor, interactive: Bool = false) throws -> ExtendedProviderConfiguration {
        var request = query(descriptor.id.rawValue)
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        if !interactive {
            let context = LAContext()
            context.interactionNotAllowed = true
            request[kSecUseAuthenticationContext as String] = context
        }
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return .init(providerID: descriptor.id) }
        guard status == errSecSuccess, let data = result as? Data else { throw StoreError(status: status) }
        let value = try JSONDecoder().decode(ExtendedProviderConfiguration.self, from: data)
        guard value.provider.id == descriptor.id.instanceID else { throw StoreError(status: errSecDecode) }
        return value
    }

    static func save(_ value: ExtendedProviderConfiguration) throws {
        let data = try JSONEncoder().encode(value)
        let request = query(value.provider.id.rawValue)
        let values = [kSecValueData as String: data]
        var status = SecItemUpdate(request as CFDictionary, values as CFDictionary)
        if status == errSecItemNotFound {
            status = SecItemAdd(request.merging(values) { _, new in new } as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw StoreError(status: status) }
    }

    struct StoreError: LocalizedError {
        let status: OSStatus
        var errorDescription: String? { "Provider settings could not be accessed in Keychain (\(status))." }
    }
}
