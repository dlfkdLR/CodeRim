import Foundation

/// Each destination owns a serial I/O queue. A blocked widget container must never
/// block the main actor or the CLI export; at most one newer snapshot waits behind it.
public final class CompanionSnapshotWriter: @unchecked Sendable {
    public typealias Write = @Sendable (CompanionSnapshot) throws -> Void
    public typealias Completion = @Sendable (CompanionSnapshot, Bool) -> Void
    private let queue: DispatchQueue
    private let lock = NSLock()
    private var pending: CompanionSnapshot?
    private var running = false
    // Only touched by this writer's serial queue, and updated only after successful I/O.
    private var lastWritten: CompanionSnapshot?
    private let write: Write
    private let completion: Completion

    public init(label: String, write: @escaping Write, completion: @escaping Completion = { _, _ in }) {
        queue = DispatchQueue(label: label, qos: .utility)
        self.write = write
        self.completion = completion
    }

    public func submit(_ snapshot: CompanionSnapshot) {
        let start = lock.withLock {
            pending = snapshot
            guard !running else { return false }
            running = true
            return true
        }
        if start { queue.async { self.drain() } }
    }

    private func drain() {
        while let snapshot = takeNext() {
            guard snapshot.providers != lastWritten?.providers else { continue }
            do {
                try write(snapshot)
                lastWritten = snapshot
                completion(snapshot, true)
            } catch {
                // Do not advance deduplication after failure; the same value can retry.
                completion(snapshot, false)
            }
        }
    }

    private func takeNext() -> CompanionSnapshot? {
        lock.withLock {
            guard let value = pending else { running = false; return nil }
            pending = nil
            return value
        }
    }
}
