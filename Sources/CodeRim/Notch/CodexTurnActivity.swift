import Foundation

enum CodexTurnActivity {
    struct Event: Equatable, Sendable {
        let isRunning: Bool
        let since: Date
    }

    /// Read a bounded tail once and visit each newline once, even when one
    /// tool result spans most of the tail. Ignore unfinished final records.
    static func read(_ url: URL, maximumBytes: Int = 8 * 1_024 * 1_024) -> Event? {
        guard maximumBytes > 0,
              let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let end = try? handle.seekToEnd() else { return nil }
        let count = min(end, UInt64(maximumBytes))
        let start = end - count
        guard (try? handle.seek(toOffset: start)) != nil,
              let data = try? handle.read(upToCount: Int(count)),
              var lineEnd = data.lastIndex(of: 10) else { return nil }
        while let previousNewline = data[..<lineEnd].lastIndex(of: 10) {
            let line = data[data.index(after: previousNewline)..<lineEnd]
            if let event = event(in: line) { return event }
            lineEnd = previousNewline
        }
        // A tail starting inside a record cannot establish that record's event.
        return start == 0 ? event(in: data[..<lineEnd]) : nil
    }

    static func event(in line: Data) -> Event? {
        guard ["\"task_started\"", "\"task_complete\"", "\"turn_aborted\""]
            .contains(where: { line.range(of: Data($0.utf8)) != nil }),
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
