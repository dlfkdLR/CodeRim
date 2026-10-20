import CSQLite
import SwiftUI
import XCTest
@testable import CodeRim

final class RemoteSessionTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)
    private let parentID = "11111111-1111-1111-1111-111111111111"
    private let childID = "22222222-2222-2222-2222-222222222222"

    private func session(_ id: String, host: String? = nil,
                         state: AgentSession.State = .unavailable,
                         parent: String? = nil) -> AgentSession {
        AgentSession(id: "\(host ?? "local").\(id)", name: host == nil ? "CodeRim" : "Vispace",
            detail: host == nil ? "로컬 작업" : "모든 사물 지원 추가", state: state,
            waitingFor: nil, since: now.addingTimeInterval(-120), codexThreadID: id,
            parentThread: parent.map { .init(id: $0, title: "원격 부모 작업") }, remoteHostID: host)
    }

    private func database(_ body: (URL, OpaquePointer?) throws -> Void) throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let url = root.appendingPathComponent("desktop.db")
        var db: OpaquePointer?
        XCTAssertEqual(sqlite3_open(url.path, &db), SQLITE_OK)
        defer { sqlite3_close(db) }
        try body(url, db)
    }

    func testRemoteCatalogNeverInventsRuntimeStateAndFiltersUnsupportedEntries() throws {
        try database { url, db in
            let sql = """
                CREATE TABLE local_thread_catalog_hosts (host_id TEXT, host_kind TEXT);
                CREATE TABLE local_thread_catalog (host_id TEXT, thread_id TEXT, display_title TEXT,
                    cwd TEXT, source_updated_at REAL, missing_candidate INTEGER, source_kind TEXT);
                INSERT INTO local_thread_catalog_hosts VALUES
                    ('local', 'local'), ('remote:pc', 'remote-control'), ('cloud', 'chatgpt');
                """
            XCTAssertEqual(sqlite3_exec(db, sql, nil, nil, nil), SQLITE_OK)
            func insert(_ host: String, age: Int = 30, missing: Int = 0, source: String = "vscode") {
                let text = "INSERT INTO local_thread_catalog VALUES ('\(host)', '\(parentID)', '원격 작업', 'C:\\Users\\test\\Vispace', \(Int(now.timeIntervalSince1970) - age), \(missing), '\(source)')"
                XCTAssertEqual(sqlite3_exec(db, text, nil, nil, nil), SQLITE_OK)
            }
            insert("local")
            insert("cloud")
            insert("remote:pc")
            insert("remote:pc", age: 7 * 3600)
            insert("remote:pc", age: -3600)
            insert("remote:pc", missing: 1)
            insert("remote:pc", source: "subagent")
            let sessions = CodexRemoteCatalog.sessions(in: url, now: now)
            XCTAssertEqual(sessions.count, 1)
            let remote = try XCTUnwrap(sessions.first)
            XCTAssertEqual(remote.remoteHostID, "remote:pc")
            XCTAssertEqual(remote.name, "Vispace")
            XCTAssertEqual(remote.state, .unavailable)
            XCTAssertEqual(remote.locationDescription, "Remote task · live status unavailable")
            XCTAssertEqual(ActivitySummary(sessions: sessions)?.state, .unavailable)
            let other = CodexProfile(slug: "other", configDirectory: url.deletingLastPathComponent())
            XCTAssertTrue(CodexRemoteCatalog.sessions(in: url, profile: other, now: now).isEmpty)
        }
    }

    func testCatalogOnlyRemoteTasksReachMonitorWithoutLocalDatabase() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let desktop = root.appendingPathComponent("desktop.db")
        var db: OpaquePointer?
        XCTAssertEqual(sqlite3_open(desktop.path, &db), SQLITE_OK)
        let sql = """
            CREATE TABLE local_thread_catalog_hosts (host_id TEXT, host_kind TEXT);
            CREATE TABLE local_thread_catalog (host_id TEXT, thread_id TEXT, display_title TEXT,
                cwd TEXT, source_updated_at REAL, missing_candidate INTEGER, source_kind TEXT);
            INSERT INTO local_thread_catalog_hosts VALUES ('ssh:server', 'ssh');
            INSERT INTO local_thread_catalog VALUES ('ssh:server', '\(parentID)', 'Deploy', '/work/app',
                \(now.timeIntervalSince1970 - 60), 0, 'vscode');
            """
        XCTAssertEqual(sqlite3_exec(db, sql, nil, nil, nil), SQLITE_OK)
        sqlite3_close(db)
        let result = await CodexActivityReader().read(stateStore: root.appendingPathComponent("missing.db"),
                                                     desktopStore: desktop, now: now)
        XCTAssertEqual(result.count, 1)
        XCTAssertEqual(result.first?.remoteHostID, "ssh:server")
        XCTAssertEqual(result.first?.state, .unavailable)
    }

    func testMissingCatalogSchemaIsHarmless() throws {
        try database { url, _ in
            XCTAssertTrue(CodexRemoteCatalog.sessions(in: url, now: now).isEmpty)
        }
    }

    func testUnknownRemoteStateCannotAnnounceCompletionOrOverrideLocalWork() throws {
        let busy = session(parentID, host: "remote:pc", state: .busy)
        let unknown = session(parentID, host: "remote:pc")
        let idle = session(parentID, host: "remote:pc", state: .idle)
        var watcher = SessionCompletionWatcher()
        XCTAssertTrue(watcher.absorb(["codex": [busy]]).isEmpty)
        XCTAssertTrue(watcher.absorb(["codex": [unknown]]).isEmpty)
        XCTAssertTrue(watcher.absorb(["codex": [idle]]).isEmpty)
        let local = session(childID, state: .busy)
        let summary = try XCTUnwrap(ActivitySummary(sessions: [unknown, local]))
        XCTAssertEqual(summary.state, .working)
        XCTAssertEqual(summary.displayRows.first?.session.id, local.id)
    }

    func testRemoteChildKeepsHostBadgeAndDoesNotAttachToLocalParent() throws {
        let local = session(parentID, state: .busy)
        let remote = session(childID, host: "remote:pc", state: .busy, parent: parentID)
        let summary = try XCTUnwrap(ActivitySummary(sessions: [local, remote]))
        XCTAssertEqual(summary.displayRows.count, 3)
        let context = try XCTUnwrap(summary.displayRows.first(where: \.isContextOnly))
        XCTAssertEqual(context.session.remoteHostID, "remote:pc")
        XCTAssertFalse(summary.displayRows.first { $0.session.id == local.id }!.isParent)
        let single = try XCTUnwrap(ActivitySummary(sessions: [remote])).presentation(cap: 1)
        XCTAssertEqual(single.rows.first?.session.remoteHostID, "remote:pc")
        XCTAssertEqual(single.rows.first?.inlineParent?.id, parentID)
    }

    @MainActor
    func testRemoteIconFitsCardBudgetAndRendersAlongsideLocalTask() throws {
        let snapshot = ProviderSnapshot(id: "codex", displayName: "Codex", glyph: .openai,
            fidelity: .official, status: .ok,
            windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.81)])
        let activity = try XCTUnwrap(ActivitySummary(sessions: [
            session(childID, state: .busy), session(parentID, host: "remote:pc")
        ]))
        let card = TooltipCard(snapshot: snapshot, activity: activity, now: now, sessionCap: 2)
        let content = ImageRenderer(content: card.cardContent.frame(width: NotchLayout.cardTextWidth))
        let size = try XCTUnwrap(content.nsImage).size
        let budget = NotchLayout.cardHeight(for: snapshot, sessionCount: 2, sessionCap: 2, now: now)
            - 2 * NotchLayout.cardPadding
        XCTAssertLessThanOrEqual(size.height, budget + 1)
        XCTAssertEqual(size.width, NotchLayout.cardTextWidth, accuracy: 1)
        if let path = ProcessInfo.processInfo.environment["REMOTE_BADGE_RENDER_PATH"] {
            let render = ImageRenderer(content: card.padding(20).background(Color.black))
            render.scale = 3
            let data = try XCTUnwrap(try XCTUnwrap(render.nsImage).tiffRepresentation)
            let png = try XCTUnwrap(NSBitmapImageRep(data: data)?.representation(using: .png, properties: [:]))
            try png.write(to: URL(fileURLWithPath: path))
        }
    }
}
