import Foundation
import XCTest
@testable import CodeRim

final class ClaudeUsageTests: XCTestCase {
    func testAnalyticsHintsNameTheSelectedProviderForEveryShortcut() {
        for title in ["Usage", "Projects", "Sessions"] {
            XCTAssertEqual(UsageProvider.claude.analyticsHint(for: title),
                           "Shows detailed local Claude Code \(title.lowercased())")
            XCTAssertEqual(UsageProvider.codex.analyticsHint(for: title),
                           "Shows detailed local Codex \(title.lowercased())")
        }
    }

    func testCacheAccountingAndContentProjection() throws {
        let line = row("msg-1", input: 12, read: 100, write: 50, output: 8)
        let parsed = try XCTUnwrap(ClaudeJSONLParser().parse(Data(line.utf8)))
        XCTAssertEqual(parsed.usage.inputTokens, 162)
        XCTAssertEqual(parsed.usage.cachedInputTokens, 100)
        XCTAssertEqual(parsed.usage.cacheWriteInputTokens, 50)
        XCTAssertEqual(parsed.usage.totalTokens, 170)
        XCTAssertEqual(parsed.model, "claude-sonnet-4-6")
    }

    func testMalformedCountersAndNonAssistantRowsAreNotCounted() {
        let valid = row("msg-1")
        for invalid in [
            valid.replacingOccurrences(of: "\"input_tokens\":10", with: "\"input_tokens\":true"),
            valid.replacingOccurrences(of: "\"input_tokens\":10", with: "\"input_tokens\":-1"),
            valid.replacingOccurrences(of: "\"input_tokens\":10", with: "\"input_tokens\":1.5"),
            valid.replacingOccurrences(of: "\"input_tokens\":10", with: "\"input_tokens\":1000000000001"),
            valid.replacingOccurrences(of: "\"cache_read_input_tokens\":100", with: "\"cache_read_input_tokens\":\"bad\""),
            valid.replacingOccurrences(of: "\"type\":\"assistant\"", with: "\"type\":\"user\""),
            valid.replacingOccurrences(of: "\"id\":\"msg-1\"", with: "\"id\":\"\""),
            valid.replacingOccurrences(of: "claude-sonnet-4-6", with: "<synthetic>"),
            valid.replacingOccurrences(of: "\"isApiErrorMessage\":false", with: "\"isApiErrorMessage\":true"),
            valid.replacingOccurrences(of: "2026-08-31T02:00:00Z", with: "not-a-date"),
            String(repeating: "x", count: CodexJSONLParser.maximumLineBytes + 1)
        ] {
            XCTAssertNil(ClaudeJSONLParser().parse(Data(invalid.utf8)))
        }
        XCTAssertNotNil(ClaudeJSONLParser().parse(Data(valid.replacingOccurrences(
            of: "2026-08-31T02:00:00Z", with: "2026-08-31T02:00:00.123Z"
        ).utf8)))
        let withoutCaches = valid.replacingOccurrences(of: "\"cache_read_input_tokens\":100,", with: "")
            .replacingOccurrences(of: "\"cache_creation_input_tokens\":20,", with: "")
        XCTAssertEqual(ClaudeJSONLParser().parse(Data(withoutCaches.utf8))?.usage.totalTokens, 15)
    }

    func testRepeatedBlocksForkCopiesStreamingUpdatesAndRestartAreIdempotent() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        let original = row("msg-1")
        let revised = row("msg-1", input: 12, read: 130, write: 25, output: 9)
        try fixture.write([original, original, revised, original], name: "session-1.jsonl")
        // Copied history must not create a second event or move usage into another day.
        try fixture.write([revised.replacingOccurrences(of: "2026-08-31T02:00:00Z", with: "2026-09-01T02:00:00Z")], name: "copy.jsonl")
        let first = try await drain(fixture.collector, now: date("2026-08-31T12:00:00Z"))
        XCTAssertEqual(first.today.totalTokens, 176)
        XCTAssertEqual(first.allTime.totalTokens, 176)
        let count = try await fixture.database.eventCount()
        XCTAssertEqual(count, 1)

        let restarted = CodexUsageCollector(database: fixture.database, roots: [fixture.sources], provider: .claude)
        let second = try await drain(restarted, now: date("2026-08-31T12:00:00Z"))
        XCTAssertEqual(second, first)
        // One component can advance without another. Raw input + reads + writes stay disjoint.
        try fixture.append(row("msg-1", input: 11, read: 140, write: 20, output: 12) + "\n")
        let updated = try await drain(restarted, now: date("2026-08-31T12:00:00Z"))
        XCTAssertEqual(updated.today.inputTokens, 177) // max(12,11) + 140 + max(25,20)
        XCTAssertEqual(updated.today.totalTokens, 189)
        XCTAssertTrue(updated.today.isValid)
    }

    func testLocalCalendarTodayWeekMonthAndAnalyticsAgree() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        // Seoul midnight is 15:00 UTC. August 31, 2026 is Monday.
        let dates = ["2026-07-31T14:59:59Z", "2026-08-29T16:00:00Z", "2026-08-30T14:59:59Z",
                     "2026-08-30T15:00:00Z", "2026-08-31T01:00:00Z"]
        try fixture.write(dates.enumerated().map { row("msg-\($0.offset)", time: $0.element) })
        var seoul = Calendar(identifier: .gregorian)
        seoul.timeZone = try XCTUnwrap(TimeZone(identifier: "Asia/Seoul"))
        let now = try date("2026-08-31T12:00:00Z")
        let result = try await drain(fixture.collector, now: now, calendar: seoul)
        XCTAssertEqual(result.today.totalTokens, 270)
        XCTAssertEqual(result.week.totalTokens, 270)
        XCTAssertEqual(result.month.totalTokens, 540)
        XCTAssertEqual(result.allTime.totalTokens, 675)
        let sunday = try await fixture.collector.cachedSnapshot(now: now, calendar: seoul, weekStart: .sunday)
        XCTAssertEqual(sunday.week.totalTokens, 540)
        let analytics = try await fixture.collector.analyticsSnapshot(range: .today, through: now, calendar: seoul)
        XCTAssertEqual(analytics.usage, result.today)
        XCTAssertEqual(analytics.models.reduce(0) { $0 + $1.usage.totalTokens }, result.today.totalTokens)
        XCTAssertEqual(analytics.projects.reduce(0) { $0 + $1.usage.totalTokens }, result.today.totalTokens)
        XCTAssertEqual(analytics.sessions.reduce(0) { $0 + $1.usage.totalTokens }, result.today.totalTokens)
        XCTAssertEqual(analytics.buckets.reduce(0) { $0 + $1.usage.totalTokens }, result.today.totalTokens)
    }

    func testSubagentsAreIncludedOnceWithParentMetadata() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.write([row("msg-parent")])
        let withAgent = row("msg-child").replacingOccurrences(of: "\"cwd\":", with: "\"agentId\":\"test\",\"cwd\":")
        try fixture.write([row("msg-child"), withAgent], name: "session-1/subagents/agent-test.jsonl")
        let now = try date("2026-08-31T12:00:00Z")
        let result = try await drain(fixture.collector, now: now)
        XCTAssertEqual(result.today.totalTokens, 270)
        let analytics = try await fixture.collector.analyticsSnapshot(range: .today, through: now, calendar: utc)
        XCTAssertEqual(analytics.sessions.count, 2)
        let child = try XCTUnwrap(analytics.sessions.first { $0.parentSessionID != nil })
        let parent = try XCTUnwrap(analytics.sessions.first { $0.id == child.parentSessionID })
        XCTAssertEqual(parent.directSubagentCount, 1)
        XCTAssertEqual(analytics.projects.first?.name, "ExampleProject")
    }

    func testPartialLinesRewriteAndClearCutoff() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.write([row("msg-1")])
        let now = try date("2026-08-31T12:00:00Z")
        _ = try await drain(fixture.collector, now: now)
        try fixture.append(row("msg-2"))
        let partial = try await drain(fixture.collector, now: now)
        XCTAssertEqual(partial.today.totalTokens, 135)
        try fixture.append("\n")
        let complete = try await drain(fixture.collector, now: now)
        XCTAssertEqual(complete.today.totalTokens, 270)
        try fixture.write([row("msg-2"), row("msg-1"), row("msg-3")])
        let rewritten = try await drain(fixture.collector, now: now)
        XCTAssertEqual(rewritten.today.totalTokens, 405)
        _ = try await fixture.collector.clearLocalHistory(at: now, calendar: utc, weekStart: .monday)
        try fixture.append(row("msg-new", time: "2026-08-31T12:01:00Z") + "\n")
        let cleared = try await drain(fixture.collector, now: date("2026-08-31T12:02:00Z"))
        XCTAssertEqual(cleared.allTime.totalTokens, 135)
    }

    func testProviderIsolationDiscoveryAndSymlinks() async throws {
        XCTAssertNotEqual(UsageProvider.codex.databaseURL, UsageProvider.claude.databaseURL)
        XCTAssertFalse(UsageProvider.claude.supportsAccountTotals)
        let fakeHome = URL(fileURLWithPath: "/tmp/example-home")
        XCTAssertEqual(UsageProvider.claude.sourceRoots(home: fakeHome, environment: [:]).first?.path,
                       "/tmp/example-home/.claude/projects")
        XCTAssertEqual(UsageProvider.claude.sourceRoots(home: fakeHome, environment: ["CLAUDE_CONFIG_DIR": "/tmp/custom-claude"]).first?.path,
                       "/tmp/custom-claude/projects")
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.write([row("msg-1")])
        try FileManager.default.createSymbolicLink(at: fixture.sources.appendingPathComponent("alias.jsonl"),
                                                   withDestinationURL: fixture.sources.appendingPathComponent("session-1.jsonl"))
        try Data(row("msg-history").utf8).write(to: fixture.root.appendingPathComponent("history.jsonl"))
        let codexDB = try SQLiteDatabase(url: fixture.root.appendingPathComponent("Codex.sqlite"))
        let codex = CodexUsageCollector(database: codexDB, roots: [fixture.sources])
        let now = try date("2026-08-31T12:00:00Z")
        let claudeSnapshot = try await drain(fixture.collector, now: now)
        let codexSnapshot = try await drain(codex, now: now)
        XCTAssertEqual(claudeSnapshot.allTime.totalTokens, 135)
        XCTAssertEqual(codexSnapshot.allTime.totalTokens, 0)
        let sources = try CodexSourceDiscovery().discover(in: [fixture.sources])
        XCTAssertEqual(sources.count, 1)
    }

    func testClearedResponseCannotReturnFromLaterStreamingBlocksOrCopiedHistory() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.write([row("msg-cleared")])
        let cutoff = try date("2026-08-31T12:00:00Z")
        _ = try await drain(fixture.collector, now: cutoff)
        _ = try await fixture.collector.clearLocalHistory(at: cutoff, calendar: utc, weekStart: .monday)

        // Streaming blocks may have later timestamps but still describe the old response.
        try fixture.append(row("msg-cleared", time: "2026-08-31T12:01:00Z", output: 50) + "\n")
        try fixture.append(row("msg-new", time: "2026-08-31T12:02:00Z") + "\n")
        let after = try await drain(fixture.collector, now: date("2026-08-31T12:03:00Z"))
        XCTAssertEqual(after.allTime.totalTokens, 135)

        // The pre-cutoff block may disappear from disk; exclusion must survive restart/rebuild.
        try fixture.write([row("msg-cleared", time: "2026-08-31T12:01:00Z", output: 50),
                           row("msg-new", time: "2026-08-31T12:02:00Z")])
        let reopenedDB = try SQLiteDatabase(url: fixture.root.appendingPathComponent("Claude.sqlite"))
        let restarted = CodexUsageCollector(database: reopenedDB, roots: [fixture.sources], provider: .claude)
        _ = try await restarted.rebuild(now: date("2026-08-31T12:03:00Z"), calendar: utc, weekStart: .monday)
        let rebuilt = try await drain(restarted, now: date("2026-08-31T12:03:00Z"))
        XCTAssertEqual(rebuilt.allTime.totalTokens, 135)
    }

    func testCutoffRejectsCopiedResponseEvenWhenLaterBlockIsDiscoveredFirst() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        let cutoff = try date("2026-08-31T12:00:00Z")
        _ = try await fixture.collector.clearLocalHistory(at: cutoff, calendar: utc, weekStart: .monday)
        try fixture.write([row("msg-old", time: "2026-08-31T12:01:00Z")], name: "a-copy.jsonl")
        try fixture.write([row("msg-old")], name: "z-original.jsonl")
        let result = try await drain(fixture.collector, now: date("2026-08-31T12:03:00Z"))
        XCTAssertEqual(result.allTime.totalTokens, 0)
    }

    func testDatabaseRetainsOnlyAccountingProjectionAndPrivatePermissions() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.write([row("message-secret-42")])
        _ = try await drain(fixture.collector, now: date("2026-08-31T12:00:00Z"))
        let files = try FileManager.default.contentsOfDirectory(at: fixture.root, includingPropertiesForKeys: nil)
        for file in files where file.lastPathComponent.hasPrefix("Claude.sqlite") {
            let data = try Data(contentsOf: file)
            for forbidden in ["PRIVATE-CONTENT-NOT-FOR-STORAGE", "/private/example/ExampleProject", "message-secret-42", "session-1"] {
                XCTAssertNil(data.range(of: Data(forbidden.utf8)))
            }
            let mode = try FileManager.default.attributesOfItem(atPath: file.path)[.posixPermissions] as? Int
            XCTAssertEqual(mode, 0o600)
        }
    }

    /// Opt-in only: the path contains a redacted numeric projection plus an
    /// independently computed oracle, never original conversations or credentials.
    func testIndependentLocalAccountingProjection() async throws {
        guard let path = ProcessInfo.processInfo.environment["CODERIM_CLAUDE_VALIDATION_DIR"] else {
            throw XCTSkip("Set CODERIM_CLAUDE_VALIDATION_DIR for local projection verification")
        }
        struct Oracle: Decodable {
            let now: Date
            let today: Int64
            let week: Int64
            let month: Int64
            let allTime: Int64
            let events: Int64
        }
        let root = URL(fileURLWithPath: path, isDirectory: true)
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .secondsSince1970
        let oracle = try decoder.decode(Oracle.self, from: Data(contentsOf: root.appendingPathComponent("oracle.json")))
        let fixture = try Fixture()
        defer { fixture.remove() }
        let collector = CodexUsageCollector(database: fixture.database,
            roots: [root.appendingPathComponent("projects")], provider: .claude)
        var seoul = Calendar(identifier: .gregorian)
        seoul.timeZone = try XCTUnwrap(TimeZone(identifier: "Asia/Seoul"))
        let actual = try await drain(collector, now: oracle.now, calendar: seoul)
        XCTAssertEqual(actual.today.totalTokens, oracle.today)
        XCTAssertEqual(actual.week.totalTokens, oracle.week)
        XCTAssertEqual(actual.month.totalTokens, oracle.month)
        XCTAssertEqual(actual.allTime.totalTokens, oracle.allTime)
        let count = try await fixture.database.eventCount()
        XCTAssertEqual(count, oracle.events)
    }

    private var utc: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        return calendar
    }

    private func date(_ value: String) throws -> Date {
        try Date(value, strategy: .iso8601)
    }

    private func drain(_ collector: CodexUsageCollector, now: Date, calendar: Calendar? = nil) async throws -> UsageSnapshot {
        for _ in 0..<200 {
            let result = try await collector.refresh(now: now, calendar: calendar ?? utc, weekStart: .monday)
            if !result.hasMoreWork { return result.snapshot }
        }
        XCTFail("Collector failed to finish bounded import")
        return .empty
    }

    private func row(_ id: String, time: String = "2026-08-31T02:00:00Z",
                     input: Int = 10, read: Int = 100, write: Int = 20, output: Int = 5) -> String {
        """
        {"type":"assistant","timestamp":"\(time)","sessionId":"session-1","cwd":"/private/example/ExampleProject","isApiErrorMessage":false,"message":{"id":"\(id)","model":"claude-sonnet-4-6","role":"assistant","usage":{"input_tokens":\(input),"cache_read_input_tokens":\(read),"cache_creation_input_tokens":\(write),"output_tokens":\(output),"cache_creation":{"ephemeral_5m_input_tokens":\(write)},"output_tokens_details":{"thinking_tokens":3}},"content":[{"type":"text","text":"PRIVATE-CONTENT-NOT-FOR-STORAGE"}]}}
        """
    }

    private struct Fixture {
        let root: URL
        let sources: URL
        let database: SQLiteDatabase
        let collector: CodexUsageCollector

        init() throws {
            root = FileManager.default.temporaryDirectory.appendingPathComponent("claude-test-\(UUID())", isDirectory: true)
            sources = root.appendingPathComponent("projects", isDirectory: true)
            try FileManager.default.createDirectory(at: sources, withIntermediateDirectories: true)
            database = try SQLiteDatabase(url: root.appendingPathComponent("Claude.sqlite"))
            collector = CodexUsageCollector(database: database, roots: [sources], provider: .claude)
        }
        func write(_ rows: [String], name: String = "session-1.jsonl") throws {
            let path = sources.appendingPathComponent(name)
            try FileManager.default.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data((rows.joined(separator: "\n") + "\n").utf8).write(to: path)
        }
        func append(_ text: String) throws {
            let handle = try FileHandle(forWritingTo: sources.appendingPathComponent("session-1.jsonl"))
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: Data(text.utf8))
        }
        func remove() { try? FileManager.default.removeItem(at: root) }
    }
}
