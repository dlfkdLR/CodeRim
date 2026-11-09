import CSQLite
import Foundation

/// Reads how long past turns took out of the agents' own logs, so the first
/// estimate on a fresh install is already built on the user's habits.
///
/// Both agents record the answer themselves: Claude Code writes a
/// `turn_duration` record at the end of every turn, and Codex writes
/// `task_complete` with `duration_ms`. Only those two numbers and the folder
/// name are taken. No prompt, reply or path ever leaves the log.
enum TurnHistoryImporter {
    /// Logs untouched for longer than this say little about how the user
    /// works now, and scanning a year of transcripts costs real time.
    static let lookback: TimeInterval = 60 * 24 * 3600
    /// Per file: the largest transcript this will read through.
    static let fileLimit: UInt64 = 256 * 1024 * 1024

    // MARK: - Claude

    static func claudeSamples(projects: URL, now: Date = Date(),
                              fileManager: FileManager = .default) -> [TurnHistory.Sample] {
        var samples: [TurnHistory.Sample] = []
        let turnEnd = Data("\"turn_duration\"".utf8)
        let sidechain = Data("\"isSidechain\":true".utf8)
        for file in logFiles(under: projects, within: lookback, now: now, fileManager: fileManager) {
            // Tool calls are counted from the bytes of the records that make
            // them, and handed to the turn that ends next.
            var calls = 0
            // Screen bounded lines as bytes; only the turn's end needs JSON parsing.
            forEachLine(of: file, containingAny: ["\"turn_duration\"", Self.toolUseNeedle], copying: false) { line in
                if line.range(of: turnEnd) != nil {
                    if let sample = claudeSample(fromLine: Data(line), toolCalls: calls) { samples.append(sample) }
                    calls = 0
                } else if line.range(of: sidechain) == nil {
                    calls += occurrences(of: Self.toolUseNeedle, in: line)
                }
            }
        }
        return samples
    }

    /// One tool call in a Claude transcript.
    static let toolUseNeedle = "\"type\":\"tool_use\""

    static func occurrences(of needle: String, in data: Data) -> Int {
        let pattern = Data(needle.utf8)
        var count = 0
        var from = data.startIndex
        while let found = data.range(of: pattern, in: from..<data.endIndex) {
            count += 1
            from = found.upperBound
        }
        return count
    }

    static func claudeSample(fromLine line: Data, toolCalls: Int? = nil) -> TurnHistory.Sample? {
        guard let json = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
              json["type"] as? String == "system",
              json["subtype"] as? String == "turn_duration",
              json["isSidechain"] as? Bool != true,
              let millis = (json["durationMs"] as? NSNumber)?.doubleValue,
              let stamp = json["timestamp"] as? String,
              let date = parse(stamp) else { return nil }
        let folder = ((json["cwd"] as? String) as NSString?)?.lastPathComponent ?? ""
        return TurnHistory.Sample(seconds: millis / 1000, project: folder, recordedAt: date, toolCalls: toolCalls)
    }

    // MARK: - Codex

    static func codexSamples(sessions: URL, now: Date = Date(),
                             fileManager: FileManager = .default) -> [TurnHistory.Sample] {
        var samples: [TurnHistory.Sample] = []
        for file in logFiles(under: sessions, within: lookback, now: now, fileManager: fileManager) {
            // The folder is stated once near the top (`session_meta` /
            // `turn_context`), the duration at the end of each turn.
            var folder = ""
            forEachLine(of: file, containingAny: ["\"task_complete\"", "\"session_meta\"", "\"turn_context\""]) { line in
                if line.range(of: Data("\"task_complete\"".utf8)) != nil {
                    if let sample = codexSample(fromLine: line, project: folder) { samples.append(sample) }
                } else if let cwd = codexCWD(fromLine: line) {
                    folder = (cwd as NSString).lastPathComponent
                }
            }
        }
        return samples
    }

    static func codexSample(fromLine line: Data, project: String) -> TurnHistory.Sample? {
        guard let json = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
              let payload = json["payload"] as? [String: Any],
              payload["type"] as? String == "task_complete",
              let millis = (payload["duration_ms"] as? NSNumber)?.doubleValue,
              let stamp = json["timestamp"] as? String,
              let date = parse(stamp) else { return nil }
        return TurnHistory.Sample(seconds: millis / 1000, project: project, recordedAt: date)
    }

    static func codexCWD(fromLine line: Data) -> String? {
        guard let json = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
              let payload = json["payload"] as? [String: Any] else { return nil }
        return payload["cwd"] as? String
    }

    // MARK: - OpenCode

    /// One turn per user message: from when it was sent to the last step the
    /// assistant finished in answer to it.
    static func openCodeSamples(database: URL, now: Date = Date()) -> [TurnHistory.Sample] {
        guard let db = SQLiteStore.open(database) else { return [] }
        defer { sqlite3_close(db) }
        let cutoff = Int((now.timeIntervalSince1970 - lookback) * 1000)
        let rows = SQLiteStore.rows(in: db, sql: """
            SELECT s.directory, json_extract(u.data,'$.time.created'),
                   MAX(json_extract(a.data,'$.time.completed'))
            FROM message u
            JOIN message a ON json_extract(a.data,'$.parentID') = u.id
            JOIN session s ON s.id = u.session_id
            WHERE json_extract(u.data,'$.role') = 'user'
              AND json_extract(a.data,'$.time.completed') IS NOT NULL
              AND u.time_created > ?
            GROUP BY u.id
            """, columns: 3, bindings: [String(cutoff)])
        return rows.compactMap { row in
            guard let started = Double(row[1]), let ended = Double(row[2]), ended > started else { return nil }
            return TurnHistory.Sample(seconds: (ended - started) / 1000,
                                      project: (row[0] as NSString).lastPathComponent,
                                      recordedAt: Date(timeIntervalSince1970: ended / 1000))
        }
    }

    // MARK: - Files

    /// Every `.jsonl` under `root` modified within `window`, newest last.
    static func logFiles(under root: URL, within window: TimeInterval, now: Date,
                         fileManager: FileManager) -> [URL] {
        guard let walker = fileManager.enumerator(
            at: root, includingPropertiesForKeys: [.contentModificationDateKey, .fileSizeKey],
            options: [.skipsHiddenFiles]) else { return [] }
        var found: [(URL, Date)] = []
        for case let url as URL in walker where url.pathExtension == "jsonl" {
            let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey])
            guard let modified = values?.contentModificationDate,
                  now.timeIntervalSince(modified) <= window,
                  UInt64(values?.fileSize ?? 0) <= fileLimit else { continue }
            found.append((url, modified))
        }
        return found.sorted { $0.1 < $1.1 }.map(\.0)
    }

    /// Streams a file line by line, handing over only the lines that contain
    /// any of `needles`, so a 20 MB transcript is not parsed in full to find a
    /// few dozen records. Only complete, bounded lines are emitted; an oversized
    /// record is skipped through its newline before matching later records.
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
            }
        }
    }
}
