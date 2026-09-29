import CSQLite
import CryptoKit
import Foundation

/// Reuse imported, deduplicated usage rather than summing cumulative transcript counters.
enum SessionTokenReader {
    static func totals(for sessions: [AgentSession], provider: UsageProvider,
                       databaseURL: URL) -> [String: Int64] {
        let roots = ActivitySummary(sessions: sessions)?.displayRows.filter { $0.depth == 0 } ?? []
        let identities: [(row: String, stored: String)] = roots.compactMap { row in
            let session = row.session
            guard !session.isRemote else { return nil }
            let raw = provider == .codex ? session.codexThreadID : session.usageSessionID
            guard let raw, !raw.isEmpty, raw.utf8.count <= 256 else { return nil }
            let material = provider == .codex ? raw : "claude-session|\(raw)"
            let stored = SHA256.hash(data: Data(material.utf8)).map { String(format: "%02x", $0) }.joined()
            return (session.id, stored)
        }
        guard !identities.isEmpty, let db = SQLiteStore.open(databaseURL) else { return [:] }
        defer { sqlite3_close(db) }
        sqlite3_busy_timeout(db, 100)
        let ids = Array(Set(identities.map(\.stored))).sorted()
        var measured: [String: Int64] = [:]
        for start in stride(from: 0, to: ids.count, by: 128) {
            let chunk = Array(ids[start..<min(start + 128, ids.count)])
            let marks = Array(repeating: "(?)", count: chunk.count).joined(separator: ",")
            let rows = SQLiteStore.rows(in: db, sql: """
                WITH RECURSIVE family(root_id, session_id) AS (
                    SELECT column1, column1 FROM (VALUES \(marks))
                    WHERE NOT EXISTS (
                        SELECT 1 FROM session_metadata m WHERE m.session_id = column1
                          AND m.parent_session_id IS NOT NULL AND m.parent_session_id != ''
                    )
                    UNION
                    SELECT f.root_id, m.session_id FROM family f
                    JOIN session_metadata m ON m.parent_session_id = f.session_id
                )
                SELECT f.root_id, SUM(e.input_tokens), SUM(e.output_tokens)
                FROM family f JOIN usage_events e ON e.session_id = f.session_id
                GROUP BY f.root_id
                """, columns: 3, bindings: chunk)
            for row in rows {
                guard let input = Int64(row[1]), let output = Int64(row[2]), input >= 0, output >= 0 else { continue }
                let sum = input.addingReportingOverflow(output)
                guard !sum.overflow else { continue }
                measured[row[0]] = sum.partialValue
            }
        }
        return Dictionary(identities.compactMap { identity in
            measured[identity.stored].map { (identity.row, $0) }
        }, uniquingKeysWith: { first, _ in first })
    }

    static func text(_ total: Int64?, enabled: Bool) -> String? {
        guard enabled, let total, total >= 0 else { return nil }
        return TokenFormatter().string(from: total, style: .compact) + " tokens"
    }
}
