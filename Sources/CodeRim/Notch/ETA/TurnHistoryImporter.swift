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
        for file in logFiles(under: projects, within: lookback, now: now, fileManager: fileManager) {
            forEachLine(of: file, containing: "\"turn_duration\"") { line in
                if let sample = claudeSample(fromLine: line) { samples.append(sample) }
            }
        }
        return samples
    }

    static func claudeSample(fromLine line: Data) -> TurnHistory.Sample? {
        guard let json = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
              json["type"] as? String == "system",
              json["subtype"] as? String == "turn_duration",
              json["isSidechain"] as? Bool != true,
              let millis = (json["durationMs"] as? NSNumber)?.doubleValue,
              let stamp = json["timestamp"] as? String,
              let date = parse(stamp) else { return nil }
        let folder = ((json["cwd"] as? String) as NSString?)?.lastPathComponent ?? ""
        return TurnHistory.Sample(seconds: millis / 1000, project: folder, recordedAt: date)
    }

    // MARK: - Codex

    static func codexSamples(sessions: URL, now: Date = Date(),
                             fileManager: FileManager = .default) -> [TurnHistory.Sample] {
        var samples: [TurnHistory.Sample] = []
        for file in logFiles(under: sessions, within: lookback, now: now, fileManager: fileManager) {
            // The folder is stated once near the top (`session_meta` /
            // `turn_context`), the duration at the end of each turn.
            var folder = ""
            forEachLine(of: file, containing: nil) { line in
                guard let text = String(data: line, encoding: .utf8) else { return }
                if text.contains("\"task_complete\"") {
                    if let sample = codexSample(fromLine: line, project: folder) { samples.append(sample) }
                } else if text.contains("\"cwd\""),
                          text.contains("\"session_meta\"") || text.contains("\"turn_context\""),
                          let cwd = codexCWD(fromLine: line) {
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
    /// `needle` (every line when nil), so a 20 MB transcript is not parsed in
    /// full to find a few dozen records.
    static func forEachLine(of url: URL, containing needle: String?, _ body: (Data) -> Void) {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return }
        defer { try? handle.close() }
        let needleData = needle.map { Data($0.utf8) }
        var carry = Data()
        let newline = UInt8(ascii: "\n")
        while let chunk = try? handle.read(upToCount: 1 << 20), !chunk.isEmpty {
            carry.append(chunk)
            while let end = carry.firstIndex(of: newline) {
                let line = carry[carry.startIndex..<end]
                if needleData == nil || line.range(of: needleData!) != nil { body(Data(line)) }
                carry = Data(carry[carry.index(after: end)...])
            }
        }
        if !carry.isEmpty, needleData == nil || carry.range(of: needleData!) != nil { body(carry) }
    }

    static func parse(_ stamp: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: stamp) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: stamp)
    }
}
