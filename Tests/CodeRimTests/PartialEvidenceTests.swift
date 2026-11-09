import XCTest
@testable import CodeRim

/// A row whose tokens could not be counted makes the period it belongs to partial, through its own time,
/// for as long as the history is kept, also after the file that held it is gone.
final class PartialEvidenceTests: XCTestCase {
    private var root: URL!
    private var sources: URL { root.appendingPathComponent("sources") }

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("partial-evidence-\(UUID())")
        try FileManager.default.createDirectory(at: sources, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: root) }

    private func write(_ rows: [String], to name: String = "session.jsonl") throws {
        try Data((rows.joined(separator: "\n") + "\n").utf8).write(to: sources.appendingPathComponent(name))
    }
    private func stamp(_ date: Date) -> String { date.formatted(.iso8601) }
    private func claude(_ id: String, at date: Date, input: String = "100", model: String = "claude-sonnet-4-6") -> String {
        #"{"type":"assistant","timestamp":""# + stamp(date) + #"","sessionId":"s","message":{"id":""# + id
            + #"","role":"assistant","model":""# + model + #"","usage":{"input_tokens":"# + input + #","output_tokens":5}}}"#
    }

    func testReplacingOrRewritingASourceAtTheSameOffsetKeepsTheNewPeriodPartial() async throws {
        let now = Date(), old = Date().addingTimeInterval(-60 * 86400)
        for atomic in [false, true] {
            let folder = sources.appendingPathComponent(atomic ? "replaced" : "rewritten")
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let file = folder.appendingPathComponent("session.jsonl")
            let collector = CodexUsageCollector(database: try SQLiteDatabase(url: root.appendingPathComponent("\(atomic).sqlite")),
                                                roots: [folder], provider: .claude)
            try Data(([claude("goodA", at: old), claude("badA", at: old, input: "-5")].joined(separator: "\n") + "\n").utf8).write(to: file)
            _ = try await collector.refresh(now: old, weekStart: .monday)
            try Data(([claude("goodB", at: now), claude("badB", at: now, input: "-5")].joined(separator: "\n") + "\n").utf8)
                .write(to: file, options: atomic ? .atomic : [])
            let refreshed = try await collector.refresh(now: now, weekStart: .monday)
            XCTAssertEqual(refreshed.snapshot.today.totalTokens, 105)
            XCTAssertEqual(refreshed.snapshot.quality, .partial)
            try FileManager.default.removeItem(at: file)
            let retained = try await collector.refresh(now: now, weekStart: .monday)
            XCTAssertEqual(retained.snapshot.quality, .partial)
        }
    }

    func testEvidenceWithoutNewTokensInvalidatesAnalytics() async throws {
        let collector = CodexUsageCollector(database: try SQLiteDatabase(url: root.appendingPathComponent("revision.sqlite")),
                                            roots: [sources], provider: .claude)
        let now = Date()
        try write([claude("good", at: now)])
        let before = try await collector.refresh(now: now, weekStart: .monday)
        let handle = try FileHandle(forWritingTo: sources.appendingPathComponent("session.jsonl"))
        defer { try? handle.close() }
        try handle.seekToEnd()
        try handle.write(contentsOf: Data((claude("bad", at: now, input: "-5") + "\n").utf8))
        let after = try await collector.refresh(now: now, weekStart: .monday)
        XCTAssertGreaterThan(after.analyticsRevision, before.analyticsRevision)
        XCTAssertEqual(after.snapshot.today, before.snapshot.today)
        XCTAssertEqual(after.snapshot.quality, .partial)
    }

    func testEvidenceOnlyCommitCannotExceedTheDatabaseBudget() async throws {
        let database = try SQLiteDatabase(url: root.appendingPathComponent("limited.sqlite"), maximumDatabaseBytes: 1)
        let checkpoint = SourceCheckpoint.fresh(sourcePath: "evidence-source", fileIdentity: "1:1")
        do {
            try await database.commit(events: [], checkpoint: checkpoint, normalizationState: nil,
                                      partialEvidence: [.init(source: "evidence-source", position: 0, observedAt: Date())])
            XCTFail("Evidence must obey the same database limit as usage rows")
        } catch SQLiteDatabaseError.resourceLimit {
            let saved = try await database.checkpoint(for: checkpoint.sourcePath)
            XCTAssertNil(saved)
        }
    }

    func testAClaudeAccountingRowThatFailsValidationKeepsItsPeriodPartialAfterTheFileIsGone() async throws {
        let collector = CodexUsageCollector(database: try SQLiteDatabase(url: root.appendingPathComponent("claude.sqlite")),
                                            roots: [sources], provider: .claude)
        let now = Date()
        try write([claude("good", at: now), claude("bad", at: now, input: "-5")])
        var result = try await collector.refresh(now: now, weekStart: .monday)
        XCTAssertEqual(result.snapshot.today.totalTokens, 105)
        XCTAssertEqual(result.snapshot.quality, .partial)

        try FileManager.default.removeItem(at: sources.appendingPathComponent("session.jsonl"))
        result = try await collector.refresh(now: now, weekStart: .monday)
        XCTAssertEqual(result.snapshot.quality, .partial, "the uncounted row is still missing from this period")
    }

    func testRowsThatAreNotAccountingDoNotMakeHistoryPartial() async throws {
        let collector = CodexUsageCollector(database: try SQLiteDatabase(url: root.appendingPathComponent("claude.sqlite")),
                                            roots: [sources], provider: .claude)
        let now = Date()
        try write([claude("good", at: now),
                   #"{"type":"user","timestamp":""# + stamp(now) + #"","sessionId":"s","message":{"role":"user","content":"hi"}}"#,
                   #"{"type":"summary","summary":"x"}"#,
                   claude("synthetic", at: now, model: "<synthetic>")])
        let result = try await collector.refresh(now: now, weekStart: .monday)
        XCTAssertEqual(result.snapshot.quality, .exact)
    }

    /// The session's last valid row is weeks old; the malformed one is from today. Today's period is partial.
    func testAMalformedCodexRowCountsAtItsOwnTimeNotTheSessionsLastValidRow() async throws {
        let collector = CodexUsageCollector(database: try SQLiteDatabase(url: root.appendingPathComponent("codex.sqlite")),
                                            roots: [sources])
        let now = Date(), old = now.addingTimeInterval(-40 * 86_400)
        try write([
            #"{"timestamp":""# + stamp(old) + #"","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"cached_input_tokens":10,"output_tokens":20,"total_tokens":100},"last_token_usage":{"input_tokens":80,"cached_input_tokens":10,"output_tokens":20,"total_tokens":100}}}}"#,
            #"{"timestamp":""# + stamp(now) + #"","type":"event_msg","payload":{"type":"token_count","info":{"rate_limits":{}}}}"#])
        let result = try await collector.refresh(now: now, weekStart: .monday)
        XCTAssertEqual(result.snapshot.quality, .partial)
    }
}
