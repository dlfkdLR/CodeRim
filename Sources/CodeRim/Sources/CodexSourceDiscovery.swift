import Darwin
import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

struct CodexSessionSource: Equatable, Sendable {
    let url: URL
    let identity: String
    let size: Int64
    let modificationTimeNanoseconds: Int64
}

enum CodexSourceDiscoveryError: Error, LocalizedError {
    case sourceLimitExceeded(Int)

    var errorDescription: String? {
        switch self {
        case let .sourceLimitExceeded(limit):
            "More than \(limit.formatted()) Codex session files were found"
        }
    }
}

struct CodexSourceDiscovery: Sendable {
    private struct Entry {
        let path: String
        let source: CodexSessionSource
    }

    static let maximumSourceCount = 50_000

    func defaultRoots(homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser) -> [URL] {
        let codex = homeDirectory.appendingPathComponent(".codex", isDirectory: true)
        return [
            codex.appendingPathComponent("sessions", isDirectory: true),
            codex.appendingPathComponent("archived_sessions", isDirectory: true)
        ]
    }

    func discover(
        in roots: [URL],
        maximumSourceCount: Int = Self.maximumSourceCount
    ) throws -> [CodexSessionSource] {
        guard maximumSourceCount > 0 else {
            throw CodexSourceDiscoveryError.sourceLimitExceeded(maximumSourceCount)
        }
        var discovered: [Entry] = []
        let keys: [URLResourceKey] = [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey]

        for root in roots {
            let rootPath = root.standardizedFileURL.path
            let rootPrefix = rootPath + "/"
            guard FileManager.default.fileExists(atPath: rootPath) else { continue }
            guard let enumerator = FileManager.default.enumerator(
                at: root,
                includingPropertiesForKeys: keys,
                options: [.skipsHiddenFiles, .skipsPackageDescendants]
            ) else { continue }

            for case let candidate as URL in enumerator {
                try Task.checkCancellation()
                let values = try candidate.resourceValues(forKeys: Set(keys))
                if values.isSymbolicLink == true {
                    if values.isDirectory == true { enumerator.skipDescendants() }
                    continue
                }
                guard values.isRegularFile == true, candidate.pathExtension == "jsonl" else { continue }

                let standardized = candidate.standardizedFileURL
                let path = standardized.path
                guard path == rootPath || path.hasPrefix(rootPrefix) else {
                    continue
                }
                guard let stat = fileStat(at: path), stat.isRegular else { continue }
                guard discovered.count < maximumSourceCount else {
                    throw CodexSourceDiscoveryError.sourceLimitExceeded(maximumSourceCount)
                }
                let source = CodexSessionSource(
                    url: standardized,
                    identity: "\(stat.device):\(stat.inode)",
                    size: stat.size,
                    modificationTimeNanoseconds: stat.modificationTimeNanoseconds
                )
                discovered.append(Entry(path: path, source: source))
            }
        }

        #if CODERIM_RUST_ACCOUNTING
        if let ordered = Self.orderInRust(discovered) { return ordered }
        #endif
        return discovered.sorted { $0.path < $1.path }.map(\.source)
    }

    #if CODERIM_RUST_ACCOUNTING
    /// Foundation supplies the normalized file paths. Rust orders large batches
    /// whose varying suffixes are ASCII. Swift compares the other Unicode names.
    private static func orderInRust(_ entries: [Entry]) -> [CodexSessionSource]? {
        guard (64...maximumSourceCount).contains(entries.count) else { return nil }
        let byteLimit = 8 * 1024 * 1024
        var bytes = [UInt8]()
        bytes.reserveCapacity(min(byteLimit, entries.count * 96))
        for entry in entries {
            let pathBytes = entry.path.utf8
            guard pathBytes.count < byteLimit - bytes.count else { return nil }
            bytes.append(contentsOf: pathBytes)
            bytes.append(0)
        }
        var indices = [UInt32](repeating: 0, count: entries.count)
        guard cr_order_session_paths(bytes, bytes.count, &indices, indices.count) != 0 else { return nil }
        return indices.map { entries[Int($0)].source }
    }
    #endif

    private func fileStat(at path: String) -> (
        device: UInt64,
        inode: UInt64,
        size: Int64,
        modificationTimeNanoseconds: Int64,
        isRegular: Bool
    )? {
        var value = stat()
        guard lstat(path, &value) == 0 else { return nil }
        let fileType = value.st_mode & S_IFMT
        return (
            device: UInt64(value.st_dev),
            inode: UInt64(value.st_ino),
            size: Int64(value.st_size),
            modificationTimeNanoseconds: Int64(value.st_mtimespec.tv_sec) * 1_000_000_000
                + Int64(value.st_mtimespec.tv_nsec),
            isRegular: fileType == S_IFREG
        )
    }
}
