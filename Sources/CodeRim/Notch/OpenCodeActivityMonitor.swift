import Combine
import CSQLite
import Foundation

/// What OpenCode sessions are doing, read from its own database.
///
/// OpenCode keeps every message in `opencode.db`, and it records both ends of a
/// turn itself: a user message when the turn starts and `time.completed` on the
/// assistant's reply when it ends. A session whose last reply has no
/// `completed` is working. It also stamps `time_compacting` on the session
/// while it summarises its context, which is the one place a compaction is
/// visible *as it happens* — no hook needed.
@MainActor
final class OpenCodeActivityMonitor: ObservableObject, AgentActivityMonitor {
    @Published private(set) var sessions: [AgentSession] = []
    var sessionsPublisher: AnyPublisher<[AgentSession], Never> { $sessions.eraseToAnyPublisher() }

    private let database: URL
    private let interval: TimeInterval
    private var timer: Timer?
    private var scan: Task<Void, Never>?

    init(database: URL = OpenCodeActivityMonitor.defaultDatabase, interval: TimeInterval = 2) {
        self.database = database
        self.interval = interval
    }

    static var defaultDatabase: URL {
        URL(fileURLWithPath: NSHomeDirectory())
            .appendingPathComponent(".local/share/opencode/opencode.db")
    }

    func start() {
        stop()
        rescan()
        let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.rescan() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        scan?.cancel()
        scan = nil
    }

    private func rescan() {
        guard scan == nil else { return }
        let database = database
        scan = Task { [weak self] in
            let found = await Task.detached(priority: .utility) {
                Self.read(database: database)
            }.value
            guard let self, !Task.isCancelled else { return }
            if found != sessions { sessions = found }
            scan = nil
        }
    }

    /// Sessions touched within the last six hours. A turn with no end after
    /// that is one a crashed client left open, not one still running.
    nonisolated static let horizon: TimeInterval = 6 * 60 * 60
    /// How long a finished session stays listed, so completion detection can
    /// see `busy` become `idle`.
    nonisolated static let lingerAfterFinish: TimeInterval = 90

    nonisolated static func read(database: URL, now: Date = Date()) -> [AgentSession] {
        guard let db = SQLiteStore.open(database) else { return [] }
        defer { sqlite3_close(db) }
        let cutoff = Int((now.timeIntervalSince1970 - horizon) * 1000)
        let rows = SQLiteStore.rows(in: db, sql: """
            SELECT s.id, s.directory, s.title, COALESCE(s.time_compacting, 0),
              COALESCE((SELECT json_extract(m.data,'$.role') FROM message m
                        WHERE m.session_id = s.id ORDER BY m.time_created DESC, m.id DESC LIMIT 1), ''),
              COALESCE((SELECT json_extract(m.data,'$.time.created') FROM message m
                        WHERE m.session_id = s.id AND json_extract(m.data,'$.role') = 'user'
                        ORDER BY m.time_created DESC, m.id DESC LIMIT 1), 0),
              COALESCE((SELECT json_extract(m.data,'$.time.completed') FROM message m
                        WHERE m.session_id = s.id AND json_extract(m.data,'$.role') = 'assistant'
                        ORDER BY m.time_created DESC, m.id DESC LIMIT 1), 0)
            FROM session s
            WHERE s.time_archived IS NULL AND s.parent_id IS NULL AND s.time_updated > ?
            ORDER BY s.time_updated DESC LIMIT 32
            """, columns: 7, bindings: [String(cutoff)])
        return rows.compactMap { session(from: $0, now: now) }
    }

    nonisolated static func session(from row: [String], now: Date) -> AgentSession? {
        guard row.count == 7 else { return nil }
        let (id, directory, title) = (row[0], row[1], row[2])
        let compacting = Double(row[3]) ?? 0
        let lastRole = row[4]
        let userCreated = (Double(row[5]) ?? 0) / 1000
        let completed = (Double(row[6]) ?? 0) / 1000

        // Working: the person spoke last, or the assistant's last reply has no
        // end yet. A reply completed *before* the latest user message belongs
        // to the previous turn.
        let working = lastRole == "user" || (completed == 0 && lastRole == "assistant")
            || completed < userCreated
        let folder = (directory as NSString).lastPathComponent
        let project = folder.isEmpty || folder == "/" ? title : folder
        let started = Date(timeIntervalSince1970: userCreated)

        if working || compacting > 0 {
            guard userCreated > 0 else { return nil }
            return AgentSession(id: "opencode.\(id)", name: project, detail: title, state: .busy,
                                waitingFor: nil, since: started, projectKey: project,
                                isCompacting: compacting > 0)
        }
        let ended = Date(timeIntervalSince1970: completed)
        guard now.timeIntervalSince(ended) < lingerAfterFinish else { return nil }
        return AgentSession(id: "opencode.\(id)", name: project, detail: title, state: .idle,
                            waitingFor: nil, since: ended, projectKey: project)
    }
}
