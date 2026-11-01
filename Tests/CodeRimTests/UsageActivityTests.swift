import XCTest
@testable import CodeRim

final class UsageActivityTests: XCTestCase {
    private var utc: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        return calendar
    }
    private func day(_ string: String) -> Date { utc.startOfDay(for: ISO8601DateFormatter().date(from: string + "T00:00:00Z")!) }
    private func line(_ text: String) -> Data { Data(text.utf8) }

    // MARK: - Claude

    private func assistant(model: String = "claude-opus-5-5", id: String = "msg_01A", stamp: String,
                           blocks: String, speed: String = "standard", sidechain: Bool = false) -> Data {
        line(#"{"parentUuid":"p","isSidechain":\#(sidechain),"message":{"model":"\#(model)","id":"\#(id)","type":"message","role":"assistant","content":[\#(blocks)],"usage":{"speed":"\#(speed)"}},"type":"assistant","timestamp":"\#(stamp)","sessionId":"s"}"#)
    }

    private func user(_ content: String, stamp: String, extra: String = "") -> Data {
        line(#"{"isSidechain":false,\#(extra)"type":"user","message":{"role":"user","content":\#(content)},"timestamp":"\#(stamp)"}"#)
    }

    func testClaudeMessagesAreCreditedToTheModelThatAnsweredThem() {
        var parser = ClaudeActivityParser(days: ActivityDayResolver(calendar: utc))
        parser.consume(user(#""fix it""#, stamp: "2026-10-29T01:00:00.000Z"))
        parser.consume(user(#"[{"type":"tool_result","tool_use_id":"t","content":"ok"}]"#, stamp: "2026-10-29T01:00:05.000Z"))
        parser.consume(user(#""caveat""#, stamp: "2026-10-29T01:00:06.000Z", extra: #""isMeta":true,"#))
        parser.consume(assistant(model: "claude-sonnet-5-5", stamp: "2026-10-29T01:00:10.000Z",
                                 blocks: #"{"type":"text","text":"ok"}"#))
        parser.consume(user(#""again""#, stamp: "2026-10-30T02:00:00.000Z"))
        parser.consume(assistant(model: "claude-opus-5-5", id: "msg_02B", stamp: "2026-10-30T02:00:09.000Z",
                                 blocks: #"{"type":"text","text":"ok"}"#))
        let activity = parser.finish()
        XCTAssertEqual(activity.days[day("2026-10-29")]?.messagesByModel, ["claude-sonnet-5-5": 1])
        XCTAssertEqual(activity.days[day("2026-10-30")]?.messagesByModel, ["claude-opus-5-5": 1])
    }

    func testClaudeCountsToolsByServerAndSkillsByName() {
        var parser = ClaudeActivityParser(days: ActivityDayResolver(calendar: utc))
        let blocks = [
            #"{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}}"#,
            #"{"type":"tool_use","id":"toolu_2","name":"Skill","input":{"skill":"cua-driver"}}"#,
            #"{"type":"tool_use","id":"toolu_3","name":"mcp__claude_ai_Notion__notion-fetch","input":{}}"#,
            #"{"type":"tool_use","id":"toolu_4","name":"mcp__claude_ai_Notion__notion-search","input":{}}"#,
        ].joined(separator: ",")
        parser.consume(assistant(stamp: "2026-10-29T01:00:10.000Z", blocks: blocks))
        let counts = parser.finish().days[day("2026-10-29")]
        XCTAssertEqual(counts?.tools, ["Bash": 1, "Skill": 1, "Notion": 2])
        XCTAssertEqual(counts?.skills, ["cua-driver": 1])
    }

    func testClaudeASubagentsToolCallsCountButItsMessagesAndModelDoNot() {
        var parser = ClaudeActivityParser(days: ActivityDayResolver(calendar: utc))
        parser.consume(assistant(model: "claude-haiku-5-5", id: "msg_side", stamp: "2026-10-29T01:00:10.000Z",
                                 blocks: #"{"type":"tool_use","id":"t","name":"Read","input":{}}"#, sidechain: true))
        let activity = parser.finish()
        XCTAssertEqual(activity.days[day("2026-10-29")]?.tools, ["Read": 1])
        XCTAssertTrue(activity.traits.isEmpty)
    }

    func testClaudeOneAnswerSplitAcrossLinesIsOneTurnOfItsModelAndSpeed() {
        var parser = ClaudeActivityParser(days: ActivityDayResolver(calendar: utc))
        for _ in 0..<3 {
            parser.consume(assistant(id: "msg_same", stamp: "2026-10-29T01:00:10.000Z", blocks: #"{"type":"text","text":"a"}"#))
        }
        parser.consume(assistant(model: "claude-sonnet-5-5", id: "msg_next", stamp: "2026-10-29T01:01:10.000Z",
                                 blocks: #"{"type":"text","text":"b"}"#, speed: "fast"))
        let traits = parser.finish().traits
        XCTAssertEqual(traits.models, ["claude-opus-5-5": 1, "claude-sonnet-5-5": 1])
        XCTAssertEqual(traits.speeds, ["Standard": 1, "Fast": 1])
        XCTAssertTrue(traits.efforts.isEmpty)
    }

    // MARK: - Codex

    private func codex(_ stamp: String, _ type: String, _ payload: String) -> Data {
        line(#"{"timestamp":"\#(stamp)","ordinal":1,"type":"\#(type)","payload":\#(payload)}"#)
    }

    func testCodexReadsWhatEachTurnRanOn() {
        var parser = CodexActivityParser(days: ActivityDayResolver(calendar: utc))
        parser.consume(codex("2026-10-29T01:00:00.000Z", "session_meta", #"{"id":"abc-123","cwd":"/x"}"#))
        parser.consume(codex("2026-10-29T01:00:01.000Z", "event_msg", #"{"type":"thread_settings_applied","thread_settings":{"model":"gpt-6-sol","service_tier":"priority"}}"#))
        parser.consume(codex("2026-10-29T01:00:02.000Z", "turn_context", #"{"turn_id":"t","model":"gpt-6-sol","collaboration_mode":{"settings":{"model":"other"}},"effort":"xhigh"}"#))
        parser.consume(codex("2026-10-29T01:00:03.000Z", "event_msg", #"{"type":"thread_settings_applied","thread_settings":{"service_tier":"default"}}"#))
        parser.consume(codex("2026-10-29T01:00:04.000Z", "turn_context", #"{"turn_id":"u","model":"gpt-6-luna","effort":"low"}"#))
        XCTAssertEqual(parser.rawSessionID, "abc-123")
        let traits = parser.finish().traits
        XCTAssertEqual(traits.models, ["gpt-6-sol": 1, "gpt-6-luna": 1])
        XCTAssertEqual(traits.efforts, ["Extra High": 1, "Low": 1])
        XCTAssertEqual(traits.speeds, ["Fast": 1, "Standard": 1])
    }

    func testCodexCountsOnlyWhatThePersonTyped() {
        var parser = CodexActivityParser(days: ActivityDayResolver(calendar: utc))
        parser.consume(codex("2026-10-29T01:00:02.000Z", "turn_context", #"{"model":"gpt-6-sol","effort":"high"}"#))
        let env = #"{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>\n  <cwd>/x</cwd>"}]}"#
        let agents = ##"{"type":"message","role":"user","content":[{"type":"input_text","text":"# AGENTS.md instructions for /x"}]}"##
        let typed = #"{"type":"message","role":"user","content":[{"type":"input_text","text":"fix the build"}]}"#
        for payload in [env, agents, typed] { parser.consume(codex("2026-10-29T01:00:05.000Z", "response_item", payload)) }
        XCTAssertEqual(parser.finish().days[day("2026-10-29")]?.messagesByModel, ["gpt-6-sol": 1])
    }

    func testCodexAnAgentsOwnRolloutHoldsNoPersonMessages() {
        var parser = CodexActivityParser(days: ActivityDayResolver(calendar: utc))
        parser.consume(codex("2026-10-29T01:00:00.000Z", "session_meta", #"{"id":"child","parent_thread_id":"parent"}"#))
        parser.consume(codex("2026-10-29T01:00:05.000Z", "response_item",
                             #"{"type":"message","role":"user","content":[{"type":"input_text","text":"do the thing"}]}"#))
        XCTAssertNil(parser.finish().days[day("2026-10-29")])
    }

    func testCodexCountsToolsAndSkillReads() {
        var parser = CodexActivityParser(days: ActivityDayResolver(calendar: utc))
        parser.consume(codex("2026-10-29T01:00:05.000Z", "response_item",
                             #"{"type":"function_call","name":"exec_command","arguments":"{\"cmd\":\"sed -n 1,40p /Users/x/.codex/skills/development-harness/SKILL.md\"}","call_id":"c"}"#))
        parser.consume(codex("2026-10-29T01:00:06.000Z", "response_item",
                             #"{"type":"custom_tool_call","name":"exec","input":"ls","call_id":"d"}"#))
        parser.consume(codex("2026-10-29T01:00:07.000Z", "response_item",
                             #"{"type":"function_call_output","call_id":"c","output":"ok"}"#))
        parser.consume(codex("2026-10-29T01:00:08.000Z", "response_item",
                             #"{"type":"function_call","name":"mcp__notion__fetch","arguments":"{}","call_id":"e"}"#))
        let counts = parser.finish().days[day("2026-10-29")]
        XCTAssertEqual(counts?.tools, ["exec_command": 1, "exec": 1, "notion": 1])
        XCTAssertEqual(counts?.skills, ["development-harness": 1])
    }

    // MARK: - Charts

    func testAChartKeepsTheBusiestNamesAndFoldsTheRestIntoOther() {
        let days = (0..<3).map { day("2026-10-2\($0 + 7)") }
        let counts: [Date: [String: Int]] = [
            days[0]: ["a": 5, "b": 4, "c": 3, "d": 2, "e": 1],
            days[1]: ["a": 1, "e": 6],
            days[2]: [:],
        ]
        let chart = ActivityChart.build(days: days, counts: counts, limit: 2)
        XCTAssertEqual(chart.series.map(\.id), ["e", "a", "other"])
        XCTAssertEqual(chart.series.map(\.total), [7, 6, 9])
        XCTAssertEqual(chart.total, 22)
        XCTAssertEqual(chart.series.first { $0.id == "other" }?.counts, [9, 0, 0])
        XCTAssertEqual(chart.series.reduce(0) { $0 + $1.total }, chart.total)
    }

    func testSharesAreLargestFirstAndTieBreakByName() {
        let shares = SessionTraits.shares(["b": 1, "a": 1, "c": 2])
        XCTAssertEqual(shares.map(\.name), ["c", "a", "b"])
        XCTAssertEqual(shares.first?.share ?? 0, 0.5, accuracy: 0.0001)
        XCTAssertTrue(SessionTraits.shares([:]).isEmpty)
    }

    // MARK: - Usage by type

    func testUsageByTypeSplitsChatsFromSubagentsAndNamesWhatItCannotPlace() {
        func session(_ id: String, tokens: Int64, parent: String?) -> SessionUsageSummary {
            SessionUsageSummary(id: id, projectID: nil, projectName: nil, startedAt: nil, lastActivityAt: day("2026-10-29"),
                                usage: TokenUsage(inputTokens: tokens, cachedInputTokens: 0, outputTokens: 0),
                                models: [], directSubagentCount: 0, imageAttachmentCount: 0, parentSessionID: parent)
        }
        let range = AnalyticsRange.sevenDays
        let snapshot = AnalyticsSnapshot(
            range: range, interval: range.interval(through: day("2026-10-29"), calendar: utc), through: day("2026-10-29"),
            usage: TokenUsage(inputTokens: 1_000, cachedInputTokens: 0, outputTokens: 0), quality: .exact,
            buckets: [], models: [], projects: [],
            sessions: [session("a", tokens: 600, parent: nil), session("b", tokens: 250, parent: "a")])
        let breakdown = UsageTypeBreakdown(snapshot: snapshot)
        XCTAssertEqual(breakdown.parts.map(\.id), ["chats", "subagents", "other"])
        XCTAssertEqual(breakdown.parts.map(\.tokens), [600, 250, 150])
        XCTAssertEqual(breakdown.share(breakdown.parts[0]), 0.6, accuracy: 0.0001)

        let none = UsageTypeBreakdown(snapshot: .empty(range: range, through: day("2026-10-29"), calendar: utc))
        XCTAssertTrue(none.parts.isEmpty)
    }

    // MARK: - Scanner

    func testTheScannerReadsBothAgentsAndKeysTraitsByTheAnalyticsSessionID() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("activity-\(UUID().uuidString)")
        let claude = root.appendingPathComponent("claude/projects/-Users-x-app", isDirectory: true)
        let codexDir = root.appendingPathComponent("codex/sessions/2026/10/29", isDirectory: true)
        try FileManager.default.createDirectory(at: claude, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: codexDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        let now = ISO8601DateFormatter().date(from: "2026-10-29T12:00:00Z")!
        let claudeLog = [
            String(decoding: user(#""go""#, stamp: "2026-10-29T01:00:00.000Z"), as: UTF8.self),
            String(decoding: assistant(stamp: "2026-10-29T01:00:05.000Z",
                                       blocks: #"{"type":"tool_use","id":"t","name":"Bash","input":{}}"#), as: UTF8.self),
        ].joined(separator: "\n")
        let claudeFile = claude.appendingPathComponent("11111111-1111-1111-1111-111111111111.jsonl")
        try claudeLog.write(to: claudeFile, atomically: true, encoding: .utf8)
        let rollout = codexDir.appendingPathComponent("rollout-2026-10-29T01-00-00-22222222-2222-2222-2222-222222222222.jsonl")
        let codexLog = [
            String(decoding: codex("2026-10-29T01:00:00.000Z", "turn_context", #"{"model":"gpt-6-sol","effort":"high"}"#), as: UTF8.self),
            String(decoding: codex("2026-10-29T01:00:01.000Z", "response_item",
                                   #"{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}"#), as: UTF8.self),
        ].joined(separator: "\n")
        try codexLog.write(to: rollout, atomically: true, encoding: .utf8)
        for url in [claudeFile, rollout] {
            try FileManager.default.setAttributes([.modificationDate: now], ofItemAtPath: url.path)
        }

        let scanner = UsageActivityScanner(
            roots: .init(claudeProjects: root.appendingPathComponent("claude/projects"),
                         codexSessions: [root.appendingPathComponent("codex/sessions")]),
            calendar: utc)
        let claudeSnapshot = await scanner.snapshot(provider: .claude, range: .sevenDays, now: now)
        XCTAssertEqual(claudeSnapshot.messages.total, 1)
        XCTAssertEqual(claudeSnapshot.messages.series.first?.id, "claude-opus-5-5")
        XCTAssertEqual(claudeSnapshot.tools.series.map(\.id), ["Bash"])
        XCTAssertEqual(claudeSnapshot.messages.days.count, 7)
        let claudeID = UsageActivityScanner.identifier("claude-session|11111111-1111-1111-1111-111111111111")
        XCTAssertEqual(claudeSnapshot.traits[claudeID]?.models, ["claude-opus-5-5": 1])

        let codexSnapshot = await scanner.snapshot(provider: .codex, range: .sevenDays, now: now)
        XCTAssertEqual(codexSnapshot.messages.series.first?.id, "gpt-6-sol")
        let codexID = UsageActivityScanner.identifier("22222222-2222-2222-2222-222222222222")
        XCTAssertEqual(codexSnapshot.traits[codexID]?.efforts, ["High": 1])
    }
}
