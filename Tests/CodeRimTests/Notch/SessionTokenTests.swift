import CSQLite
import CryptoKit
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor final class SessionTokenTests: XCTestCase {
    private func hash(_ raw: String) -> String {
        SHA256.hash(data: Data(raw.utf8)).map { String(format: "%02x", $0) }.joined()
    }
    private func session(_ id: String, parent: String? = nil, remote: String? = nil) -> AgentSession {
        AgentSession(id: id, name: "", detail: id, state: .busy, waitingFor: nil, since: Date(),
            codexThreadID: id, parentThread: parent.map { .init(id: $0, title: $0) }, remoteHostID: remote)
    }
    func testMainTotalIncludesFinishedAndNestedChildrenOnce() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: url) }
        var db: OpaquePointer?
        XCTAssertEqual(sqlite3_open(url.path, &db), SQLITE_OK)
        defer { sqlite3_close(db) }
        func sql(_ value: String) throws {
            guard sqlite3_exec(db, value, nil, nil, nil) == SQLITE_OK else {
                XCTFail(String(cString: sqlite3_errmsg(db))); return
            }
        }
        try sql("CREATE TABLE usage_events(session_id TEXT, input_tokens INTEGER, output_tokens INTEGER, cached_input_tokens INTEGER)")
        try sql("CREATE TABLE session_metadata(session_id TEXT PRIMARY KEY, parent_session_id TEXT)")
        for (id, parent) in [("child", "main"), ("grandchild", "child"), ("finished", "main")] {
            try sql("INSERT INTO session_metadata VALUES ('\(hash(id))', '\(hash(parent))')")
        }
        for (id, input, output) in [("main",100,10),("child",200,20),("grandchild",300,30),("finished",400,40),("zero",0,0)] {
            try sql("INSERT INTO usage_events VALUES ('\(hash(id))', \(input), \(output), 99)")
        }
        let live = [session("main"),session("child",parent:"main"),session("zero"),session("absent")]
        let totals = SessionTokenReader.totals(for: live, provider: .codex, databaseURL: url)
        XCTAssertEqual(totals["main"], 1100, "All descendants, including finished ones, count exactly once")
        XCTAssertNil(totals["child"], "Sub-agent never gets a separate total")
        XCTAssertEqual(totals["zero"],0)
        XCTAssertNil(totals["absent"])
        XCTAssertTrue(SessionTokenReader.totals(for: [session("main",remote:"pc")],provider:.codex,databaseURL:url).isEmpty)
        let context = SessionTokenReader.totals(for: [session("child",parent:"main")],provider:.codex,databaseURL:url)
        XCTAssertEqual(context["parent.main"],1100)
        XCTAssertNil(context["child"])
        let nested = SessionTokenReader.totals(for:[session("main"),session("grandchild",parent:"child")],provider:.codex,databaseURL:url)
        XCTAssertEqual(nested["main"],1100)
        XCTAssertNil(nested["parent.child"], "A synthetic intermediate parent must never get a separate total")
        try sql("INSERT INTO session_metadata VALUES ('\(hash("main"))','\(hash("grandchild"))')")
        XCTAssertTrue(SessionTokenReader.totals(for:live,provider:.codex,databaseURL:url)["main"] == nil,
            "A corrupted root cycle has no trustworthy main total")
        try sql("INSERT INTO usage_events VALUES ('\(hash("claude-session|abc"))', 5, 2, 5)")
        try sql("INSERT INTO usage_events VALUES ('\(hash("claude-agent|abc|sub"))', 6, 3, 6)")
        try sql("INSERT INTO session_metadata VALUES ('\(hash("claude-agent|abc|sub"))','\(hash("claude-session|abc"))')")
        let claude = AgentSession(id:"claude.7",name:"Claude",detail:"",state:.busy,waitingFor:nil,since:Date(),usageSessionID:"abc")
        XCTAssertEqual(SessionTokenReader.totals(for:[claude],provider:.claude,databaseURL:url)["claude.7"],16)
    }
    func testOwnedWalTotalsAfterCheckpointAndDuringCollection() throws {
        let files = FileManager.default
        let directory = files.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let source = directory.appendingPathComponent("Usage.sqlite")
        let restored = directory.appendingPathComponent("복원 %?#.sqlite")
        defer { try? files.removeItem(at: directory) }
        let accounting = try SQLiteDatabase(url: source)
        defer { withExtendedLifetime(accounting) {} }
        var connection: OpaquePointer?
        XCTAssertEqual(sqlite3_open_v2(source.path, &connection, SQLITE_OPEN_READWRITE, nil), SQLITE_OK)
        let writer = try XCTUnwrap(connection)
        defer { sqlite3_close(writer) }
        func add(_ position: Int, input: Int) throws {
            try SQLiteSchema.execute("""
                INSERT INTO usage_events (event_key, occurred_at, session_id, input_tokens, cached_input_tokens,
                    output_tokens, source_path, source_generation, source_position)
                VALUES ('event-\(position)', 1, '\(hash("root"))', \(input), 0, 1, 'fixture', 0, \(position))
                """, on: writer)
        }
        func total(_ url: URL) -> Int64? {
            SessionTokenReader.totals(for: [session("root")], provider: .codex, databaseURL: url)["root"]
        }
        try SQLiteSchema.execute("PRAGMA wal_autocheckpoint = 0", on: writer)
        try add(1, input: 40)
        XCTAssertEqual(sqlite3_wal_checkpoint_v2(writer, nil, SQLITE_CHECKPOINT_TRUNCATE, nil, nil), SQLITE_OK)
        // Only copy a known checkpointed main file. This reproduces a restored history.
        try files.copyItem(at: source, to: restored)
        let checkpoint = try Data(contentsOf: restored)
        XCTAssertFalse(files.fileExists(atPath: restored.path + "-shm"))
        XCTAssertEqual(total(restored), 41)
        XCTAssertEqual(try Data(contentsOf: restored), checkpoint)

        try SQLiteSchema.execute("BEGIN IMMEDIATE", on: writer)
        try add(2, input: 22)
        XCTAssertEqual(total(source), 41)
        try SQLiteSchema.execute("COMMIT", on: writer)
        XCTAssertEqual(try Data(contentsOf: source), checkpoint, "The new event is still in the WAL")
        XCTAssertEqual(total(source), 64)
        XCTAssertEqual(total(restored), 41)
        XCTAssertEqual(try Data(contentsOf: source), checkpoint)

        // Snapshot the idle writer's files together to model its last process disappearing.
        let interrupted = directory.appendingPathComponent("interrupted.sqlite")
        for suffix in ["", "-wal", "-shm"] {
            try files.copyItem(atPath: source.path + suffix, toPath: interrupted.path + suffix)
        }
        let wal = URL(fileURLWithPath: interrupted.path + "-wal")
        let committedFrames = try Data(contentsOf: wal)
        XCTAssertFalse(committedFrames.isEmpty)
        XCTAssertEqual(total(interrupted), 64)
        XCTAssertEqual(try Data(contentsOf: interrupted), checkpoint)
        XCTAssertEqual(try Data(contentsOf: wal), committedFrames)

        let missing = directory.appendingPathComponent("missing.sqlite")
        XCTAssertNil(total(missing))
        XCTAssertFalse(files.fileExists(atPath: missing.path))
        let damaged = directory.appendingPathComponent("damaged.sqlite")
        let invalid = Data("damaged fixture".utf8)
        try invalid.write(to: damaged)
        XCTAssertNil(total(damaged))
        XCTAssertEqual(try Data(contentsOf: damaged), invalid)

        let readOnly = directory.appendingPathComponent("read-only")
        try files.createDirectory(at: readOnly, withIntermediateDirectories: false)
        let frozen = readOnly.appendingPathComponent("Usage.sqlite")
        try checkpoint.write(to: frozen)
        try files.setAttributes([.posixPermissions: 0o400], ofItemAtPath: frozen.path)
        try files.setAttributes([.posixPermissions: 0o500], ofItemAtPath: readOnly.path)
        defer { try? files.setAttributes([.posixPermissions: 0o700], ofItemAtPath: readOnly.path) }
        if !files.isWritableFile(atPath: readOnly.path) {
            XCTAssertNil(total(frozen), "A WAL snapshot without writable lock files cannot be opened safely")
            XCTAssertEqual(try files.contentsOfDirectory(atPath: readOnly.path), ["Usage.sqlite"])
        }
        XCTAssertEqual(try Data(contentsOf: frozen), checkpoint)
    }

    func testPreferencesAndMissingUsageRemainOffOrBlank() throws {
        let suite = "TokenTests.\(UUID().uuidString)"
        let d = try XCTUnwrap(UserDefaults(suiteName:suite))
        defer { d.removePersistentDomain(forName:suite) }
        AppPreferences.registerDefaults(in:d)
        XCTAssertFalse(d.bool(forKey:AppPreferences.notchShowUnknownSessionsKey))
        XCTAssertNil(SessionTokenReader.text(nil,enabled:true))
        XCTAssertNil(SessionTokenReader.text(100,enabled:false))
        XCTAssertEqual(SessionTokenReader.text(0,enabled:true),"0 tokens")
    }
    func testChildrenAlwaysAppearAndOldPreferencesCannotHideThem() throws {
        let activity = try XCTUnwrap(ActivitySummary(sessions:[session("A"),session("a",parent:"A"),session("B"),session("b",parent:"B")]))
        let presentation = SessionPresentation(summary:activity,cap:8)
        XCTAssertEqual(Set(presentation.rows.map(\.id)),["A","B","a","b"])
        let one = SessionPresentation(summary:activity,cap:1)
        XCTAssertEqual(one.groups.count,1)
        XCTAssertEqual(one.rows.count,2, "A shown main chat always includes its children")
        XCTAssertEqual(one.hidden,1, "More counts main chats only")
        let zero = SessionPresentation(summary:activity,cap:0)
        XCTAssertEqual(zero.hidden,2)
        XCTAssertTrue(zero.showsDisclosure)
        XCTAssertFalse(SessionPresentation(summary:nil,cap:0,expanded:true).showsDisclosure)
        XCTAssertEqual(SessionPresentation(summary:activity,cap:1,expanded:true).rows.count,4)
        let context = SessionPresentation(summary:ActivitySummary(sessions:[session("a",parent:"A")]),cap:1)
        XCTAssertEqual(context.rows.first?.id,"parent.A")
        XCTAssertTrue(context.rows.first?.isContextOnly == true)
        XCTAssertEqual(context.rows.count,2)
        let suite = "LegacySubagentPreferences.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName:suite))
        defer { defaults.removePersistentDomain(forName:suite) }
        let snapshot = ProviderSnapshot(id:"codex",displayName:"Codex",glyph:.openai,fidelity:.official,status:.ok,windows:[])
        for oldValue in [false,true] {
            defaults.set(oldValue,forKey:"notchSubagentsOnHover")
            defaults.set(oldValue,forKey:"notchSubagentsAlwaysVisible")
            let model = NotchViewModel(defaults:defaults)
            model.snapshots = [snapshot]
            model.sessions = ["codex":activity.sessions]
            XCTAssertEqual(model.sessionPresentation(for:snapshot,cellCount:1).rows.count,4)
        }
    }
    func testRenderedFamiliesIncludeChildrenWithoutHover() throws {
        let suite = "SessionFamilyRender.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName:suite))
        defer { defaults.removePersistentDomain(forName:suite) }
        let now = Date(timeIntervalSince1970:1_800_000_000)
        let snapshot = ProviderSnapshot(id:"codex",displayName:"Codex",glyph:.openai,fidelity:.official,status:.ok,
            windows:[LimitWindow(id:"weekly",label:"Weekly",usedFraction:0.81)])
        let summary = try XCTUnwrap(ActivitySummary(sessions:[session("메인 채팅"),session("Poincare",parent:"메인 채팅"),session("Ampere",parent:"메인 채팅")]))
        var sizes:[CGSize] = []
        let parentOnly = try XCTUnwrap(ActivitySummary(sessions:[session("메인 채팅")]))
        for (name, activity) in [("parent-only",parentOnly),("family-always",summary)] {
            let card = TooltipCard(snapshot:snapshot,activity:activity,now:now,
                tokenTotals:["메인 채팅":125_600,"Poincare":55_000,"Ampere":50_000])
                .defaultAppStorage(defaults)
                .transaction { $0.animation = nil; $0.disablesAnimations = true }
            let renderer = ImageRenderer(content:card.padding(20).background(Color.black))
            renderer.scale = 3
            let image = try XCTUnwrap(renderer.nsImage)
            sizes.append(image.size)
            if let dir = ProcessInfo.processInfo.environment["SESSION_DISCLOSURE_RENDER_DIR"] {
                let data = try XCTUnwrap(image.tiffRepresentation)
                let png = try XCTUnwrap(NSBitmapImageRep(data:data)?.representation(using:.png,properties:[:]))
                try png.write(to:URL(fileURLWithPath:dir).appendingPathComponent(name+"-v6.png"))
            }
        }
        XCTAssertGreaterThan(sizes[1].height,sizes[0].height)
        XCTAssertEqual(sizes[0].width,sizes[1].width)
    }

}
