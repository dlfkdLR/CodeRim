import Darwin
import XCTest
@testable import CodeRim

/// The state machine that reads a session's transcript.
///
/// It exists because Claude Code only writes `status` into the session registry
/// from its terminal interface, so a session hosted by the Claude desktop app
/// has no status at all. Every fixture here is the shape of a line found in a
/// real transcript on a Mac running the desktop app.
final class NotchClaudeTranscriptTests: XCTestCase {
    private func turn(_ lines: String...) -> ClaudeTranscript.Turn? {
        ClaudeTranscript.turn(inTail: Data(lines.joined(separator: "\n").utf8))
    }

    // MARK: - Where the file is

    func testSlugReplacesSeparatorsAndDots() {
        XCTAssertEqual(ClaudeTranscript.projectSlug(forCWD: "/Users/vinz/notch-app"),
                       "-Users-vinz-notch-app")
        // A worktree under a dot directory: both characters collapse, and the
        // run of two dashes is what the real directory names look like.
        XCTAssertEqual(
            ClaudeTranscript.projectSlug(forCWD: "/Users/vinz/app/.claude/worktrees/x"),
            "-Users-vinz-app--claude-worktrees-x"
        )
    }

    // MARK: - What it says

    func testAToolInFlightIsWork() {
        XCTAssertEqual(turn(#"{"type":"assistant","message":{"stop_reason":"tool_use"}}"#),
                       .inFlight)
    }

    func testTheEndOfATurnIsNotWork() {
        XCTAssertEqual(turn(#"{"type":"assistant","message":{"stop_reason":"end_turn"}}"#),
                       .finished)
        XCTAssertEqual(turn(#"{"type":"assistant","message":{"stop_reason":"stop_sequence"}}"#),
                       .finished)
    }

    /// Claude Code records each streamed block separately; a thinking block
    /// carries `end_turn` and is followed by the answer, so it is mid-turn.
    func testAThinkingBlockIsNotTheEndOfTheTurn() {
        XCTAssertEqual(turn(#"{"type":"assistant","message":{"stop_reason":"end_turn","content":[{"type":"thinking"}]}}"#),
                       .inFlight)
        XCTAssertEqual(turn(#"{"type":"assistant","message":{"stop_reason":"end_turn","content":[{"type":"text"}]}}"#),
                       .finished)
    }

    func testABlockStillStreamingIsNotTheEndOfTheTurn() {
        XCTAssertEqual(turn(#"{"type":"assistant","message":{"stop_reason":null,"content":[{"type":"text"}]}}"#),
                       .inFlight)
    }

    /// A tool result means the tool is done and the model is thinking about it,
    /// which is the part of a turn that looks quietest from outside.
    func testAToolResultIsStillWork() {
        XCTAssertEqual(turn("""
        {"type":"user","message":{"content":[{"type":"tool_result","content":"ok"}]}}
        """), .inFlight)
    }

    func testAFreshPromptIsWork() {
        XCTAssertEqual(turn(#"{"type":"user","message":{"content":"build the thing"}}"#),
                       .inFlight)
    }

    /// Esc writes a record saying so, which is why a stopped session is
    /// something the notch knows rather than something it waits out.
    func testAnInterruptionEndsTheTurn() {
        XCTAssertEqual(turn("""
        {"type":"user","message":{"content":[{"type":"text","text":"[Request interrupted by user]"}]}}
        """), .finished)
        XCTAssertEqual(turn("""
        {"type":"user","message":{"content":"[Request interrupted by user for tool use]"}}
        """), .finished)
    }

    /// The reason a plain "was the file touched recently" test cannot work
    /// here: Claude Code keeps appending these to a session that is sitting
    /// doing nothing, and on this Mac that made an hour-old parked session look
    /// like it was working.
    func testBookkeepingNeverDecides() {
        XCTAssertEqual(turn(
            #"{"type":"assistant","message":{"stop_reason":"end_turn"}}"#,
            #"{"type":"attachment"}"#,
            #"{"type":"bridge-session"}"#,
            #"{"type":"atis-latch"}"#,
            #"{"type":"frame-link"}"#,
            #"{"type":"custom-title"}"#,
            #"{"type":"queue-operation","operation":"enqueue"}"#,
            #"{"type":"system","subtype":"stop_hook_summary"}"#
        ), .finished)
    }

    /// An unknown kind must not be read as work either — the list of things
    /// Claude Code writes grows between releases, so only `user` and
    /// `assistant` are allowed to answer.
    func testAnUnknownKindNeverDecides() {
        XCTAssertEqual(turn(
            #"{"type":"assistant","message":{"stop_reason":"tool_use"}}"#,
            #"{"type":"something-shipped-next-month"}"#
        ), .inFlight)
    }

    /// A subagent's records are interleaved with its parent's; the parent's
    /// turn is the one being reported on.
    func testASidechainIsIgnored() {
        XCTAssertEqual(turn(
            #"{"type":"assistant","message":{"stop_reason":"tool_use"}}"#,
            #"{"type":"assistant","isSidechain":true,"message":{"stop_reason":"end_turn"}}"#
        ), .inFlight)
    }

    /// The tail starts mid-file, so its first line is usually half a record.
    func testAHalfLineAtTheFrontIsSkipped() {
        XCTAssertEqual(turn(
            #"or":"tool_use"}}"#,
            #"{"type":"assistant","message":{"stop_reason":"end_turn"}}"#
        ), .finished)
    }

    /// A session opened and not yet used says nothing, and the monitor leaves
    /// it as the registry described it rather than guessing.
    func testNothingSaidYetIsUnknown() {
        XCTAssertNil(turn(#"{"type":"bridge-session"}"#))
        XCTAssertNil(ClaudeTranscript.turn(inTail: Data()))
    }
}

/// The reader that puts the state machine on a timer.
final class NotchClaudeTranscriptReaderTests: XCTestCase {
    private var projects: URL!

    override func setUpWithError() throws {
        projects = FileManager.default.temporaryDirectory
            .appendingPathComponent("transcripts-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: projects, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: projects)
    }

    @discardableResult
    private func write(_ body: String, folder: String, session: String) throws -> URL {
        let directory = projects.appendingPathComponent(folder)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let url = directory.appendingPathComponent("\(session).jsonl")
        try Data(body.utf8).write(to: url)
        return url
    }

    private let working = #"{"type":"assistant","message":{"stop_reason":"tool_use"}}"# + "\n"
    private let done = #"{"type":"assistant","message":{"stop_reason":"end_turn"}}"# + "\n"

    func testReadsTheTranscriptUnderTheSlugForTheWorkingDirectory() throws {
        try write(working, folder: "-Users-vinz-app", session: "abc")
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertEqual(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")?.turn, .inFlight)
    }

    /// A session resumed somewhere else keeps its transcript where it was first
    /// written, so the slug no longer names it.
    func testFindsATranscriptThatIsNotUnderItsOwnSlug() throws {
        try write(done, folder: "-Users-vinz-elsewhere", session: "abc")
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertEqual(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")?.turn, .finished)
    }

    func testListsOnlyTheSubagentsStillWorking() throws {
        try write(working, folder: "-Users-vinz-app", session: "abc")
        let directory = projects.appendingPathComponent("-Users-vinz-app/abc/subagents")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        func agent(_ id: String, meta: String, age: TimeInterval = 0) throws {
            try Data(meta.utf8).write(to: directory.appendingPathComponent("agent-\(id).meta.json"))
            let log = directory.appendingPathComponent("agent-\(id).jsonl")
            try Data("{}\n".utf8).write(to: log)
            try FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(-age)],
                                                  ofItemAtPath: log.path)
        }
        try agent("live", meta: #"{"description":"Write the docs"}"#)
        try agent("done", meta: #"{"description":"Old","finished":true}"#)
        try agent("stale", meta: #"{"description":"Abandoned"}"#, age: 600)
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertEqual(reader.subagents(sessionID: "abc", cwd: "/Users/vinz/app").map(\.title), ["Write the docs"])
    }

    func testTitleFollowsTheLatestGeneratedTitleAndPrefersACustomOne() throws {
        let url = try write(working + #"{"type":"ai-title","aiTitle":"First idea","sessionId":"abc"}"# + "\n",
                            folder: "-Users-vinz-app", session: "abc")
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertEqual(reader.title(sessionID: "abc", cwd: "/Users/vinz/app"), "First idea")

        let handle = try FileHandle(forWritingTo: url)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data((#"{"type":"ai-title","aiTitle":"전체 코드 리뷰","sessionId":"abc"}"# + "\n").utf8))
        XCTAssertEqual(reader.title(sessionID: "abc", cwd: "/Users/vinz/app"), "전체 코드 리뷰")

        // A half-written line is left for the next read.
        try handle.write(contentsOf: Data(#"{"type":"custom-title","customTitle":"Renamed""#.utf8))
        XCTAssertEqual(reader.title(sessionID: "abc", cwd: "/Users/vinz/app"), "전체 코드 리뷰")
        try handle.write(contentsOf: Data((#","sessionId":"abc"}"# + "\n" + done).utf8))
        try handle.close()
        XCTAssertEqual(reader.title(sessionID: "abc", cwd: "/Users/vinz/app"), "Renamed")
    }

    func testNoTitleUntilClaudeCodeWritesOne() throws {
        try write(working, folder: "-Users-vinz-app", session: "abc")
        XCTAssertNil(ClaudeTranscriptReader(projects: projects).title(sessionID: "abc", cwd: "/Users/vinz/app"))
    }

    func testSaysNothingWhenThereIsNoTranscript() {
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertNil(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app"))
    }

    /// `since` is when the turn last moved, which is what the tooltip counts
    /// from — not the moment the notch happened to look.
    func testReportsWhenTheTranscriptLastMoved() throws {
        let url = try write(working, folder: "-Users-vinz-app", session: "abc")
        let when = Date(timeIntervalSince1970: 1_700_000_000)
        try FileManager.default.setAttributes([.modificationDate: when], ofItemAtPath: url.path)
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertEqual(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")?.since, when)
    }

    /// The whole point of the cache: a tick that finds the file unchanged must
    /// not read it again. Rewriting the body without touching the timestamps
    /// leaves the reader on its old answer.
    func testAnUnchangedFileIsNotReadTwice() throws {
        let url = try write(working, folder: "-Users-vinz-app", session: "abc")
        // Pinned to the same whole second on both sides, so the only thing this
        // can be measuring is the cache.
        let frozen = Date(timeIntervalSince1970: 1_700_000_000)
        try FileManager.default.setAttributes([.modificationDate: frozen], ofItemAtPath: url.path)
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertEqual(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")?.turn, .inFlight)

        // Same length as well, so neither size nor date moves.
        XCTAssertEqual(working.utf8.count, done.utf8.count)
        try Data(done.utf8).write(to: url)
        try FileManager.default.setAttributes([.modificationDate: frozen], ofItemAtPath: url.path)
        XCTAssertEqual(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")?.turn, .inFlight)
    }

    private func call(at stamp: String, ids: [String], sidechain: Bool = false) -> String {
        let blocks = ids.map { #"{"type":"tool_use","id":"\#($0)"}"# }.joined(separator: ",")
        let side = sidechain ? #""isSidechain":true,"# : ""
        return #"{"type":"assistant",\#(side)"timestamp":"\#(stamp)","message":{"stop_reason":"tool_use","content":[\#(blocks)]}}"# + "\n"
    }

    func testToolCallsAreCountedByBlockAndASubagentsAreLeftOut() {
        let log = call(at: "2026-10-29T00:00:10.000Z", ids: ["a", "b"])
            + call(at: "2026-10-29T00:00:11.000Z", ids: ["s"], sidechain: true)
            + call(at: "2026-10-29T00:00:12.000Z", ids: ["c"])
        XCTAssertEqual(ClaudeTranscript.toolUses(inTail: Data(log.utf8)).count, 3)
    }

    func testToolCallsKeepCountingAsTheFileGrowsAndOnlyThisTurnsCount() throws {
        let begun = ClaudeTranscriptReader.toolCalls(
            [Date(timeIntervalSince1970: 10), Date(timeIntervalSince1970: 20), Date(timeIntervalSince1970: 30)],
            since: Date(timeIntervalSince1970: 20))
        XCTAssertEqual(begun, 2)

        // Calls older than a turn could be are dropped, so these are made now.
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let first = call(at: formatter.string(from: Date().addingTimeInterval(-20)), ids: ["a"])
        let url = try write(first, folder: "-Users-vinz-app", session: "abc")
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertEqual(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")?.toolUses.count, 1)

        let more = call(at: formatter.string(from: Date().addingTimeInterval(-10)), ids: ["b", "c"])
        try Data((first + more).utf8).write(to: url)
        XCTAssertEqual(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")?.toolUses.count, 3)
    }

    func testRereadsOnceTheFileHasGrown() throws {
        let url = try write(working, folder: "-Users-vinz-app", session: "abc")
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertEqual(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")?.turn, .inFlight)

        try Data((working + done).utf8).write(to: url)
        XCTAssertEqual(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")?.turn, .finished)
    }
}

/// `since` means when the state was entered, not when the file was last
/// touched. Reporting the latter froze the tooltip's elapsed time at zero for
/// the whole of a long turn, and gave the notch a fresh value to animate every
/// two seconds while nothing visible had changed.
extension NotchClaudeTranscriptReaderTests {
    func testTheStartOfTheStateIsHeldWhileItLasts() throws {
        let url = try write(working, folder: "-Users-vinz-app", session: "abc")
        let started = Date(timeIntervalSince1970: 1_700_000_000)
        try FileManager.default.setAttributes([.modificationDate: started],
                                              ofItemAtPath: url.path)
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertEqual(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")?.since, started)

        // The turn goes on: more records, a newer timestamp, same state.
        try Data((working + working).utf8).write(to: url)
        try FileManager.default.setAttributes([.modificationDate: started.addingTimeInterval(30)],
                                              ofItemAtPath: url.path)
        XCTAssertEqual(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")?.since, started)
    }

    func testTheStartMovesWhenTheStateDoes() throws {
        let url = try write(working, folder: "-Users-vinz-app", session: "abc")
        let started = Date(timeIntervalSince1970: 1_700_000_000)
        try FileManager.default.setAttributes([.modificationDate: started],
                                              ofItemAtPath: url.path)
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertEqual(reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")?.since, started)

        let ended = started.addingTimeInterval(120)
        try Data((working + done).utf8).write(to: url)
        try FileManager.default.setAttributes([.modificationDate: ended], ofItemAtPath: url.path)
        let after = reader.activity(sessionID: "abc", cwd: "/Users/vinz/app")
        XCTAssertEqual(after?.turn, .finished)
        XCTAssertEqual(after?.since, ended)
    }
}

extension NotchClaudeTranscriptReaderTests {
    func testTitleAfterALargeTranscriptAndOversizedUnfinishedLine() throws {
        let url = try write("", folder: "-Users-vinz-app", session: "abc")
        let output = try FileHandle(forWritingTo: url)
        defer { try? output.close() }
        let chunk = Data(repeating: 0x20, count: TranscriptLineCursor.chunkBytes)
        for _ in 0..<128 { try output.write(contentsOf: chunk) }
        try output.write(contentsOf: Data(("\n" + #"{"type":"ai-title","aiTitle":"Near EOF"}"# + "\n").utf8))
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertEqual(reader.title(sessionID: "abc", cwd: "/Users/vinz/app"), "Near EOF")
        for _ in 0..<128 { try output.write(contentsOf: chunk) }
        for _ in 0..<3 {
            XCTAssertEqual(reader.title(sessionID: "abc", cwd: "/Users/vinz/app"), "Near EOF")
        }
        try output.write(contentsOf: Data(("\n" + #"{"type":"custom-title","customTitle":"After skipped tail"}"# + "\n").utf8))
        XCTAssertEqual(reader.title(sessionID: "abc", cwd: "/Users/vinz/app"), "After skipped tail")
    }

    func testCompletedLargeTranscriptsReleaseCarryAndEvictOldTitleCursors() throws {
        let reader = ClaudeTranscriptReader(projects: projects)
        let padding = String(repeating: "x", count: 900 * 1024) + "\n"
        for index in 0..<80 {
            let title = "Title \(index)"
            try write(padding + "{\"type\":\"ai-title\",\"aiTitle\":\"\(title)\"}\n",
                      folder: "-Users-vinz-app", session: "s\(index)")
            XCTAssertEqual(reader.title(sessionID: "s\(index)", cwd: "/Users/vinz/app"), title)
        }
        let cached = try XCTUnwrap(Mirror(reflecting: reader).children.first { $0.label == "titles" }?.value
            as? [String: ClaudeTranscriptReader.TitleScan])
        XCTAssertLessThanOrEqual(cached.count, 64)
        XCTAssertNil(cached["s0"])
        XCTAssertTrue(cached.values.allSatisfy { $0.cursor.carry.isEmpty })
        XCTAssertEqual(reader.title(sessionID: "s0", cwd: "/Users/vinz/app"), "Title 0")
    }

    func testTitleSplitAcrossChunksAndRewriteAfterAnUnfinishedTail() throws {
        let prefix = String(repeating: " ", count: TranscriptLineCursor.chunkBytes - 20) + "\n"
        let url = try write(prefix + #"{"type":"ai-title","aiTitle":"Split title"}"# + "\n",
                            folder: "-Users-vinz-app", session: "abc")
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertEqual(reader.title(sessionID: "abc", cwd: "/Users/vinz/app"), "Split title")
        let output = try FileHandle(forWritingTo: url)
        try output.seekToEnd()
        try output.write(contentsOf: Data(repeating: 0x78, count: TranscriptLineCursor.lineBytes * 2))
        try output.close()
        XCTAssertEqual(reader.title(sessionID: "abc", cwd: "/Users/vinz/app"), "Split title")
        try Data((#"{"type":"ai-title","aiTitle":"Rewritten"}"# + "\n").utf8).write(to: url)
        XCTAssertEqual(reader.title(sessionID: "abc", cwd: "/Users/vinz/app"), "Rewritten")
    }

    func testBoundedCursorRetainsCompletedPositionWithoutRereadingOversizedTail() throws {
        let url = try write("ok\n" + String(repeating: "x", count: TranscriptLineCursor.lineBytes * 4),
                            folder: "-Users-vinz-app", session: "abc")
        let input = try FileHandle(forReadingFrom: url)
        defer { try? input.close() }
        var cursor = TranscriptLineCursor()
        let end = try input.seekToEnd()
        var lines: [Data] = []
        try cursor.read(input, through: end) { lines.append($0) }
        XCTAssertEqual(lines, [Data("ok".utf8)])
        XCTAssertEqual(cursor.offset, 3)
        XCTAssertEqual(cursor.readOffset, end)
        XCTAssertTrue(cursor.discarding)
        XCTAssertLessThanOrEqual(cursor.carry.count, TranscriptLineCursor.lineBytes)
        try cursor.read(input, through: end) { _ in XCTFail("Unfinished bytes were emitted") }
        XCTAssertEqual(cursor.readOffset, end)
        let output = try FileHandle(forWritingTo: url)
        try output.seekToEnd()
        try output.write(contentsOf: Data("\nkept\npartial".utf8))
        try output.close()
        try cursor.read(input, through: input.seekToEnd()) { lines.append($0) }
        XCTAssertEqual(lines, [Data("ok".utf8), Data("kept".utf8)])
        XCTAssertEqual(cursor.carry, Data("partial".utf8))
        XCTAssertEqual(cursor.offset, cursor.readOffset - 7)
    }

    func testActivityReaderSkipsOversizedAndUnfinishedRecords() throws {
        let url = try write("match " + String(repeating: "x", count: TranscriptLineCursor.lineBytes + 1)
                            + "\nmatch retained\nmatch unfinished", folder: "-Users-vinz-app", session: "abc")
        var lines: [String] = []
        TranscriptLines.forEachLine(of: url, containingAny: ["match"], copying: false) {
            lines.append(String(decoding: $0, as: UTF8.self))
        }
        XCTAssertEqual(lines, ["match retained"])
    }

    @MainActor
    func testInitialMonitorScanKeepsTheMainActorResponsiveAndStopRejectsLatePublication() async throws {
        let registry = projects.appendingPathComponent("sessions")
        try FileManager.default.createDirectory(at: registry, withIntermediateDirectories: true)
        let started = Int((ProcessLiveness.startTime(pid: getpid()) ?? Date()).timeIntervalSince1970 * 1000)
        let metadata = """
        {"pid":\(getpid()),"sessionId":"abc","cwd":"/Users/vinz/app","startedAt":\(started),"status":"busy","kind":"interactive"}
        """
        try metadata.write(to: registry.appendingPathComponent("session.json"), atomically: true, encoding: .utf8)
        let url = try write("", folder: "-Users-vinz-app", session: "abc")
        let output = try FileHandle(forWritingTo: url)
        let chunk = Data(String(repeating: #"{"type":"progress"}"# + "\n", count: 4096).utf8)
        for _ in 0..<128 { try output.write(contentsOf: chunk) }
        try output.write(contentsOf: Data((#"{"type":"ai-title","aiTitle":"Late title"}"# + "\n").utf8))
        try output.close()
        let monitor = ClaudeSessionMonitor(directory: registry, projects: projects, livenessInterval: 60)
        defer { monitor.stop() }
        let start = ContinuousClock.now
        monitor.start()
        XCTAssertLessThan(start.duration(to: .now), .milliseconds(500))
        var beats = 0
        let deadline = ContinuousClock.now + .seconds(15)
        while monitor.sessions.isEmpty, ContinuousClock.now < deadline {
            let before = ContinuousClock.now
            try await Task.sleep(for: .milliseconds(10))
            XCTAssertLessThan(before.duration(to: .now), .milliseconds(500))
            beats += 1
        }
        XCTAssertGreaterThan(beats, 0)
        XCTAssertEqual(monitor.sessions.first?.name, "Late title")
        // A stopped generation must never publish a queued scan's results.
        let append = try FileHandle(forWritingTo: url)
        try append.seekToEnd()
        try append.write(contentsOf: Data((#"{"type":"custom-title","customTitle":"Restarted"}"# + "\n").utf8))
        try append.close()
        monitor.start()
        monitor.stop()
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(monitor.sessions.first?.name, "Late title")
        monitor.start()
        let restartDeadline = ContinuousClock.now + .seconds(5)
        while monitor.sessions.first?.name != "Restarted", ContinuousClock.now < restartDeadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(monitor.sessions.first?.name, "Restarted")
    }
}

extension NotchClaudeTranscriptTests {
    func testTailUsesTheRequestedBoundAndRejectsLinks() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let file = root.appendingPathComponent("transcript.jsonl")
        let expected = Data("the requested tail".utf8)
        try (Data(repeating: 0x61, count: 100_000) + expected).write(to: file)
        XCTAssertEqual(ClaudeTranscript.tail(of: file, bytes: expected.count), expected)
        XCTAssertNil(ClaudeTranscript.tail(of: file, bytes: 0))
        XCTAssertNil(ClaudeTranscript.tail(of: file, bytes: -1))
        let link = root.appendingPathComponent("link.jsonl")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: file)
        XCTAssertNil(ClaudeTranscript.tail(of: link))
    }
}

extension NotchClaudeTranscriptReaderTests {
    func testRetiredSessionsDoNotKeepAnOldTranscriptLocation() throws {
        let reader = ClaudeTranscriptReader(projects: projects)
        let original = try write(working, folder: "old-folder", session: "resumed")
        XCTAssertEqual(reader.activity(sessionID: "resumed", cwd: "/tmp/moved")?.turn, .inFlight)
        _ = ClaudeSessionMonitor.read(directory: projects.appendingPathComponent("no-registry"), transcripts: reader)
        try FileManager.default.removeItem(at: original)
        try write(done, folder: "new-folder", session: "resumed")
        XCTAssertEqual(reader.activity(sessionID: "resumed", cwd: "/tmp/moved")?.turn, .finished)
    }

    func testRetiredMissingSessionCanBeDiscoveredWhenItReturns() throws {
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertNil(reader.activity(sessionID: "resumed", cwd: "/tmp/moved"))
        _ = ClaudeSessionMonitor.read(directory: projects.appendingPathComponent("no-registry"), transcripts: reader)
        try write(done, folder: "new-folder", session: "resumed")
        XCTAssertEqual(reader.activity(sessionID: "resumed", cwd: "/tmp/moved")?.turn, .finished)
    }
}


extension NotchClaudeTranscriptReaderTests {
    func testACompletedSubagentInOneChatCannotHideTheSameIDInAnother() throws {
        let cwd = "/Users/vinz/app"
        for session in ["first", "second"] {
            try write(working, folder: "-Users-vinz-app", session: session)
            let folder = projects.appendingPathComponent("-Users-vinz-app/\(session)/subagents")
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let meta = session == "first" ? #"{"finished":true}"# : #"{"description":"Still working"}"#
            try Data(meta.utf8).write(to: folder.appendingPathComponent("agent-same.meta.json"))
            try Data("{}\n".utf8).write(to: folder.appendingPathComponent("agent-same.jsonl"))
        }
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertTrue(reader.subagents(sessionID: "first", cwd: cwd).isEmpty)
        XCTAssertEqual(reader.subagents(sessionID: "second", cwd: cwd).map(\.title), ["Still working"])
    }
}


extension NotchClaudeTranscriptReaderTests {
    func testQuietSubagentCanBecomeActiveAgain() throws {
        let cwd = "/Users/vinz/app", session = "quiet-resume"
        try write(working, folder: "-Users-vinz-app", session: session)
        let folder = projects.appendingPathComponent("-Users-vinz-app/\(session)/subagents")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let meta = folder.appendingPathComponent("agent-wait.meta.json")
        let log = folder.appendingPathComponent("agent-wait.jsonl")
        try Data(#"{"description":"Long-running task"}"#.utf8).write(to: meta)
        try Data("{}\n".utf8).write(to: log)
        let now = Date()
        try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-3700)], ofItemAtPath: log.path)
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertTrue(reader.subagents(sessionID: session, cwd: cwd, now: now).isEmpty)
        try Data("{}\n{}\n".utf8).write(to: log)
        try FileManager.default.setAttributes([.modificationDate: now], ofItemAtPath: log.path)
        XCTAssertEqual(reader.subagents(sessionID: session, cwd: cwd, now: now).map(\.title), ["Long-running task"])
    }

    func testFinishedSubagentMetadataCanBeReplacedByResumedWork() throws {
        let cwd = "/Users/vinz/app", session = "finished-resume"
        try write(working, folder: "-Users-vinz-app", session: session)
        let folder = projects.appendingPathComponent("-Users-vinz-app/\(session)/subagents")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let meta = folder.appendingPathComponent("agent-resume.meta.json")
        let log = folder.appendingPathComponent("agent-resume.jsonl")
        try Data(#"{"finished":true}"#.utf8).write(to: meta)
        try Data("{}\n".utf8).write(to: log)
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertTrue(reader.subagents(sessionID: session, cwd: cwd).isEmpty)
        try Data(#"{"finished":false,"description":"Resumed work"}"#.utf8).write(to: meta, options: .atomic)
        try Data("{}\n{}\n".utf8).write(to: log)
        XCTAssertEqual(reader.subagents(sessionID: session, cwd: cwd).map(\.title), ["Resumed work"])
    }

    func testSubagentCacheBenchmarkWhenRequested() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["CODERIM_RUN_SUBAGENT_BENCHMARK"] == "1", "Opt-in local metadata-cache measurement.")
        let cwd = "/Users/vinz/app", session = "cache-benchmark"
        try write(working, folder: "-Users-vinz-app", session: session)
        let folder = projects.appendingPathComponent("-Users-vinz-app/\(session)/subagents")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for index in 0..<1000 {
            try Data(#"{"finished":true}"#.utf8).write(to: folder.appendingPathComponent("agent-bench-\(index).meta.json"))
            try Data("{}\n".utf8).write(to: folder.appendingPathComponent("agent-bench-\(index).jsonl"))
        }
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertTrue(reader.subagents(sessionID: session, cwd: cwd).isEmpty)
        var samples: [Double] = []
        for _ in 0..<10 {
            let start = DispatchTime.now().uptimeNanoseconds
            let values = reader.subagents(sessionID: session, cwd: cwd)
            samples.append(Double(DispatchTime.now().uptimeNanoseconds - start) / 1_000_000)
            XCTAssertTrue(values.isEmpty)
        }
        print("CodeRim subagent cache: 1000 settled files, warm milliseconds=\(samples), median=\(samples.sorted()[5])")
    }
}


extension NotchClaudeTranscriptReaderTests {
    func testAtomicMetadataReplacementReopensResumedSubagent() throws {
        let cwd = "/Users/vinz/app", session = "atomic-resume"
        try write(working, folder: "-Users-vinz-app", session: session)
        let folder = projects.appendingPathComponent("-Users-vinz-app/\(session)/subagents")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let meta = folder.appendingPathComponent("agent-atomic.meta.json")
        let log = folder.appendingPathComponent("agent-atomic.jsonl")
        let stopped = Data(#"{"finished":true,"description":"done"}"#.utf8)
        let resumed = Data(#"{"finished":false,"description":"run"}"#.utf8)
        XCTAssertEqual(stopped.count, resumed.count)
        try stopped.write(to: meta)
        try Data("{}\n".utf8).write(to: log)
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertTrue(reader.subagents(sessionID: session, cwd: cwd).isEmpty)
        var original = stat()
        XCTAssertEqual(lstat(meta.path, &original), 0)
        let replacement = folder.appendingPathComponent("replacement.tmp")
        try resumed.write(to: replacement)
        let times = [original.st_atimespec, original.st_mtimespec]
        let result = times.withUnsafeBufferPointer { Darwin.utimensat(AT_FDCWD, replacement.path, $0.baseAddress, 0) }
        XCTAssertEqual(result, 0)
        XCTAssertEqual(rename(replacement.path, meta.path), 0)
        var current = stat()
        XCTAssertEqual(lstat(meta.path, &current), 0)
        XCTAssertEqual(current.st_size, original.st_size)
        XCTAssertEqual(current.st_mtimespec.tv_sec, original.st_mtimespec.tv_sec)
        XCTAssertEqual(current.st_mtimespec.tv_nsec, original.st_mtimespec.tv_nsec)
        XCTAssertNotEqual(current.st_ino, original.st_ino)
        XCTAssertEqual(reader.subagents(sessionID: session, cwd: cwd).map(\.title), ["run"])
    }

    func testFinishedMetadataStillSuppressesLaterLogWrites() throws {
        let cwd = "/Users/vinz/app", session = "finished-log"
        try write(working, folder: "-Users-vinz-app", session: session)
        let folder = projects.appendingPathComponent("-Users-vinz-app/\(session)/subagents")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let meta = folder.appendingPathComponent("agent-done.meta.json")
        let log = folder.appendingPathComponent("agent-done.jsonl")
        try Data(#"{"finished":true,"description":"Done"}"#.utf8).write(to: meta)
        try Data("{}\n".utf8).write(to: log)
        let reader = ClaudeTranscriptReader(projects: projects)
        XCTAssertTrue(reader.subagents(sessionID: session, cwd: cwd).isEmpty)
        try Data("{}\n{}\n".utf8).write(to: log)
        XCTAssertTrue(reader.subagents(sessionID: session, cwd: cwd).isEmpty)
    }
}


extension NotchClaudeTranscriptReaderTests {
    func testMetadataPathABAIsNotCachedAsTheReadInode() throws {
        let cwd = "/Users/vinz/app", session = "metadata-aba"
        try write(working, folder: "-Users-vinz-app", session: session)
        let folder = projects.appendingPathComponent("-Users-vinz-app/\(session)/subagents")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let meta = folder.appendingPathComponent("agent-aba.meta.json")
        let other = folder.appendingPathComponent("other.tmp")
        let saved = folder.appendingPathComponent("saved.tmp")
        let original = Data(#"{"finished":false,"description":"A"}"#.utf8)
        let temporary = Data(#"{"finished":true ,"description":"B"}"#.utf8)
        XCTAssertEqual(original.count, temporary.count)
        try original.write(to: meta)
        try temporary.write(to: other)
        try Data("{}\n".utf8).write(to: folder.appendingPathComponent("agent-aba.jsonl"))
        var before = stat()
        XCTAssertEqual(lstat(meta.path, &before), 0)
        let times = [before.st_atimespec, before.st_mtimespec]
        XCTAssertEqual(times.withUnsafeBufferPointer { Darwin.utimensat(AT_FDCWD, other.path, $0.baseAddress, 0) }, 0)
        let reader = ClaudeTranscriptReader(projects: projects)
        let during = ClaudeTranscriptReader.$beforeSubagentMetadataOpenForTesting.withValue({ url in
            XCTAssertEqual(rename(url.path, saved.path), 0)
            XCTAssertEqual(rename(other.path, url.path), 0)
        }) {
            ClaudeTranscriptReader.$afterSubagentMetadataOpenForTesting.withValue({ url in
                XCTAssertEqual(rename(url.path, other.path), 0)
                XCTAssertEqual(rename(saved.path, url.path), 0)
            }) {
                reader.subagents(sessionID: session, cwd: cwd)
            }
        }
        XCTAssertTrue(during.isEmpty) // The temporary B bytes were read while B occupied the name.
        var restored = stat()
        XCTAssertEqual(lstat(meta.path, &restored), 0)
        XCTAssertEqual(restored.st_dev, before.st_dev)
        XCTAssertEqual(restored.st_ino, before.st_ino)
        XCTAssertEqual(restored.st_size, before.st_size)
        XCTAssertEqual(restored.st_mtimespec.tv_sec, before.st_mtimespec.tv_sec)
        XCTAssertEqual(restored.st_mtimespec.tv_nsec, before.st_mtimespec.tv_nsec)
        XCTAssertEqual(try Data(contentsOf: meta), original)
        XCTAssertEqual(reader.subagents(sessionID: session, cwd: cwd).map(\.title), ["A"])
        XCTAssertEqual(reader.subagents(sessionID: session, cwd: cwd).map(\.title), ["A"])
    }
}
