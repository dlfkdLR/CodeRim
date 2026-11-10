import CryptoKit
import Foundation

enum UsageStorageIdentity {
    static func sessionIdentifierFromFilename(_ url: URL) -> String? {
        let stem = url.deletingPathExtension().lastPathComponent
        guard stem.count >= 36 else { return nil }
        let suffix = String(stem.suffix(36))
        guard let identifier = UUID(uuidString: suffix)?.uuidString.lowercased() else { return nil }
        return digest(identifier)
    }

    static func digest(_ value: String) -> String {
        digest(Data(value.utf8))
    }

    static func digest(_ value: Data) -> String {
        SHA256.hash(data: value).map { String(format: "%02x", $0) }.joined()
    }

    static func project(_ workingDirectory: String?, keyData: Data) -> (id: String, name: String)? {
        guard let workingDirectory,
              !workingDirectory.isEmpty,
              workingDirectory.utf8.count <= 4_096,
              !workingDirectory.contains("\0")
        else { return nil }

        let standardizedPath = URL(fileURLWithPath: workingDirectory).standardizedFileURL.path
        let name = URL(fileURLWithPath: standardizedPath).lastPathComponent
        guard !name.isEmpty,
              name != ".",
              name != "..",
              name.utf8.count <= 128,
              !name.contains("\0")
        else { return nil }

        let key = SymmetricKey(data: keyData)
        let material = Data("CodexMeter.project.v1|\(standardizedPath)".utf8)
        let digest = HMAC<SHA256>.authenticationCode(for: material, using: key)
            .map { String(format: "%02x", $0) }
            .joined()
        return (digest, name)
    }

}
