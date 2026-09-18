import Darwin
import Foundation

/// Reads a borrowed credential file the way the account code reads
/// `auth.json`, rather than the way a config file gets read.
///
/// Every provider here borrows a session another tool wrote to disk —
/// `~/.grok/auth.json`, `~/.config/gh/hosts.yml`, ZCode's `config.json`.
/// `Data(contentsOf:)` reads those through whatever the path happens to point
/// at, with no ceiling: a symlink aimed elsewhere is followed silently, and a
/// file that is large (by accident or otherwise) is loaded whole, on a timer.
///
/// `CodexLoginFile` and `ClaudeProfileFile` already answer this for the two
/// logins CodeRim can *write*. The files it only reads deserve the same
/// treatment, because the consequence of getting it wrong is the same: a token
/// is lifted out and sent to a vendor.
///
/// What it checks, all against the descriptor it will actually read from —
/// never against the path, which could change underneath a second look:
///
/// - `O_NOFOLLOW`, so the final path component is not a symlink.
/// - A regular file owned by this user. Another account's file is not ours to
///   read, and a fifo or device would block or lie.
/// - A size ceiling, so a runaway file fails this one poll instead of the app.
///
/// Deliberately *not* checked: the mode bits. These files belong to other
/// tools, which write them with their own umask — `hosts.yml` is commonly
/// `0644` — and refusing those would disable the feature for most users while
/// protecting a secret that its owner already chose to leave readable.
enum CredentialFileReader {
    /// Generous next to the few kilobytes these files actually hold.
    static let defaultMaximumBytes = 1_048_576

    /// Nil when the file is missing, unreadable, or fails a check above.
    /// Callers treat that as "this provider is not configured", which is what
    /// they already did with a failed `Data(contentsOf:)`.
    static func data(at url: URL, maximumBytes: Int = defaultMaximumBytes) -> Data? {
        let fd = open(url.path, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { return nil }
        defer { close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG,
              info.st_uid == getuid(), info.st_size >= 0, info.st_size <= maximumBytes
        else { return nil }

        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 8_192)
        while true {
            let count = Darwin.read(fd, &buffer, buffer.count)
            if count < 0, errno == EINTR { continue }
            guard count >= 0 else { return nil }
            if count == 0 { break }
            data.append(contentsOf: buffer.prefix(count))
            guard data.count <= maximumBytes else { return nil }
        }
        return data
    }

    /// The same read, decoded as UTF-8, for the one source that is not JSON.
    static func text(at url: URL, maximumBytes: Int = defaultMaximumBytes) -> String? {
        data(at: url, maximumBytes: maximumBytes).flatMap { String(data: $0, encoding: .utf8) }
    }

    /// The shape every one of these files actually has.
    static func jsonObject(at url: URL, maximumBytes: Int = defaultMaximumBytes) -> [String: Any]? {
        guard let data = data(at: url, maximumBytes: maximumBytes),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return nil }
        return object
    }
}
