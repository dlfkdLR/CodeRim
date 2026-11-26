import Foundation
import Security
#if EXTENDED_PROVIDER_FIXTURE
import LocalAuthentication
#endif

// Test-only. Every Security query must name the private keychain before it reaches the OS.
final class PrivateKeychain: @unchecked Sendable {
    static let shared = PrivateKeychain()
    private let handle: SecKeychain
    private let mutex = NSLock()
    private var events: [String] = []

    var trace: [String] { mutex.withLock { events } }

    private init() {
        let args = CommandLine.arguments
        precondition(args.count == 3)
        let file = URL(fileURLWithPath: args[2]).standardizedFileURL.resolvingSymlinksInPath()
        let directory = file.deletingLastPathComponent()
        let temp = FileManager.default.temporaryDirectory.standardizedFileURL.resolvingSymlinksInPath()
        precondition(directory.deletingLastPathComponent().path == temp.path)
        precondition(directory.lastPathComponent.hasPrefix("coderim-private-ollama-"))
        precondition(file.lastPathComponent == "fixture.keychain" && !file.path.contains("/login.keychain"))
        precondition(SecKeychainSetUserInteractionAllowed(false) == errSecSuccess)
        var result: SecKeychain?
        let pass = Array("synthetic-disposable-keychain".utf8)
        if args[1] == "original-save" {
            precondition(!FileManager.default.fileExists(atPath: file.path))
            let status = pass.withUnsafeBytes {
                SecKeychainCreate(file.path, UInt32($0.count), $0.baseAddress, false, nil, &result)
            }
            precondition(status == errSecSuccess, "private keychain creation failed: \(status)")
        } else {
            precondition(SecKeychainOpen(file.path, &result) == errSecSuccess && result != nil)
        }
        guard let result else { fatalError("Missing private keychain handle") }
        handle = result
        var length: UInt32 = 4096
        var pathBuffer = [CChar](repeating: 0, count: Int(length))
        precondition(SecKeychainGetPath(result, &length, &pathBuffer) == errSecSuccess)
        let path = String(decoding: pathBuffer.prefix(Int(length)).map { UInt8(bitPattern: $0) }, as: UTF8.self)
        let actual = URL(fileURLWithPath: path).standardizedFileURL.resolvingSymlinksInPath()
        precondition(actual.deletingLastPathComponent().path == directory.path &&
                     ["fixture.keychain", "fixture.keychain-db"].contains(actual.lastPathComponent))
        unlock()
    }

    func lock() { precondition(SecKeychainLock(handle) == errSecSuccess) }
    func unlock() {
        let pass = Array("synthetic-disposable-keychain".utf8)
        let status = pass.withUnsafeBytes { SecKeychainUnlock(handle, UInt32($0.count), $0.baseAddress, true) }
        precondition(status == errSecSuccess, "private unlock failed: \(status)")
    }

    private func scoped(_ request: CFDictionary, add: Bool = false) -> CFDictionary {
        #if EXTENDED_PROVIDER_FIXTURE
        let extraKeys = [kSecUseAuthenticationContext as String]
        let services = ["dev.codexmeter.CodexMeter.extended-providers"]
        let accounts = ["openai", "azureopenai", "deepseek", "bedrock", "copilot", "openrouter"]
        #else
        let extraKeys: [String] = []
        let services = [OllamaCredentials.keychainService, "dev.codexmeter.test.ollama.sibling"]
        let accounts = [OllamaCredentials.keychainAccount]
        #endif
        let expectedKeys = Set(extraKeys + [kSecClass, kSecAttrService, kSecAttrAccount, kSecAttrSynchronizable,
                                kSecReturnData, kSecMatchLimit, kSecValueData].map { $0 as String })
        guard var values = request as? [String: Any], Set(values.keys).isSubset(of: expectedKeys),
              let service = values[kSecAttrService as String] as? String,
              services.contains(service),
              let account = values[kSecAttrAccount as String] as? String, accounts.contains(account),
              values[kSecClass as String] as? String == kSecClassGenericPassword as String,
              values[kSecAttrSynchronizable as String] as? Bool == false,
              values[kSecMatchSearchList as String] == nil, values[kSecUseKeychain as String] == nil
        else { fatalError("Unexpected fixture query") }
        #if EXTENDED_PROVIDER_FIXTURE
        if let context = values[kSecUseAuthenticationContext as String] {
            precondition((context as? LAContext)?.interactionNotAllowed == true)
        }
        #endif
        if add { values[kSecUseKeychain as String] = handle }
        else { values[kSecMatchSearchList as String] = [handle] }
        return values as CFDictionary
    }

    private func record(_ operation: String, _ status: OSStatus) -> OSStatus {
        mutex.withLock { events.append("\(operation):\(status)") }
        return status
    }

    func read(_ request: CFDictionary, _ result: UnsafeMutablePointer<CFTypeRef?>?) -> OSStatus {
        record("read", Security.SecItemCopyMatching(scoped(request), result))
    }
    func update(_ request: CFDictionary, _ changes: CFDictionary) -> OSStatus {
        guard let update = changes as? [String: Any], Set(update.keys) == [kSecValueData as String],
              update[kSecValueData as String] is Data else { fatalError("Unexpected change to private item") }
        return record("update", Security.SecItemUpdate(scoped(request), changes))
    }
    func add(_ request: CFDictionary, _ result: UnsafeMutablePointer<CFTypeRef?>?) -> OSStatus {
        record("add", Security.SecItemAdd(scoped(request, add: true), result))
    }
    func remove(_ request: CFDictionary) -> OSStatus {
        record("delete", Security.SecItemDelete(scoped(request)))
    }
}
