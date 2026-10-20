import CoreServices
import Foundation

final class CodexSessionWatcher: @unchecked Sendable {
    private let roots: [URL]
    private let sourceRoots: [String]
    private let queue = DispatchQueue(label: "dev.coderim.session-watcher", qos: .utility)
    private let lock = NSLock()
    private var stream: FSEventStreamRef?
    private let continuation: AsyncStream<Void>.Continuation
    let events: AsyncStream<Void>

    init(roots: [URL], sourceRoots: [URL]? = nil) {
        self.roots = roots
        self.sourceRoots = Array(Set((sourceRoots ?? roots).flatMap {
            [$0.standardizedFileURL.path, $0.resolvingSymlinksInPath().standardizedFileURL.path]
        }))
        let pair = AsyncStream<Void>.makeStream(bufferingPolicy: .bufferingNewest(1))
        events = pair.stream
        continuation = pair.continuation
    }

    func start() {
        lock.lock()
        defer { lock.unlock() }
        guard stream == nil else { return }

        var context = FSEventStreamContext(
            version: 0,
            info: Unmanaged.passUnretained(self).toOpaque(),
            retain: { pointer in
                guard let pointer else { return nil }
                _ = Unmanaged<CodexSessionWatcher>.fromOpaque(pointer).retain()
                return UnsafeRawPointer(pointer)
            },
            release: { pointer in
                guard let pointer else { return }
                Unmanaged<CodexSessionWatcher>.fromOpaque(pointer).release()
            },
            copyDescription: nil
        )
        let paths = roots.map(\.path) as CFArray
        let flags = FSEventStreamCreateFlags(
            kFSEventStreamCreateFlagFileEvents
                | kFSEventStreamCreateFlagWatchRoot
                | kFSEventStreamCreateFlagNoDefer
                | kFSEventStreamCreateFlagUseCFTypes
        )
        guard let created = FSEventStreamCreate(
            nil,
            { _, contextInfo, count, paths, eventFlags, _ in
                guard let contextInfo else { return }
                let watcher = Unmanaged<CodexSessionWatcher>.fromOpaque(contextInfo).takeUnretainedValue()
                let changedPaths = Unmanaged<CFArray>.fromOpaque(paths).takeUnretainedValue() as! [String]
                for index in 0..<count {
                    if watcher.shouldRefresh(path: changedPaths[index], flags: eventFlags[index]) {
                        watcher.continuation.yield(())
                        break
                    }
                }
            },
            &context,
            paths,
            FSEventStreamEventId(kFSEventStreamEventIdSinceNow),
            0.25,
            flags
        ) else { return }

        FSEventStreamSetDispatchQueue(created, queue)
        guard FSEventStreamStart(created) else {
            FSEventStreamInvalidate(created)
            FSEventStreamRelease(created)
            return
        }
        stream = created
    }

    /// Parent directories are watched so a missing source root can appear.
    /// Editor databases, auth files and logs beside those roots are not usage.
    func shouldRefresh(path: String, flags: FSEventStreamEventFlags) -> Bool {
        let rescanFlags = FSEventStreamEventFlags(
            kFSEventStreamEventFlagMustScanSubDirs | kFSEventStreamEventFlagUserDropped
                | kFSEventStreamEventFlagKernelDropped | kFSEventStreamEventFlagEventIdsWrapped
                | kFSEventStreamEventFlagRootChanged
                | kFSEventStreamEventFlagMount | kFSEventStreamEventFlagUnmount
        )
        if flags & rescanFlags != 0 { return true }
        let url = URL(fileURLWithPath: path)
        let paths = [url.standardizedFileURL.path, url.resolvingSymlinksInPath().standardizedFileURL.path]
        return paths.contains { path in
            sourceRoots.contains { root in
                path == root || path.hasPrefix(root + "/") || root.hasPrefix(path + "/")
            }
        }
    }

    func stop() {
        lock.lock()
        let active = stream
        stream = nil
        lock.unlock()

        if let active {
            FSEventStreamStop(active)
            FSEventStreamInvalidate(active)
            FSEventStreamRelease(active)
        }
        continuation.finish()
    }

    deinit {
        stop()
    }
}
