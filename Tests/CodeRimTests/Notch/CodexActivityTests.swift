import CSQLite
import CryptoKit
import XCTest
@testable import CodeRim

final class CodexActivityTests: XCTestCase {
    private var root: URL!
    private var store: URL { root.appendingPathComponent("state_5.sqlite") }
    private var desktop: URL { root.appendingPathComponent("missing.db") }
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try execute("CREATE TABLE threads (id TEXT, rollout_path TEXT, cwd TEXT, name TEXT, title TEXT, archived INTEGER, updated_at INTEGER)")
    }

    override func tearDownWithError() throws {
        try FileManager.default.removeItem(at: root)
    }

    private func execute(_ sql: String) throws {
        var db: OpaquePointer?
        XCTAssertEqual(sqlite3_open(store.path, &db), SQLITE_OK)
        defer { sqlite3_close(db) }
        XCTAssertEqual(sqlite3_exec(db, sql, nil, nil, nil), SQLITE_OK)
    }

    private func event(_ type: String, at: Date) -> String {
        let timestamp = ISO8601DateFormatter().string(from: at)
        return "{\"type\":\"event_msg\",\"timestamp\":\"\(timestamp)\",\"payload\":{\"type\":\"\(type)\"}}\n"
    }

    @discardableResult
    private func thread(_ id: String, body: String, cwd: String = "/work/CodeRim",
                        name: String = "Fix progress") throws -> URL {
        let url = root.appendingPathComponent("\(id).jsonl")
        try Data(body.utf8).write(to: url)
        try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-60)], ofItemAtPath: url.path)
        let quote: (String) -> String = { "'" + $0.replacingOccurrences(of: "'", with: "''") + "'" }
        try execute("INSERT INTO threads (id, rollout_path, cwd, name, title, archived, updated_at) VALUES (\(quote(id)), \(quote(url.path)), \(quote(cwd)), \(quote(name)), '', 0, \(Int(now.timeIntervalSince1970)))")
        return url
    }

    func testSilentTurnRemainsWorkingWithProjectAndTaskNames() async throws {
        let began = now.addingTimeInterval(-300)
        try thread("one", body: event("task_started", at: began))
        let reader = CodexActivityReader()
        let first = await reader.read(stateStore: store, desktopStore: desktop, now: now)
        let later = await reader.read(stateStore: store, desktopStore: desktop, now: now.addingTimeInterval(120))
        XCTAssertEqual(first, later)
        XCTAssertEqual(first.first?.state, .busy)
        XCTAssertEqual(first.first?.since, began)
        XCTAssertEqual(first.first?.name, "CodeRim")
        XCTAssertEqual(first.first?.detail, "Fix progress")
    }

    func testCompletedOrAbortedTurnStopsImmediatelyDespiteFreshMetadata() async throws {
        for terminal in ["task_complete", "turn_aborted"] {
            let url = try thread(terminal, body: event("task_started", at: now.addingTimeInterval(-60)))
            let reader = CodexActivityReader()
            let before = await reader.read(stateStore: store, desktopStore: desktop, now: now)
            XCTAssertEqual(before.first { $0.id.hasSuffix(terminal) }?.state, .busy)
            try Data((event("task_started", at: now.addingTimeInterval(-60))
                      + event(terminal, at: now)
                      + "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"}}\n").utf8).write(to: url)
            try FileManager.default.setAttributes([.modificationDate: now], ofItemAtPath: url.path)
            let after = await reader.read(stateStore: store, desktopStore: desktop, now: now)
            XCTAssertEqual(after.first { $0.id.hasSuffix(terminal) }?.state, .idle)
            let expired = await reader.read(stateStore: store, desktopStore: desktop, now: now.addingTimeInterval(100))
            XCTAssertFalse(expired.contains { $0.id.hasSuffix(terminal) })
        }
    }

    func testAllRunningThreadsSurviveANewerCompletedThread() async throws {
        try thread("running-a", body: event("task_started", at: now.addingTimeInterval(-300)))
        try thread("running-b", body: event("task_started", at: now.addingTimeInterval(-120)))
        try thread("done", body: event("task_complete", at: now.addingTimeInterval(-100)))
        let sessions = await CodexActivityReader().read(stateStore: store, desktopStore: desktop, now: now)
        XCTAssertEqual(Set(sessions.map(\.id)), ["codex.running-a", "codex.running-b"])
    }

    func testEveryProgressRowKeepsItsOwnRawThreadIDAcrossProfilesAndStates() async throws {
        let running = "01a0ac9e-2019-7872-ba5f-c293768dd2d5"
        let completed = "01a0a49e-1295-7003-ae99-c6905e2be95f"
        try thread(running, body: event("task_started", at: now.addingTimeInterval(-120)))
        try thread(completed, body: event("task_complete", at: now))
        let profile = CodexProfile(slug: "work.account", configDirectory: root)
        let sessions = await CodexActivityReader().read(
            stateStore: store, desktopStore: desktop, profile: profile, now: now)
        XCTAssertEqual(Set(sessions.compactMap(\.codexThreadID)), [running, completed])
        for session in sessions {
            let id = try XCTUnwrap(session.codexThreadID)
            XCTAssertEqual(session.id, "codex-work.account.\(id)")
            XCTAssertEqual(SessionFocus.target(for: session),
                           .codexThread(try XCTUnwrap(URL(string: "codex://threads/\(id)"))))
        }
    }

    private func addSpawnMetadata() throws {
        try execute("ALTER TABLE threads ADD COLUMN source TEXT")
        try execute("ALTER TABLE threads ADD COLUMN agent_nickname TEXT")
    }

    private func setSpawn(_ child: String, parent: String, nickname: String = "Reviewer") throws {
        let source = String(decoding: try JSONSerialization.data(withJSONObject: [
            "subagent": ["thread_spawn": ["parent_thread_id": parent, "agent_nickname": nickname]]
        ]), as: UTF8.self).replacingOccurrences(of: "'", with: "''")
        try execute("UPDATE threads SET source = '\(source)' WHERE id = '\(child)'")
    }

    func testArchivedParentOutsideActivityLimitStillHasItsTitle() async throws {
        try addSpawnMetadata()
        let parent = "11111111-1111-1111-1111-111111111111"
        let child = "22222222-2222-2222-2222-222222222222"
        try thread(parent, body: "", name: "채팅 이동 개선")
        try execute("UPDATE threads SET archived = 1, updated_at = 0 WHERE id = '\(parent)'")
        try thread(child, body: event("task_started", at: now), name: "")
        try setSpawn(child, parent: parent)
        let found = try XCTUnwrap(CodexStore.threads(in: store, limit: 1).first)
        XCTAssertEqual(found.id, child)
        XCTAssertEqual(found.displayTitle, "Reviewer")
        XCTAssertEqual(found.parentThread, .init(id: parent, title: "채팅 이동 개선"))
        let reader = CodexActivityReader()
        let before = await reader.read(stateStore: store, desktopStore: desktop, now: now)
        XCTAssertEqual(before.first?.codexThreadID, child)
        XCTAssertEqual(before.first?.parentThread, found.parentThread)
        try execute("UPDATE threads SET name = '새 부모 제목' WHERE id = '\(parent)'")
        let after = await reader.read(stateStore: store, desktopStore: desktop, now: now)
        XCTAssertEqual(after.first?.parentThread?.title, "새 부모 제목")
        XCTAssertEqual(after.first?.since, before.first?.since)
    }

    func testMissingParentIsIdentifiedWithoutLeakingRawPromptAsTitle() throws {
        try addSpawnMetadata()
        let child = "22222222-2222-2222-2222-222222222222"
        let parent = "33333333-3333-3333-3333-333333333333"
        try thread(child, body: "", name: "")
        try setSpawn(child, parent: parent)
        XCTAssertEqual(CodexStore.threads(in: store).first?.parentThread?.title, "Task 33333333")
        try thread(parent, body: "", name: "A pasted prompt\nthat should not be a title")
        XCTAssertEqual(CodexStore.threads(in: store).first { $0.id == child }?.parentThread?.title,
                       "Task 33333333")
    }

    func testNormalForkMalformedAndSelfParentSourcesAreNotSubagents() throws {
        try addSpawnMetadata()
        let id = "22222222-2222-2222-2222-222222222222"
        try thread(id, body: "")
        for source in ["vscode", "invalid json", "{}", "{\"forked_from_id\":\"other\"}"] {
            try execute("UPDATE threads SET source = '\(source)'")
            XCTAssertNil(CodexStore.threads(in: store).first?.parentThread)
        }
        try setSpawn(id, parent: id)
        XCTAssertNil(CodexStore.threads(in: store).first?.parentThread)
        try setSpawn(id, parent: "../settings")
        XCTAssertNil(CodexStore.threads(in: store).first?.parentThread)
    }

    func testLargeToolOutputAndPartialFinalRecordDoNotHideStart() throws {
        let began = now.addingTimeInterval(-600)
        let body = event("task_started", at: began)
            + "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"output\":\""
            + String(repeating: "x", count: 7 * 1_024 * 1_024) + "\"}}\n"
            + event("task_complete", at: now).trimmingCharacters(in: .newlines)
        let url = try thread("large", body: body)
        XCTAssertEqual(CodexTurnActivity.read(url), .init(isRunning: true, since: began))
        try Data((body + "\n").utf8).write(to: url)
        XCTAssertEqual(CodexTurnActivity.read(url)?.isRunning, false)
    }

    func testBoundedTailIgnoresTruncatedRecordAndRejectsNonpositiveLimits() throws {
        let started = event("task_started", at: now)
        let url = try thread("bounded", body: started)
        XCTAssertNil(CodexTurnActivity.read(url, maximumBytes: 0))
        XCTAssertNil(CodexTurnActivity.read(url, maximumBytes: -1))
        XCTAssertNil(CodexTurnActivity.read(url, maximumBytes: started.utf8.count - 1))
        XCTAssertEqual(CodexTurnActivity.read(url, maximumBytes: started.utf8.count),
                       .init(isRunning: true, since: now))
        let complete = event("task_complete", at: now)
        try Data((String(repeating: "x", count: 1_024) + "\n" + complete).utf8).write(to: url)
        XCTAssertEqual(CodexTurnActivity.read(url, maximumBytes: complete.utf8.count + 1),
                       .init(isRunning: false, since: now))
    }


    /// A tool result that quotes a boundary marker — reviewing this very file
    /// would do it — makes the record a decoding candidate. Chunked scanning
    /// dropped the old 8 MB tail ceiling, so nothing else stops that record
    /// from being read into memory whole, on a timer, only to fail the JSON
    /// parse. The real start event behind it still has to be found.
    func testAnEnormousRecordQuotingAMarkerIsNotMaterialized() throws {
        let began = now.addingTimeInterval(-600)
        let quoted = "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\","
            + "\"output\":\"reviewing \\\"task_complete\\\" handling: "
            + String(repeating: "x", count: CodexTurnActivity.maximumRecordBytes + 4_096)
            + "\"}}\n"
        let url = try thread("oversized-record", body: event("task_started", at: began) + quoted)

        XCTAssertEqual(CodexTurnActivity.read(url), .init(isRunning: true, since: began))
    }

    /// The ceiling is on one record, not on the file: a real boundary event
    /// sitting after a record over the ceiling is still found.
    func testARealBoundaryAfterAnOversizedRecordIsStillRead() throws {
        let quoted = "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\","
            + "\"output\":\"\\\"task_started\\\" "
            + String(repeating: "x", count: CodexTurnActivity.maximumRecordBytes + 4_096)
            + "\"}}\n"
        let url = try thread("oversized-then-real",
                             body: quoted + event("task_complete", at: now))

        XCTAssertEqual(CodexTurnActivity.read(url), .init(isRunning: false, since: now))
    }

    private func append(_ text: String, to url: URL) throws {
        let handle = try FileHandle(forWritingTo: url)
        defer { try? handle.close() }
        try handle.seekToEnd()
        try handle.write(contentsOf: Data(text.utf8))
        try FileManager.default.setAttributes([.modificationDate: now], ofItemAtPath: url.path)
    }

    private func largeOutput(megabytes: Int) -> String {
        "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"output\":\""
            + String(repeating: "x", count: megabytes * 1_024 * 1_024) + "\"}}\n"
    }

    func testColdReaderRecoversTurnBeyondFormerTailLimit() async throws {
        let began = now.addingTimeInterval(-1_200)
        let url = try thread("long-running", body: event("task_started", at: began)
            + largeOutput(megabytes: 24))
        XCTAssertNil(CodexTurnActivity.read(url, maximumBytes: 8 * 1_024 * 1_024))
        XCTAssertEqual(CodexTurnActivity.read(url), .init(isRunning: true, since: began))
        let first = await CodexActivityReader().read(stateStore: store, desktopStore: desktop, now: now)
        XCTAssertEqual(first.first?.state, .busy)
        XCTAssertEqual(first.first?.since, began)
    }

    func testIncrementalReaderKeepsLongTurnAndConsumesPartialCompletion() async throws {
        let began = now.addingTimeInterval(-600)
        let url = try thread("append", body: event("task_started", at: began))
        let reader = CodexActivityReader()
        let initial = await reader.read(stateStore: store, desktopStore: desktop, now: now)
        try append(largeOutput(megabytes: 12), to: url)
        let grown = await reader.read(stateStore: store, desktopStore: desktop, now: now)
        XCTAssertEqual(initial, grown)
        try append(event("task_complete", at: now).trimmingCharacters(in: .newlines), to: url)
        let partial = await reader.read(stateStore: store, desktopStore: desktop, now: now)
        XCTAssertEqual(initial, partial)
        try append("\n", to: url)
        let ended = await reader.read(stateStore: store, desktopStore: desktop, now: now)
        XCTAssertEqual(ended.first?.state, .idle)
        XCTAssertEqual(ended.first?.since, now)
        let expired = await reader.read(stateStore: store, desktopStore: desktop, now: now.addingTimeInterval(100))
        XCTAssertTrue(expired.isEmpty)
    }

    func testLatestTerminalBeforeLargeOutputDoesNotResurrectOldStart() async throws {
        for terminal in ["task_complete", "turn_aborted"] {
            let url = try thread(terminal, body: event("task_started", at: now.addingTimeInterval(-60)))
            var reader = CodexTurnActivity.Reader()
            XCTAssertEqual(reader.read(url)?.isRunning, true)
            try append(event(terminal, at: now) + largeOutput(megabytes: 9), to: url)
            XCTAssertEqual(reader.read(url), .init(isRunning: false, since: now))
            var restarted = CodexTurnActivity.Reader()
            XCTAssertEqual(restarted.read(url), .init(isRunning: false, since: now))
        }
    }

    func testReaderInvalidatesReplacedTruncatedAndRewrittenFiles() throws {
        let started = event("task_started", at: now)
        let url = try thread("rewrite", body: started + String(repeating: " ", count: 1_024) + "\n")
        var reader = CodexTurnActivity.Reader()
        XCTAssertEqual(reader.read(url)?.isRunning, true)
        // Rewrite larger than the old file, without any activity boundary.
        try Data(String(repeating: " \n", count: 1_024).utf8).write(to: url)
        XCTAssertNil(reader.read(url))
        try Data(started.utf8).write(to: url)
        XCTAssertEqual(reader.read(url)?.isRunning, true)
        try Data(event("turn_aborted", at: now).utf8).write(to: url, options: .atomic)
        XCTAssertEqual(reader.read(url)?.isRunning, false)
        try Data().write(to: url)
        XCTAssertNil(reader.read(url))
        try append(started, to: url)
        XCTAssertEqual(reader.read(url)?.isRunning, true)
        // Same byte count, different boundary; force an observable modification.
        let other = event("turn_aborted", at: now)
        XCTAssertEqual(started.utf8.count, other.utf8.count)
        try Data(other.utf8).write(to: url)
        try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(1)], ofItemAtPath: url.path)
        XCTAssertEqual(reader.read(url)?.isRunning, false)
    }


    func testGrowingRewriteCannotKeepAnOverwrittenBoundary() throws {
        let padding = String(repeating: " ", count: 512) + "\n"
        let started = event("task_started", at: now)
        let aborted = event("turn_aborted", at: now)
        XCTAssertEqual(started.utf8.count, aborted.utf8.count)
        let url = try thread("middle-rewrite", body: padding + started + padding)
        var reader = CodexTurnActivity.Reader()
        XCTAssertEqual(reader.read(url)?.isRunning, true)
        // Keep both cached file-end samples unchanged while overwriting the boundary.
        let handle = try FileHandle(forWritingTo: url)
        try handle.seek(toOffset: UInt64(padding.utf8.count))
        try handle.write(contentsOf: Data(aborted.utf8))
        try handle.seekToEnd()
        try handle.write(contentsOf: Data("\n".utf8))
        try handle.close()
        XCTAssertEqual(reader.read(url), .init(isRunning: false, since: now))
    }

    func testBoundaryMarkersSplitAcrossScanChunksRemainVisible() throws {
        for type in ["task_started", "task_complete", "turn_aborted"] {
            let line = event(type, at: now)
            let marker = "\"\(type)\""
            let range = try XCTUnwrap(line.range(of: marker))
            let markerOffset = line[..<range.lowerBound].utf8.count
            for split in 1..<marker.utf8.count {
                // Align each interior byte of the marker with a reverse chunk edge.
                let padding = 65_536 + markerOffset + split - line.utf8.count
                let url = try thread("split-\(type)-\(split)", body: line + String(repeating: " ", count: padding - 1) + "\n")
                XCTAssertEqual(CodexTurnActivity.read(url), .init(isRunning: type == "task_started", since: now),
                               "type=\(type), split=\(split)")
            }
        }
    }

    func testInitialPartialRecordAndNonBoundaryAppendsStayInvisible() throws {
        let started = event("task_started", at: now)
        let url = try thread("partial-append", body: String(started.dropLast()))
        var reader = CodexTurnActivity.Reader()
        XCTAssertNil(reader.read(url))
        try append("\n", to: url)
        XCTAssertEqual(reader.read(url)?.isRunning, true)
        try append("{\"type\":\"response_item\",\"payload\":{\"type\":\"task_complete\"}}\n", to: url)
        XCTAssertEqual(reader.read(url)?.isRunning, true)
    }

    func testRestartedTurnUsesItsOwnStartTime() throws {
        let url = try thread("restart", body: event("task_started", at: now.addingTimeInterval(-300))
                             + event("turn_aborted", at: now.addingTimeInterval(-200))
                             + event("task_started", at: now.addingTimeInterval(-100)))
        XCTAssertEqual(CodexTurnActivity.read(url)?.since, now.addingTimeInterval(-100))
    }

    func testAnUnterminatedOnlyRecordIsNotConsumed() throws {
        let url = try thread("partial", body: event("task_started", at: now).trimmingCharacters(in: .newlines))
        XCTAssertNil(CodexTurnActivity.read(url))
    }

    func testBookkeepingAndOldOrphanedTurnsDoNotShowWork() async throws {
        try thread("metadata", body: "{\"type\":\"session_meta\",\"payload\":{}}\n")
        let url = try thread("orphan", body: event("task_started", at: now.addingTimeInterval(-86_400)))
        try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-86_400)], ofItemAtPath: url.path)
        let sessions = await CodexActivityReader().read(stateStore: store, desktopStore: desktop, now: now)
        XCTAssertTrue(sessions.isEmpty)
    }

    func testGenericFolderUsesTaskTitleAndSavedProjectTakesPriority() throws {
        try thread("generic", body: "", cwd: "/work/Codex", name: "Telegram Assistant")
        XCTAssertEqual(CodexStore.threads(in: store).first?.projectName, "Telegram Assistant")
        try execute("ALTER TABLE threads ADD COLUMN project_id TEXT")
        try execute("CREATE TABLE projects (id TEXT, name TEXT)")
        try execute("INSERT INTO projects VALUES ('project', '모아')")
        try execute("UPDATE threads SET project_id = 'project'")
        XCTAssertEqual(CodexStore.threads(in: store).first?.projectName, "모아")
    }

    func testUsageResolvesExistingGenericNamesWithoutChangingAccounting() async throws {
        try thread("generic", body: "", cwd: "/work/Codex", name: "Telegram Assistant")
        let usageDB = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let id = SHA256.hash(data: Data("generic".utf8)).map { String(format: "%02x", $0) }.joined()
        let usage = TokenUsage(inputTokens: 100, cachedInputTokens: 20, outputTokens: 10)
        var checkpoint = SourceCheckpoint.fresh(sourcePath: "source", fileIdentity: "fixture")
        checkpoint.sessionID = id
        checkpoint.projectPath = "existing-project-id"
        checkpoint.projectName = "Codex"
        _ = try await usageDB.commit(events: [UsageEvent(eventKey: "one", occurredAt: now,
            sessionID: id, model: "gpt-6-astra", projectPath: checkpoint.projectPath,
            usage: usage, sourcePath: "source", sourcePosition: 0),
            UsageEvent(eventKey: "two", occurredAt: now, sessionID: id, model: "gpt-6-astra",
                       projectPath: "older-project-id", usage: usage, sourcePath: "source", sourcePosition: 1)
        ], checkpoint: checkpoint, normalizationState: nil)
        let original = try await usageDB.analyticsSnapshot(range: .today, through: now, calendar: .current)
        let collector = CodexUsageCollector(database: usageDB, roots: [root.appendingPathComponent("sessions")])
        let displayed = try await collector.analyticsSnapshot(range: .today, through: now)
        XCTAssertEqual(displayed.projects.first?.name, "Telegram Assistant")
        XCTAssertEqual(displayed.projects.count, 2)
        XCTAssertTrue(displayed.projects.allSatisfy { $0.name == "Telegram Assistant" })
        XCTAssertEqual(displayed.sessions.first?.displayName, "Telegram Assistant")
        XCTAssertEqual(displayed.projects.first?.id, original.projects.first?.id)
        XCTAssertEqual(displayed.usage, original.usage)
        XCTAssertEqual(displayed.buckets, original.buckets)
        XCTAssertEqual(displayed.models, original.models)
        let stored = try await usageDB.analyticsSnapshot(range: .today, through: now, calendar: .current)
        XCTAssertEqual(stored, original)
    }
}
