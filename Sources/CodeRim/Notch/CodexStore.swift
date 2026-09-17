import Foundation
import CSQLite

enum CodexStore {
    static var stateURL: URL {
        CodexProfile.default().stateURL
    }

    /// Optional desktop titles supplement the local rollout catalogue.
    static var desktopStoreURL: URL {
        CodexProfile.default().desktopStoreURL
    }

    struct Thread: Sendable {
        let id: String
        let rollout: URL
        let cwd: String
        let title: String?
        let savedProjectName: String?
        let agentName: String?
        let parentThread: AgentSession.ParentThread?

        var displayTitle: String { title ?? agentName ?? "Task \(id.prefix(8))" }
        var projectName: String {
            if let savedProjectName { return savedProjectName }
            let folder = URL(fileURLWithPath: cwd).lastPathComponent
            return Self.isGeneric(folder) ? displayTitle : folder
        }

        static func isGeneric(_ name: String) -> Bool {
            ["", "/", "codex", ".codex", "unknown project"].contains(name.lowercased())
        }
    }

    static func threads(in url: URL, desktopStore: URL? = nil,
                        limit: Int? = nil, includeArchived: Bool = false) -> [Thread] {
        guard let db = SQLiteStore.open(url) else { return [] }
        defer { sqlite3_close(db) }
        let columns = Set(SQLiteStore.rows(in: db, sql: "PRAGMA table_info(threads)", columns: 6).map { $0[1] })
        guard columns.contains("rollout_path") else { return [] }
        let name = columns.contains("name") ? "substr(name, 1, 161)" : "NULL"
        let order = columns.contains("updated_at_ms") ? "COALESCE(updated_at_ms, updated_at * 1000)" : "updated_at"
        let project = columns.contains("project_id") ? "project_id" : "NULL"
        let source = columns.contains("source") ? "substr(source, 1, 8193)" : "NULL"
        let nickname = columns.contains("agent_nickname") ? "substr(agent_nickname, 1, 161)" : "NULL"
        let archiveFilter = includeArchived ? "" : "WHERE archived = 0"
        let bound = limit.map { " LIMIT \(max(1, $0))" } ?? ""
        let rows = SQLiteStore.rows(
            in: db,
            sql: "SELECT id, rollout_path, cwd, \(name), substr(title, 1, 161), \(project), \(source), \(nickname) FROM threads \(archiveFilter) ORDER BY \(order) DESC\(bound)",
            columns: 8
        )
        let projectRows = SQLiteStore.rows(in: db, sql: "SELECT id, name FROM projects", columns: 2)
        let projects = Dictionary(projectRows.compactMap { row in
            cleanTitle(row[1]).map { (row[0], $0) }
        }, uniquingKeysWith: { first, _ in first })
        let desktopTitles = desktopStore.map(titles(in:)) ?? [:]
        let spawns = rows.map { spawn(in: $0[6], excluding: $0[0]) }
        let agentNames = rows.indices.map { cleanTitle(rows[$0][7]) ?? spawns[$0]?.nickname }
        var resolvedTitles = Dictionary(rows.indices.map { index in
            let row = rows[index]
            return (row[0], cleanTitle(row[3]) ?? desktopTitles[row[0]] ?? cleanTitle(row[4])
                    ?? agentNames[index] ?? "Task \(row[0].prefix(8))")
        }, uniquingKeysWith: { first, _ in first })
        // Parents may be archived or older than the activity query's limit.
        // Resolve only missing IDs, in bounded batches using SQL bindings.
        let missingParents = Set(spawns.compactMap { $0?.parentID })
            .subtracting(resolvedTitles.keys).sorted()
        for start in stride(from: 0, to: missingParents.count, by: 128) {
            let ids = Array(missingParents[start..<min(start + 128, missingParents.count)])
            let placeholders = Array(repeating: "?", count: ids.count).joined(separator: ",")
            let parents = SQLiteStore.rows(in: db,
                sql: "SELECT id, \(name), substr(title, 1, 161), \(nickname) FROM threads WHERE id IN (\(placeholders))",
                columns: 4, bindings: ids)
            for parent in parents {
                resolvedTitles[parent[0]] = cleanTitle(parent[1]) ?? desktopTitles[parent[0]]
                    ?? cleanTitle(parent[2]) ?? cleanTitle(parent[3]) ?? "Task \(parent[0].prefix(8))"
            }
        }
        return rows.indices.map { index in
            let row = rows[index]
            let parent = spawns[index].map {
                AgentSession.ParentThread(id: $0.parentID,
                    title: resolvedTitles[$0.parentID] ?? desktopTitles[$0.parentID]
                        ?? "Task \($0.parentID.prefix(8))")
            }
            return Thread(id: row[0], rollout: URL(fileURLWithPath: (row[1] as NSString).expandingTildeInPath),
                   cwd: row[2], title: cleanTitle(row[3]) ?? desktopTitles[row[0]] ?? cleanTitle(row[4]),
                   savedProjectName: projects[row[5]], agentName: agentNames[index], parentThread: parent)
        }
    }

    private struct Spawn {
        let parentID: String
        let nickname: String?
    }

    private static func spawn(in source: String, excluding threadID: String) -> Spawn? {
        guard source.utf8.count <= 8192,
              let object = try? JSONSerialization.jsonObject(with: Data(source.utf8)) as? [String: Any],
              let subagent = object["subagent"] as? [String: Any],
              let spawn = subagent["thread_spawn"] as? [String: Any],
              let parentID = spawn["parent_thread_id"] as? String,
              UUID(uuidString: parentID) != nil, parentID != threadID else { return nil }
        return Spawn(parentID: parentID, nickname: (spawn["agent_nickname"] as? String).flatMap(cleanTitle))
    }

    private static func titles(in url: URL) -> [String: String] {
        guard let db = SQLiteStore.open(url) else { return [:] }
        defer { sqlite3_close(db) }
        let rows = SQLiteStore.rows(in: db,
            sql: "SELECT thread_id, display_title FROM local_thread_catalog ORDER BY source_updated_at DESC", columns: 2)
        return Dictionary(rows.compactMap { row in cleanTitle(row[1]).map { (row[0], $0) } },
                          uniquingKeysWith: { first, _ in first })
    }

    /// Never surface an entire pasted prompt or attachment preamble as a name.
    private static func cleanTitle(_ value: String) -> String? {
        let value = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty, value.count <= 160, !value.contains("\n"),
              !value.contains("\0"), !Thread.isGeneric(value) else { return nil }
        return value
    }
}
