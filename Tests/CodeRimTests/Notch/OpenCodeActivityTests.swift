import CSQLite
import XCTest
@testable import CodeRim

final class OpenCodeActivityTests: XCTestCase {
    private var root: URL!
    private var database: URL { root.appendingPathComponent("opencode.db") }
    private let now = Date(timeIntervalSince1970: 1_800_000_000)
    private var ms: (TimeInterval) -> Int { { Int((self.now.timeIntervalSince1970 + $0) * 1000) } }

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try execute("""
            CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT, title TEXT, parent_id TEXT,
              time_updated INTEGER, time_compacting INTEGER, time_archived INTEGER);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER,
              time_updated INTEGER, data TEXT);
            """)
    }

    override func tearDownWithError() throws { try FileManager.default.removeItem(at: root) }

    private func execute(_ sql: String) throws {
        var db: OpaquePointer?
        XCTAssertEqual(sqlite3_open(database.path, &db), SQLITE_OK)
        defer { sqlite3_close(db) }
        XCTAssertEqual(sqlite3_exec(db, sql, nil, nil, nil), SQLITE_OK)
    }

    private func session(_ id: String, compacting: Int? = nil, parent: String? = nil) throws {
        try execute("INSERT INTO session VALUES ('\(id)', '/work/app', 'Title', \(parent.map { "'\($0)'" } ?? "NULL"), \(ms(-10)), \(compacting.map(String.init) ?? "NULL"), NULL)")
    }

    private func user(_ id: String, session: String, at: TimeInterval) throws {
        try execute("INSERT INTO message VALUES ('\(id)', '\(session)', \(ms(at)), \(ms(at)), '{\"role\":\"user\",\"time\":{\"created\":\(ms(at))}}')")
    }

    private func assistant(_ id: String, session: String, parent: String, created: TimeInterval,
                           completed: TimeInterval?) throws {
        let done = completed.map { ",\"completed\":\(ms($0))" } ?? ""
        try execute("INSERT INTO message VALUES ('\(id)', '\(session)', \(ms(created)), \(ms(created)), '{\"role\":\"assistant\",\"parentID\":\"\(parent)\",\"time\":{\"created\":\(ms(created))\(done)}}')")
    }

    func testReplyWithNoEndIsWorking() throws {
        try session("a")
        try user("u1", session: "a", at: -120)
        try assistant("r1", session: "a", parent: "u1", created: -119, completed: nil)
        let found = OpenCodeActivityMonitor.read(database: database, now: now)
        XCTAssertEqual(found.first?.state, .busy)
        XCTAssertEqual(found.first?.projectKey, "app")
        XCTAssertEqual(found.first?.since.timeIntervalSince1970 ?? 0, now.timeIntervalSince1970 - 120, accuracy: 1)
    }

    func testPersonWhoSpokeLastMeansWorking() throws {
        try session("a")
        try user("u1", session: "a", at: -200)
        try assistant("r1", session: "a", parent: "u1", created: -199, completed: -150)
        try user("u2", session: "a", at: -20)
        XCTAssertEqual(OpenCodeActivityMonitor.read(database: database, now: now).first?.state, .busy)
    }

    func testFinishedSessionLingersThenVanishes() throws {
        try session("a")
        try user("u1", session: "a", at: -100)
        try assistant("r1", session: "a", parent: "u1", created: -99, completed: -30)
        XCTAssertEqual(OpenCodeActivityMonitor.read(database: database, now: now).first?.state, .idle)
        XCTAssertTrue(OpenCodeActivityMonitor.read(database: database, now: now.addingTimeInterval(120)).isEmpty)
    }

    func testCompactingSessionSaysSo() throws {
        try session("a", compacting: ms(-5))
        try user("u1", session: "a", at: -100)
        try assistant("r1", session: "a", parent: "u1", created: -99, completed: nil)
        XCTAssertEqual(OpenCodeActivityMonitor.read(database: database, now: now).first?.isCompacting, true)
    }

    func testSubagentSessionsAreNotListed() throws {
        try session("child", parent: "a")
        try user("u1", session: "child", at: -10)
        XCTAssertTrue(OpenCodeActivityMonitor.read(database: database, now: now).isEmpty)
    }

    func testPastTurnLengthsComeFromParentedReplies() throws {
        try session("a")
        try user("u1", session: "a", at: -300)
        try assistant("r1", session: "a", parent: "u1", created: -299, completed: -250)
        try assistant("r2", session: "a", parent: "u1", created: -249, completed: -180)
        let samples = TurnHistoryImporter.openCodeSamples(database: database, now: now)
        XCTAssertEqual(samples.map(\.seconds), [120])
        XCTAssertEqual(samples.first?.project, "app")
    }

    func testMissingDatabaseIsEmptyNotAnError() {
        XCTAssertTrue(OpenCodeActivityMonitor.read(database: root.appendingPathComponent("none.db")).isEmpty)
    }
}
