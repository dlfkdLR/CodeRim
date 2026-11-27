import CryptoKit
import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

enum UsageStorageIdentity {
    static func sessionIdentifierFromFilename(_ url: URL) -> String? {
        let stem = url.deletingPathExtension().lastPathComponent
        guard stem.count >= 36 else { return nil }
        let suffix = String(stem.suffix(36))
        guard let identifier = UUID(uuidString: suffix)?.uuidString.lowercased() else { return nil }
        return digest(identifier)
    }

    static func claudeMessage(_ messageID: String) -> String {
        #if CODERIM_RUST_ACCOUNTING
        var id = messageID
        let encoded = id.withUTF8 { string(cr_claude_message_identity($0.baseAddress, $0.count)) }
        if let encoded { return encoded }
        #endif
        return digest("claude-message|\(messageID)")
    }

    static func claudeLineage(_ sessionID: String, agentID: String?) -> (session: String, parent: String?) {
        #if CODERIM_RUST_ACCOUNTING
        var id = sessionID
        var agent = agentID ?? ""
        let encoded = id.withUTF8 { session in
            agent.withUTF8 { suffix in
                cr_claude_lineage_identity(session.baseAddress, session.count, suffix.baseAddress,
                                          suffix.count, agentID != nil)
            }
        }
        if let session = string(encoded.session) {
            if agentID == nil { return (session, nil) }
            if let parent = string(encoded.parent) { return (session, parent) }
        }
        #endif
        let parent = digest("claude-session|\(sessionID)")
        guard let agentID else { return (parent, nil) }
        return (digest("claude-agent|\(sessionID)|\(agentID)"), parent)
    }

    static func digest(_ value: String) -> String {
        digest(Data(value.utf8))
    }

    static func digest(_ value: Data) -> String {
        lowercaseDigest(SHA256.hash(data: value))
    }

    static func lowercaseDigest<Bytes: ContiguousBytes>(_ digest: Bytes) -> String {
        digest.withUnsafeBytes { buffer in
            #if CODERIM_RUST_ACCOUNTING
            if let encoded = string(cr_digest_hex(buffer.bindMemory(to: UInt8.self).baseAddress, buffer.count)) {
                return encoded
            }
            #endif
            return buffer.map { String(format: "%02x", $0) }.joined()
        }
    }

    #if CODERIM_RUST_ACCOUNTING
    static func string(_ digest: cr_digest_text) -> String? {
        guard digest.valid != 0 else { return nil }
        return withUnsafeBytes(of: digest.ascii) { String(decoding: $0, as: UTF8.self) }
    }
    #endif

    static func project(_ workingDirectory: String?, keyData: Data) -> (id: String, name: String)? {
        guard let workingDirectory,
              !workingDirectory.isEmpty,
              workingDirectory.utf8.count <= 4_096,
              !workingDirectory.contains("\0")
        else { return nil }

        let url = URL(fileURLWithPath: workingDirectory).standardizedFileURL
        let standardizedPath = url.path
        let name = url.lastPathComponent
        guard !name.isEmpty,
              name != ".",
              name != "..",
              name.utf8.count <= 128,
              !name.contains("\0")
        else { return nil }

        #if CODERIM_RUST_ACCOUNTING
        var pathBytes = standardizedPath
        let encoded = pathBytes.withUTF8 { path in
            keyData.withUnsafeBytes { key in
                cr_usage_project_identity(path.baseAddress, path.count,
                                          key.bindMemory(to: UInt8.self).baseAddress, key.count)
            }
        }
        if let id = string(encoded) { return (id, name) }
        #endif
        let key = SymmetricKey(data: keyData)
        let material = Data("CodexMeter.project.v1|\(standardizedPath)".utf8)
        let digest = lowercaseDigest(HMAC<SHA256>.authenticationCode(for: material, using: key))
        return (digest, name)
    }

}
