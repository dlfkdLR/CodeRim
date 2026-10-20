import CryptoKit
import Foundation

/// The subject distinguishes users sharing a workspace. Only its digest is used
/// for response ownership; credentials are never persisted in usage snapshots.
extension ProfileCredential {
    private static func identityDigest(_ value: String) -> String {
        SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    static func currentAccountKey() throws -> String? {
        try CodexAuthCredentialLoader().load().accountKey
    }

    var accountKey: String? {
        let parts = accessToken.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        var payload = String(parts[1]).replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        payload += String(repeating: "=", count: (4 - payload.count % 4) % 4)
        guard let data = Data(base64Encoded: payload),
              let claims = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let subject = claims["sub"] as? String, !subject.isEmpty else { return nil }
        return Self.identityDigest("\(accountID.utf8.count):\(accountID)\(subject)")
    }
}
