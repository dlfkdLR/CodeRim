import Foundation
import CSQLite

/// Desktop catalog entries identify remote tasks, not their runtime state.
/// Show a bounded recent list with an explicit unknown status until a live
/// remote collector can supply authoritative turn boundaries.
enum CodexRemoteCatalog {
    static func sessions(in url: URL, profile: CodexProfile = .default(),
                         now: Date = Date()) -> [AgentSession] {
        // The desktop catalog belongs to the default desktop profile. Alternate
        // profile databases can be copies and must not duplicate its remote rows.
        guard profile.slug == nil, let db = SQLiteStore.open(url) else { return [] }
        defer { sqlite3_close(db) }
        let rows = SQLiteStore.rows(in: db, sql: """
            SELECT c.host_id, c.thread_id, substr(c.display_title, 1, 161),
                   substr(c.cwd, 1, 4097), c.source_updated_at
            FROM local_thread_catalog c
            JOIN local_thread_catalog_hosts h ON h.host_id = c.host_id
            WHERE h.host_kind IN ('remote-control', 'ssh', 'wsl')
              AND c.missing_candidate = 0
              AND c.source_kind != 'subagent'
              AND c.source_updated_at >= CAST(? AS REAL)
              AND c.source_updated_at <= CAST(? AS REAL)
            ORDER BY c.source_updated_at DESC, c.host_id, c.thread_id
            LIMIT 6
            """, columns: 5, bindings: [String(now.addingTimeInterval(-6 * 3600).timeIntervalSince1970),
                                       String(now.timeIntervalSince1970)])
        return rows.compactMap { row in
            let host = row[0], thread = row[1]
            guard !host.isEmpty, host != "local", UUID(uuidString: thread) != nil,
                  let timestamp = Double(row[4]), timestamp.isFinite else { return nil }
            let title = label(row[2]) ?? "Task \(thread.prefix(8))"
            // Windows paths are not POSIX file URLs on this Mac.
            let folder = row[3].replacingOccurrences(of: "\\", with: "/")
                .split(separator: "/").last.map(String.init)
            let project = folder.flatMap(label).flatMap { CodexStore.Thread.isGeneric($0) ? nil : $0 }
                ?? "Remote task"
            return AgentSession(id: "\(profile.id).remote.\(host).\(thread)", name: CodexStore.Thread.isProjectlessWorkspace(row[3]) ? "" : project,
                detail: title, state: .unavailable, waitingFor: nil,
                since: Date(timeIntervalSince1970: timestamp), codexThreadID: thread,
                remoteHostID: host)
        }
    }

    private static func label(_ value: String) -> String? {
        let value = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty, value.count <= 160,
              !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
        else { return nil }
        return value
    }
}
