import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

/// Bounded JSONL reads shared by activity and session monitors.
enum TranscriptLines {
    static let fileLimit: UInt64 = 256 * 1024 * 1024

    /// Emit matching complete lines only; oversized records are skipped through their newline.
    static func forEachLine(of url: URL, containingAny needles: [String], copying: Bool = true,
                            _ body: (Data) -> Void) {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return }
        defer { try? handle.close() }
        let needleData = needles.map { Data($0.utf8) }
        func matches(_ line: Data) -> Bool { needleData.contains { line.range(of: $0) != nil } }
        var cursor = TranscriptLineCursor()
        guard let size = try? handle.seekToEnd() else { return }
        try? cursor.read(handle, through: min(size, fileLimit)) { line in
            if matches(line) { body(copying ? Data(line) : line) }
        }
    }

    static func parse(_ stamp: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: stamp) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: stamp)
    }
}

/// Keeps consumed bytes separate from the last completed line, so unfinished
/// appends are retained once and oversized records are discarded through newline.
struct TranscriptLineCursor {
    static let chunkBytes = 64 * 1024
    // shortcut: records over 1 MiB are skipped, raise only for a supported record needing more.
    static let lineBytes = 1024 * 1024
    private(set) var offset: UInt64 = 0
    private(set) var readOffset: UInt64 = 0
    private(set) var carry = Data()
    private(set) var discarding = false

    mutating func read(_ handle: FileHandle, through end: UInt64, _ body: (Data) -> Void) throws {
        defer { if carry.isEmpty { carry = Data() } }
        try handle.seek(toOffset: readOffset)
        while readOffset < end {
            guard !Task.isCancelled else { return }
            let chunk = try handle.read(upToCount: Int(min(UInt64(Self.chunkBytes), end - readOffset))) ?? Data()
            guard !chunk.isEmpty else { return }
            autoreleasepool {
                #if CODERIM_RUST_ACCOUNTING
                consume(chunk, body)
                #else
                var start = chunk.startIndex
                while start < chunk.endIndex {
                    let newline = chunk[start...].firstIndex(of: 0x0A)
                    let stop = newline ?? chunk.endIndex
                    let part = chunk[start..<stop]
                    if !discarding {
                        if carry.count + part.count <= Self.lineBytes { carry.append(part) }
                        else { carry = Data(); discarding = true }
                    }
                    readOffset += UInt64(part.count)
                    if let newline {
                        readOffset += 1
                        offset = readOffset
                        if !discarding { body(carry) }
                        carry.removeAll(keepingCapacity: true)
                        discarding = false
                        start = chunk.index(after: newline)
                    } else { break }
                }
                #endif
            }
        }
    }
    #if CODERIM_RUST_ACCOUNTING
    private mutating func consume(_ chunk: Data, _ body: (Data) -> Void) {
        chunk.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            let bytes = raw.bindMemory(to: UInt8.self)
            guard let base = bytes.baseAddress else { return }
            withUnsafeTemporaryAllocation(of: CRTranscriptSpan.self, capacity: 128) { spans in
                spans.initialize(repeating: CRTranscriptSpan())
                defer { spans.deinitialize() }
                var consumed = 0
                while consumed < bytes.count {
                    let batch = consumed
                    let count = cr_transcript_plan(base + batch, bytes.count - batch, carry.count,
                                                   discarding, Self.lineBytes, spans.baseAddress, spans.count)
                    precondition(count > 0)
                    for span in spans.prefix(count) {
                        let end = batch + span.end
                        let length = end - consumed
                        let complete = span.action == CR_TRANSCRIPT_EMIT || span.action == CR_TRANSCRIPT_SKIP
                        let shouldKeep = span.action == CR_TRANSCRIPT_EMIT || span.action == CR_TRANSCRIPT_CARRY
                        if shouldKeep {
                            carry.append(base + consumed, count: length)
                        } else if !carry.isEmpty {
                            carry = Data()
                        }
                        consumed = end + (complete ? 1 : 0)
                        readOffset += UInt64(length) + (complete ? 1 : 0)
                        if complete {
                            offset = readOffset
                            if shouldKeep { body(carry) }
                            carry.removeAll(keepingCapacity: true)
                        }
                        discarding = span.action == CR_TRANSCRIPT_DISCARD
                    }
                }
            }
        }
    }
    #endif

}
