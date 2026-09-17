import Foundation

enum CodexTurnActivity {
    struct Event: Equatable, Sendable {
        let isRunning: Bool
        let since: Date
    }

    /// Scan backwards in bounded chunks, including past large tool results.
    static func read(_ url: URL, maximumBytes: Int = 8 * 1_024 * 1_024) -> Event? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let end = try? handle.seekToEnd() else { return nil }
        var offset = end
        var remainder = Data()
        var discardingPartialEnd = true
        let lowerBound = end > UInt64(maximumBytes) ? end - UInt64(maximumBytes) : 0
        while offset > lowerBound {
            let start = max(lowerBound, offset > 65_536 ? offset - 65_536 : 0)
            guard (try? handle.seek(toOffset: start)) != nil,
                  let chunk = try? handle.read(upToCount: Int(offset - start)) else { return nil }
            var data = chunk
            data.append(remainder)
            if discardingPartialEnd {
                guard let newline = data.lastIndex(of: 10) else {
                    remainder = data
                    offset = start
                    continue
                }
                data = Data(data[..<newline])
                discardingPartialEnd = false
            }
            let lines = data.split(separator: 10, omittingEmptySubsequences: false)
            for line in lines.dropFirst().reversed() {
                if let event = event(in: Data(line)) { return event }
            }
            remainder = lines.first.map { Data($0) } ?? Data()
            offset = start
        }
        return lowerBound == 0 && !discardingPartialEnd ? event(in: remainder) : nil
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
