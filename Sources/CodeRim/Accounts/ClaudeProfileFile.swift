import CryptoKit
import Darwin
import Foundation

protocol ClaudeProfileStoring {
    func read() throws -> Data?
    func replace(with data: Data, expecting original: Data?) throws
}

/// Atomic, owner-only replacement of the profile JSON. The containing user
/// configuration and all fields except oauthAccount are preserved.
struct ClaudeProfileFile: ClaudeProfileStoring {
    let url: URL
    private var directory: URL { url.deletingLastPathComponent() }
    private var name: String { url.lastPathComponent }

    func read() throws -> Data? {
        let dir = try openDirectory()
        defer { close(dir) }
        return try read(in: dir)
    }

    func replace(with data: Data, expecting original: Data?) throws {
        guard data.count <= 8_388_608, (try? JSONSerialization.jsonObject(with: data)) is [String: Any] else { throw ClaudeAccountError.unsafeFile }
        let dir = try openDirectory()
        defer { close(dir) }
        guard try read(in: dir) == original else { throw ClaudeAccountError.changedLogin }
        let name = ".coderim-login-\(UUID().uuidString)"
        let fd = openat(dir, name, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw ClaudeAccountError.unsafeFile }
        defer { close(fd); unlinkat(dir, name, 0) }
        // 0600 is not the last word: an inheritable ACL on the directory is
        // stamped onto a file at creation, so a staging file can be born
        // world-writable however carefully its mode was set. Checked after the
        // fact because inheritance happens in `openat`, not before it.
        guard !CredentialFileSafety.hasPermissiveACL(fileDescriptor: fd) else {
            throw ClaudeAccountError.unsafeFile
        }
        try data.withUnsafeBytes { bytes in
            var offset = 0
            while offset < bytes.count {
                let count = Darwin.write(fd, bytes.baseAddress!.advanced(by: offset), bytes.count - offset)
                if count < 0, errno == EINTR { continue }
                guard count > 0 else { throw ClaudeAccountError.unsafeFile }
                offset += count
            }
        }
        guard fsync(fd) == 0 else { throw ClaudeAccountError.unsafeFile }
        guard try read(in: dir) == original else { throw ClaudeAccountError.changedLogin }
        if original == nil {
            // linkat publishes without replacement. A newly created file or symlink
            // must win over a switch that observed the user as logged out.
            guard linkat(dir, name, dir, self.name, 0) == 0 else {
                throw errno == EEXIST ? ClaudeAccountError.changedLogin : ClaudeAccountError.unsafeFile
            }
            _ = unlinkat(dir, name, 0)
        } else {
            guard renameat(dir, name, dir, self.name) == 0 else { throw ClaudeAccountError.unsafeFile }
        }
        // After rename the new login is committed. Never report a pre-commit failure
        // or roll back over another writer if directory fsync is unsupported.
        _ = fsync(dir)
    }

    private func openDirectory() throws -> Int32 {
        let fd = open(directory.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw ClaudeAccountError.unsafeFile }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_uid == getuid(), info.st_mode & 0o022 == 0,
              !CredentialFileSafety.hasPermissiveACL(fileDescriptor: fd) else {
            close(fd)
            throw ClaudeAccountError.unsafeFile
        }
        return fd
    }

    private func read(in dir: Int32) throws -> Data? {
        let fd = openat(dir, self.name, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC)
        if fd < 0, errno == ENOENT { return nil }
        guard fd >= 0 else { throw ClaudeAccountError.invalidLogin }
        defer { close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG,
              info.st_uid == getuid(), info.st_nlink == 1, info.st_mode & 0o022 == 0,
              !CredentialFileSafety.hasPermissiveACL(fileDescriptor: fd),
              info.st_size > 0, info.st_size <= 8_388_608
        else { throw ClaudeAccountError.unsafeFile }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 8_192)
        while true {
            let count = Darwin.read(fd, &buffer, buffer.count)
            if count < 0, errno == EINTR { continue }
            guard count >= 0 else { throw ClaudeAccountError.unsafeFile }
            if count == 0 { break }
            data.append(contentsOf: buffer.prefix(count))
            guard data.count <= 8_388_608 else { throw ClaudeAccountError.unsafeFile }
        }
        return data
    }
}
