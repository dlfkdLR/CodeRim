import Darwin
import Foundation

/// An unlinked COW clone pins one generation of a growing transcript. No raw
/// transcript path is left behind, even if the process exits during a scan.
final class SourceReadSnapshot: @unchecked Sendable {
    let handle: FileHandle
    let metadata: stat

    private init(handle: FileHandle, metadata: stat) {
        self.handle = handle
        self.metadata = metadata
    }
    deinit { try? handle.close() }

    func accepts(_ current: stat) -> Bool {
        guard current.st_dev == metadata.st_dev, current.st_ino == metadata.st_ino,
              current.st_size >= metadata.st_size else { return false }
        if current.st_size > metadata.st_size { return true }
        return Self.sameVersion(current, metadata)
    }

    private static func sameVersion(_ a: stat, _ b: stat) -> Bool {
        a.st_dev == b.st_dev && a.st_ino == b.st_ino && a.st_size == b.st_size
            && a.st_mtimespec.tv_sec == b.st_mtimespec.tv_sec
            && a.st_mtimespec.tv_nsec == b.st_mtimespec.tv_nsec
            && a.st_ctimespec.tv_sec == b.st_ctimespec.tv_sec
            && a.st_ctimespec.tv_nsec == b.st_ctimespec.tv_nsec
    }

    static func capture(descriptor: Int32, metadata: stat) -> SourceReadSnapshot? {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodeRimRead-\(UUID().uuidString)", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false,
                                                    attributes: [.posixPermissions: 0o700])
        } catch { return nil }
        defer { try? FileManager.default.removeItem(at: directory) }
        let path = directory.appendingPathComponent("source").path
        // No unbounded copy fallback: unsupported filesystems retain the strict
        // live-file verification path and the collector's fair source rotation.
        guard fclonefileat(descriptor, AT_FDCWD, path, 0) == 0 else { return nil }
        let copied = Darwin.open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW)
        guard copied >= 0 else { return nil }
        _ = unlink(path)
        var after = stat()
        var frozen = stat()
        guard fstat(descriptor, &after) == 0, sameVersion(metadata, after),
              fstat(copied, &frozen) == 0, frozen.st_size == metadata.st_size else {
            close(copied); return nil
        }
        return SourceReadSnapshot(handle: FileHandle(fileDescriptor: copied, closeOnDealloc: true), metadata: metadata)
    }
}
