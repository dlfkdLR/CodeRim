import Darwin
import Foundation

/// Serializes account mutations across stable/preview app instances. Codex itself
/// does not participate, so the switch also requires stopped clients and a CAS read.
final class CodexAccountOperationLock {
    private let descriptor: Int32
    private init(descriptor: Int32) { self.descriptor = descriptor }
    deinit { flock(descriptor, LOCK_UN); close(descriptor) }

    static func acquire(directory: URL = CodexLoginFile.defaultDirectory) throws -> CodexAccountOperationLock {
        let dir = open(directory.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard dir >= 0 else { throw AccountSwitchError.unsafeFile }
        defer { close(dir) }
        var info = stat()
        guard fstat(dir, &info) == 0, info.st_uid == getuid(), info.st_mode & 0o022 == 0,
              !CredentialFileSafety.hasPermissiveACL(fileDescriptor: dir) else {
            throw AccountSwitchError.unsafeFile
        }
        let fd = openat(dir, ".codexmeter-accounts.lock", O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK, 0o600)
        guard fd >= 0 else { throw AccountSwitchError.unsafeFile }
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_uid == getuid(),
              info.st_mode & 0o077 == 0, info.st_nlink == 1,
              !CredentialFileSafety.hasPermissiveACL(fileDescriptor: fd) else {
            close(fd); throw AccountSwitchError.unsafeFile
        }
        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else { close(fd); throw AccountSwitchError.busy }
        return CodexAccountOperationLock(descriptor: fd)
    }
}

protocol CodexLoginStoring {
    func read() throws -> Data?
    func replace(with data: Data, expecting original: Data?) throws
    /// When the login last changed, for callers that want to skip re-reading an
    /// unchanged file. Nil when there is no login, or its date cannot be read.
    var lastModified: Date? { get }
}

extension CodexLoginStoring {
    /// Test doubles need not track a date; without one every look re-reads,
    /// which is correct, just not the fast path.
    var lastModified: Date? { nil }
}

/// All operations are relative to an opened, user-owned directory. No symlink traversal,
/// world-readable staging file, truncation of the current login, or broad cleanup.
struct CodexLoginFile: CodexLoginStoring {
    let directory: URL

    var lastModified: Date? {
        try? FileManager.default
            .attributesOfItem(atPath: directory.appendingPathComponent("auth.json").path)[.modificationDate] as? Date
    }

    static var defaultDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex", isDirectory: true)
    }

    func read() throws -> Data? {
        let dir = try openDirectory()
        defer { close(dir) }
        return try read(in: dir)
    }

    func replace(with data: Data, expecting original: Data?) throws {
        _ = try SavedCodexAccount(loginData: data)
        let dir = try openDirectory()
        defer { close(dir) }
        guard try read(in: dir) == original else { throw AccountSwitchError.changedLogin }
        let name = ".coderim-login-\(UUID().uuidString)"
        let fd = openat(dir, name, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw AccountSwitchError.unsafeFile }
        defer { close(fd); unlinkat(dir, name, 0) }
        // 0600 is not the last word: an inheritable ACL on the directory is
        // stamped onto a file at creation, so a staging file can be born
        // world-writable however carefully its mode was set. Checked after the
        // fact because inheritance happens in `openat`, not before it.
        guard !CredentialFileSafety.hasPermissiveACL(fileDescriptor: fd) else {
            throw AccountSwitchError.unsafeFile
        }
        try data.withUnsafeBytes { bytes in
            var offset = 0
            while offset < bytes.count {
                let count = Darwin.write(fd, bytes.baseAddress!.advanced(by: offset), bytes.count - offset)
                if count < 0, errno == EINTR { continue }
                guard count > 0 else { throw AccountSwitchError.unsafeFile }
                offset += count
            }
        }
        guard fsync(fd) == 0 else { throw AccountSwitchError.unsafeFile }
        guard try read(in: dir) == original else { throw AccountSwitchError.changedLogin }
        if original == nil {
            // linkat publishes without replacement. A newly created file or symlink
            // must win over a switch that observed the user as logged out.
            guard linkat(dir, name, dir, "auth.json", 0) == 0 else {
                throw errno == EEXIST ? AccountSwitchError.changedLogin : AccountSwitchError.unsafeFile
            }
            _ = unlinkat(dir, name, 0)
        } else {
            guard renameat(dir, name, dir, "auth.json") == 0 else { throw AccountSwitchError.unsafeFile }
        }
        // After rename the new login is committed. Never report a pre-commit failure
        // or roll back over another writer if directory fsync is unsupported.
        _ = fsync(dir)
    }

    private func openDirectory() throws -> Int32 {
        let fd = open(directory.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw AccountSwitchError.unsafeFile }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_uid == getuid(), info.st_mode & 0o022 == 0,
              !CredentialFileSafety.hasPermissiveACL(fileDescriptor: fd) else {
            close(fd)
            throw AccountSwitchError.unsafeFile
        }
        return fd
    }

    private func read(in dir: Int32) throws -> Data? {
        let fd = openat(dir, "auth.json", O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC)
        if fd < 0, errno == ENOENT { return nil }
        guard fd >= 0 else { throw AccountSwitchError.invalidLogin }
        defer { close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG,
              info.st_uid == getuid(), info.st_nlink == 1, info.st_mode & 0o077 == 0,
              !CredentialFileSafety.hasPermissiveACL(fileDescriptor: fd),
              info.st_size > 0, info.st_size <= 262_144
        else { throw AccountSwitchError.unsafeFile }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 8_192)
        while true {
            let count = Darwin.read(fd, &buffer, buffer.count)
            if count < 0, errno == EINTR { continue }
            guard count >= 0 else { throw AccountSwitchError.unsafeFile }
            if count == 0 { break }
            data.append(contentsOf: buffer.prefix(count))
            guard data.count <= 262_144 else { throw AccountSwitchError.unsafeFile }
        }
        return data
    }
}
