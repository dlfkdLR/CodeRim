import CryptoKit
import Foundation

// Generate a brand-new QA seed for sign_update, or verify data with a supplied public key.
let arguments = Array(CommandLine.arguments.dropFirst())
do {
    if arguments.count == 2, arguments[0] == "generate" {
        let key = Curve25519.Signing.PrivateKey()
        let seed = Data(key.rawRepresentation.base64EncodedString().utf8)
        guard FileManager.default.createFile(atPath: arguments[1], contents: seed,
                                             attributes: [.posixPermissions: 0o600]) else { exit(2) }
        print(key.publicKey.rawRepresentation.base64EncodedString())
    } else if arguments.count == 4, arguments[0] == "verify",
              let bytes = Data(base64Encoded: arguments[1]),
              let signature = Data(base64Encoded: arguments[2]) {
        let key = try Curve25519.Signing.PublicKey(rawRepresentation: bytes)
        let data = try Data(contentsOf: URL(fileURLWithPath: arguments[3]), options: .mappedIfSafe)
        exit(key.isValidSignature(signature, for: data) ? 0 : 1)
    } else { exit(2) }
} catch {
    FileHandle.standardError.write(Data("CryptoKit verification failed\n".utf8))
    exit(2)
}
