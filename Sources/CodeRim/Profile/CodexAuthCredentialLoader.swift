import Darwin
import Foundation

struct CodexAuthCredentialLoader: Sendable {
    static let maximumFileSize = 256 * 1_024
    static let maximumAccessTokenLength = 16 * 1_024
    static let maximumAccountIDLength = 256

    private let authFileURL: URL

    init(
        authFileURL: URL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex", isDirectory: true)
            .appendingPathComponent("auth.json", isDirectory: false)
    ) {
        self.authFileURL = authFileURL
    }

    func load() throws -> ProfileCredential {
        let descriptor = authFileURL.path.withCString {
            open($0, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC)
        }
        guard descriptor >= 0 else {
            if errno == ELOOP {
                throw ProfileUsageError.unsafeCredentialFile
            }
            throw ProfileUsageError.credentialsUnavailable
        }
        defer { close(descriptor) }

        var fileStatus = stat()
        guard fstat(descriptor, &fileStatus) == 0 else {
            throw ProfileUsageError.credentialsUnavailable
        }
        guard (fileStatus.st_mode & S_IFMT) == S_IFREG,
              fileStatus.st_uid == geteuid()
        else {
            throw ProfileUsageError.unsafeCredentialFile
        }

        let permissions = fileStatus.st_mode & 0o7777
        guard (permissions & ~mode_t(0o600)) == 0,
              (permissions & mode_t(0o400)) != 0
        else {
            throw ProfileUsageError.unsafeCredentialFile
        }
        guard fileStatus.st_size >= 0,
              fileStatus.st_size <= off_t(Self.maximumFileSize)
        else {
            throw ProfileUsageError.unsafeCredentialFile
        }

        let data = try readAll(from: descriptor)
        let document: AuthDocument
        do {
            document = try JSONDecoder().decode(AuthDocument.self, from: data)
        } catch {
            throw ProfileUsageError.invalidCredentials
        }

        guard Self.isValidHeaderValue(
            document.tokens.accessToken,
            maximumUTF8Length: Self.maximumAccessTokenLength
        ), Self.isValidHeaderValue(
            document.tokens.accountID,
            maximumUTF8Length: Self.maximumAccountIDLength
        ) else {
            throw ProfileUsageError.invalidCredentials
        }

        return ProfileCredential(
            accessToken: document.tokens.accessToken,
            accountID: document.tokens.accountID
        )
    }

    private func readAll(from descriptor: Int32) throws -> Data {
        var result = Data()
        result.reserveCapacity(8 * 1_024)
        var buffer = [UInt8](repeating: 0, count: 8 * 1_024)

        while true {
            let bytesRead = buffer.withUnsafeMutableBytes { bytes in
                read(descriptor, bytes.baseAddress, bytes.count)
            }
            if bytesRead == 0 { return result }
            if bytesRead < 0 {
                if errno == EINTR { continue }
                throw ProfileUsageError.credentialsUnavailable
            }
            guard result.count <= Self.maximumFileSize - bytesRead else {
                throw ProfileUsageError.unsafeCredentialFile
            }
            result.append(contentsOf: buffer.prefix(bytesRead))
        }
    }

    static func isValidHeaderValue(
        _ value: String,
        maximumUTF8Length: Int
    ) -> Bool {
        let utf8Length = value.utf8.count
        guard utf8Length > 0, utf8Length <= maximumUTF8Length else { return false }
        guard value == value.trimmingCharacters(in: .whitespacesAndNewlines) else { return false }
        return !value.unicodeScalars.contains { scalar in
            CharacterSet.controlCharacters.contains(scalar)
                || CharacterSet.whitespacesAndNewlines.contains(scalar)
        }
    }
}

private struct AuthDocument: Decodable {
    let tokens: AuthTokens
}

private struct AuthTokens: Decodable {
    let accessToken: String
    let accountID: String

    private enum CodingKeys: String, CodingKey {
        case accessToken = "access_token"
        case accountID = "account_id"
    }
}
