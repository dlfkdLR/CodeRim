import Foundation

enum CodexTurnActivity {
    struct Event: Equatable, Sendable {
        let isRunning: Bool
        let since: Date
    }

    /// Keep the last boundary while consuming only newly completed records.
    /// A fresh reader searches back to the boundary, however long the turn is.
    struct Reader {
        private struct Snapshot {
            let identity: String
            let modified: Date
            let size: UInt64
            let offset: UInt64
            let prefix: Data
            let anchor: Data
            let event: Event?
            let boundary: Range<UInt64>?
        }
        private var snapshot: Snapshot?

        mutating func read(_ url: URL) -> Event? {
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
                  let modified = attributes[.modificationDate] as? Date,
                  let size = (attributes[.size] as? NSNumber)?.uint64Value,
                  let device = attributes[.systemNumber] as? NSNumber,
                  let inode = attributes[.systemFileNumber] as? NSNumber,
                  let handle = try? FileHandle(forReadingFrom: url) else { return nil }
            defer { try? handle.close() }
            let identity = "\(device):\(inode)"
            do {
                var previous = snapshot
                if let saved = previous {
                    if saved.identity != identity || size < saved.size {
                        previous = nil
                    } else if size == saved.size {
                        if modified == saved.modified { return saved.event }
                        // Same-size rewrites are not appends.
                        previous = nil
                    } else if try bytes(handle, at: 0, count: saved.prefix.count) != saved.prefix
                                || bytes(handle, at: saved.offset - UInt64(saved.anchor.count),
                                         count: saved.anchor.count) != saved.anchor
                                || !boundaryMatches(saved, in: handle) {
                        // Validate the prior boundary as well as the ends when
                        // recovering from common in-place rewrites between polls.
                        previous = nil
                    }
                }
                let result = try scan(handle, from: previous?.offset ?? 0, through: size,
                                      includesFirstRecord: true)
                let offset = result.offset ?? previous?.offset ?? 0
                let event = result.event ?? previous?.event
                let prefix = try bytes(handle, at: 0, count: Int(min(size, 256)))
                let anchorSize = Int(min(offset, 256))
                let anchor = try bytes(handle, at: offset - UInt64(anchorSize), count: anchorSize)
                snapshot = Snapshot(identity: identity, modified: modified, size: size,
                                    offset: offset, prefix: prefix, anchor: anchor, event: event,
                                    boundary: result.boundary ?? previous?.boundary)
                return event
            } catch {
                // Leave the cursor untouched so a transient read failure is retried.
                return nil
            }
        }

        private func boundaryMatches(_ saved: Snapshot, in handle: FileHandle) throws -> Bool {
            guard let range = saved.boundary else { return true }
            return try CodexTurnActivity.event(in: bytes(handle, at: range.lowerBound,
                count: Int(range.count))) == saved.event
        }

    }

    /// Explicit limits are useful for diagnostics. Normal reads have no history
    /// cutoff: chunks bound scanning memory, not how long a turn remains visible.
    static func read(_ url: URL, maximumBytes: Int? = nil) -> Event? {
        guard maximumBytes.map({ $0 > 0 }) ?? true,
              let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        do {
            let end = try handle.seekToEnd()
            let start = maximumBytes.map { end - min(end, UInt64($0)) } ?? 0
            return try scan(handle, from: start, through: end,
                            includesFirstRecord: start == 0).event
        } catch { return nil }
    }

    private static let markers = ["\"task_started\"", "\"task_complete\"", "\"turn_aborted\""]
        .map { Data($0.utf8) }
    private static let chunkSize = 65_536
    /// The ceiling on a single JSONL record this reader will materialize. A
    /// real `task_started` / `task_complete` / `turn_aborted` line is well
    /// under a kilobyte; this is four orders of magnitude of headroom and only
    /// ever trips on a record that is not one of them.
    static let maximumRecordBytes = 4 * 1_024 * 1_024

    private static func bytes(_ handle: FileHandle, at offset: UInt64, count: Int) throws -> Data {
        try handle.seek(toOffset: offset)
        let data = try handle.read(upToCount: count) ?? Data()
        guard data.count == count else { throw CocoaError(.fileReadUnknown) }
        return data
    }

    /// Walk newline positions backwards without buffering enormous tool results.
    /// Only records containing a boundary marker need JSON decoding. `prefix`
    /// joins markers split across chunks; unfinished final records are ignored.
    private static func scan(_ handle: FileHandle, from lowerBound: UInt64, through end: UInt64,
                             includesFirstRecord: Bool) throws -> (event: Event?, offset: UInt64?, boundary: Range<UInt64>?) {
        var position = end
        var lineEnd: UInt64?
        var completedOffset: UInt64?
        var candidate = false
        var prefix = Data()
        func inspect(_ segment: Data.SubSequence) {
            let overlap = 15
            if !candidate {
                candidate = markers.contains { segment.range(of: $0) != nil }
                if !candidate, !prefix.isEmpty {
                    let boundary = Data(segment.suffix(overlap)) + prefix
                    candidate = markers.contains { boundary.range(of: $0) != nil }
                }
            }
            prefix = Data((Data(segment.prefix(overlap)) + prefix).prefix(overlap))
        }
        func decode(from start: UInt64, to finish: UInt64) throws -> Event? {
            guard candidate else { return nil }
            // A boundary record is a timestamp and a small payload. A record
            // far larger than that is a tool result that happens to quote one
            // of the markers — reviewing this very file would do it — and it
            // must not be read into memory whole just to be rejected by the
            // JSON parse. The old tail-based reader was capped at 8 MB for the
            // whole read; chunked scanning removed that ceiling, so the cap
            // belongs here, on the one place that still materializes a record.
            guard finish - start <= UInt64(maximumRecordBytes) else { return nil }
            return event(in: try bytes(handle, at: start, count: Int(finish - start)))
        }
        while position > lowerBound {
            let start = position - min(position - lowerBound, UInt64(chunkSize))
            let data = try bytes(handle, at: start, count: Int(position - start))
            var upper = data.endIndex
            while let newline = data[..<upper].lastIndex(of: 10) {
                let absolute = start + UInt64(newline)
                if let finish = lineEnd {
                    inspect(data[data.index(after: newline)..<upper])
                    if let event = try decode(from: absolute + 1, to: finish) {
                        return (event, completedOffset, (absolute + 1)..<finish)
                    }
                } else {
                    completedOffset = absolute + 1
                }
                lineEnd = absolute
                candidate = false
                prefix.removeAll(keepingCapacity: true)
                upper = newline
            }
            if lineEnd != nil { inspect(data[..<upper]) }
            position = start
        }
        if includesFirstRecord, let finish = lineEnd,
           let event = try decode(from: lowerBound, to: finish) {
            return (event, completedOffset, lowerBound..<finish)
        }
        return (nil, completedOffset, nil)
    }

    static func event(in line: Data) -> Event? {
        guard markers.contains(where: { line.range(of: $0) != nil }),
              let json = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
              json["type"] as? String == "event_msg",
              let payload = json["payload"] as? [String: Any],
              let type = payload["type"] as? String,
              ["task_started", "task_complete", "turn_aborted"].contains(type),
              let timestamp = json["timestamp"] as? String,
              let date = timestampDate(timestamp) else { return nil }
        return Event(isRunning: type == "task_started", since: date)
    }

    private static func timestampDate(_ value: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: value) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: value)
    }
}
