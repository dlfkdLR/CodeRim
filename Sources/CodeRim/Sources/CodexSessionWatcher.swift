import CoreServices
import Foundation

final class CodexSessionWatcher: @unchecked Sendable {
    private let roots: [URL]
    private let queue = DispatchQueue(label: "dev.codexmeter.session-watcher", qos: .utility)
    private let lock = NSLock()
    private var stream: FSEventStreamRef?
    private let continuation: AsyncStream<Void>.Continuation
    let events: AsyncStream<Void>

    init(roots: [URL]) {
        self.roots = roots
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
        )
        guard let created = FSEventStreamCreate(
            nil,
            { _, contextInfo, _, _, _, _ in
                guard let contextInfo else { return }
                let watcher = Unmanaged<CodexSessionWatcher>.fromOpaque(contextInfo).takeUnretainedValue()
                watcher.continuation.yield(())
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
