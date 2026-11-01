import Foundation

/// Reads the few values a log record carries by looking for them in its bytes.
///
/// These logs run to hundreds of megabytes and nearly every line is a record
/// that matters to somebody else, so each line is not parsed as JSON: the
/// handful of fields wanted (a model, a tool's name, a timestamp) are written in
/// a fixed, compact form by the agents, and are found by their key. A value is
/// read up to its closing quote; none of the ones wanted contain one.
enum LogBytes {
    static func contains(_ line: Data, _ needle: Data) -> Bool {
        line.range(of: needle) != nil
    }

    /// The text after the first `marker` (for instance `"model":"`) at or after
    /// `from`, up to the closing quote, and where that quote is.
    static func string(after marker: Data, in line: Data, from: Data.Index? = nil)
        -> (value: String, end: Data.Index)? {
        guard let found = line.range(of: marker, in: (from ?? line.startIndex)..<line.endIndex) else { return nil }
        var bytes: [UInt8] = []
        var index = found.upperBound
        while index < line.endIndex {
            let byte = line[index]
            if byte == UInt8(ascii: "\\") {
                let next = line.index(after: index)
                guard next < line.endIndex else { return nil }
                bytes.append(line[next])
                index = line.index(after: next)
                continue
            }
            if byte == UInt8(ascii: "\"") { return (String(decoding: bytes, as: UTF8.self), index) }
            bytes.append(byte)
            guard bytes.count <= 2_048 else { return nil }
            index = line.index(after: index)
        }
        return nil
    }

    /// Every value after `marker`, in order.
    static func strings(after marker: Data, in line: Data) -> [String] {
        var values: [String] = []
        var cursor = line.startIndex
        while let found = string(after: marker, in: line, from: cursor) {
            values.append(found.value)
            cursor = found.end
        }
        return values
    }
}

/// Turns a log timestamp into the day it fell on, here.
final class ActivityDayResolver {
    private let calendar: Calendar
    private var cache: [String: Date] = [:]
    private let formatter = ISO8601DateFormatter()

    init(calendar: Calendar = .current) { self.calendar = calendar }

    /// "2026-10-29T10:12:03.123Z" is read to the minute, which is as fine as a
    /// day in any time zone needs, and is looked up rather than parsed again.
    func day(of stamp: String) -> Date? {
        guard stamp.count >= 16 else { return nil }
        let key = String(stamp.prefix(16))
        if let cached = cache[key] { return cached }
        guard let date = formatter.date(from: key + ":00Z") else { return nil }
        let day = calendar.startOfDay(for: date)
        cache[key] = day
        return day
    }
}

/// How the agents' own names for things are shown.
enum ActivityNames {
    /// An MCP server's tools are counted as the server: `mcp__claude_ai_Notion__fetch`
    /// is "Notion". Built-in tools keep their names.
    static func tool(_ raw: String) -> String {
        guard raw.hasPrefix("mcp__") else { return raw }
        let parts = raw.dropFirst(5).components(separatedBy: "__")
        guard let server = parts.first, !server.isEmpty else { return raw }
        return server.hasPrefix("claude_ai_") ? String(server.dropFirst(10)) : server
    }

    static func effort(_ raw: String) -> String {
        switch raw.lowercased() {
        case "xhigh": return "Extra High"
        case "": return raw
        default: return raw.prefix(1).uppercased() + raw.dropFirst()
        }
    }

    /// Codex calls its fast tier "priority".
    static func speed(_ raw: String?) -> String {
        switch raw?.lowercased() {
        case nil, "", "default", "standard": return "Standard"
        case "priority", "fast": return "Fast"
        case let other?: return other.prefix(1).uppercased() + other.dropFirst()
        }
    }
}

// MARK: - Claude

/// One Claude Code transcript, a line at a time.
///
/// A message the person sent is credited to the model that answered it, so it
/// waits for the next assistant record. Each content block of an answer is its
/// own line, with the same message id, so a message is counted once.
struct ClaudeActivityParser {
    static let needles = ["\"type\":\"assistant\"", "\"type\":\"user\""]

    private static let assistantType = Data("\"type\":\"assistant\"".utf8)
    private static let userType = Data("\"type\":\"user\"".utf8)
    private static let sidechain = Data("\"isSidechain\":true".utf8)
    private static let toolResult = Data("\"tool_result\"".utf8)
    private static let meta = Data("\"isMeta\":true".utf8)
    private static let timestamp = Data("\"timestamp\":\"".utf8)
    private static let model = Data("\"model\":\"".utf8)
    private static let speed = Data("\"speed\":\"".utf8)
    private static let messageID = Data("\"id\":\"msg_".utf8)
    private static let toolUse = Data("\"type\":\"tool_use\"".utf8)
    private static let name = Data("\"name\":\"".utf8)
    private static let skill = Data("\"skill\":\"".utf8)

    private(set) var activity = FileActivity()
    private var waiting: [Date] = []
    private var lastModel: String?
    private var lastMessage: String?
    private let days: ActivityDayResolver

    init(days: ActivityDayResolver) {
        self.days = days
    }

    mutating func consume(_ line: Data) {
        // Where `type` sits among a record's keys varies, and a message carries
        // a `type` of its own, so the kind is looked for rather than read first.
        let kind: String
        if LogBytes.contains(line, Self.assistantType) { kind = "assistant" }
        else if LogBytes.contains(line, Self.userType) { kind = "user" }
        else { return }
        guard let stamp = LogBytes.string(after: Self.timestamp, in: line)?.value,
              let day = days.day(of: stamp) else { return }
        let isSubagent = LogBytes.contains(line, Self.sidechain)

        if kind == "user" {
            guard !isSubagent, !LogBytes.contains(line, Self.toolResult),
                  !LogBytes.contains(line, Self.meta) else { return }
            waiting.append(day)
            return
        }

        // A subagent's tool calls are real calls; its messages are not the
        // person's, and its model is not what the chat ran on.
        countTools(in: line, on: day)
        guard !isSubagent else { return }
        guard let model = LogBytes.string(after: Self.model, in: line)?.value,
              !model.isEmpty, !model.hasPrefix("<") else { return }
        lastModel = model
        for sent in waiting { activity.edit(sent) { $0.messagesByModel[model, default: 0] += 1 } }
        waiting.removeAll()

        let message = LogBytes.string(after: Self.messageID, in: line)?.value
        guard message == nil || message != lastMessage else { return }
        lastMessage = message
        activity.traits.models[model, default: 0] += 1
        if let speed = LogBytes.string(after: Self.speed, in: line)?.value {
            activity.traits.speeds[ActivityNames.speed(speed), default: 0] += 1
        }
    }

    private mutating func countTools(in line: Data, on day: Date) {
        guard LogBytes.contains(line, Self.toolUse) else { return }
        var cursor = line.startIndex
        while let call = line.range(of: Self.toolUse, in: cursor..<line.endIndex) {
            cursor = call.upperBound
            guard let named = LogBytes.string(after: Self.name, in: line, from: cursor) else { continue }
            let tool = ActivityNames.tool(named.value)
            activity.edit(day) { $0.tools[tool, default: 0] += 1 }
            if named.value == "Skill",
               let used = LogBytes.string(after: Self.skill, in: line, from: named.end), !used.value.isEmpty {
                activity.edit(day) { $0.skills[used.value, default: 0] += 1 }
            }
            cursor = named.end
        }
    }

    /// Messages still waiting for an answer are credited to the last model seen.
    mutating func finish() -> FileActivity {
        if !waiting.isEmpty {
            let model = lastModel ?? "Unknown"
            for sent in waiting { activity.edit(sent) { $0.messagesByModel[model, default: 0] += 1 } }
            waiting.removeAll()
        }
        return activity
    }
}

// MARK: - Codex

/// One Codex rollout, a line at a time.
struct CodexActivityParser {
    static let needles = ["\"type\":\"session_meta\"", "\"type\":\"turn_context\"", "\"type\":\"thread_settings_applied\"",
                          "\"type\":\"function_call\"", "\"type\":\"custom_tool_call\"",
                          "\"role\":\"user\""]

    private static let sessionMeta = Data("\"type\":\"session_meta\"".utf8)
    private static let sessionIDKey = Data("\"id\":\"".utf8)
    private static let parentThread = Data("\"parent_thread_id\":\"".utf8)
    private static let turnContext = Data("\"type\":\"turn_context\"".utf8)
    private static let settings = Data("\"type\":\"thread_settings_applied\"".utf8)
    private static let functionCall = Data("\"type\":\"function_call\"".utf8)
    private static let customCall = Data("\"type\":\"custom_tool_call\"".utf8)
    private static let userRole = Data("\"role\":\"user\"".utf8)
    private static let message = Data("\"type\":\"message\"".utf8)
    private static let timestamp = Data("\"timestamp\":\"".utf8)
    private static let model = Data("\"model\":\"".utf8)
    private static let effort = Data("\"effort\":\"".utf8)
    private static let tier = Data("\"service_tier\":\"".utf8)
    private static let name = Data("\"name\":\"".utf8)
    private static let text = Data("\"text\":\"".utf8)
    private static let skillsDirectory = Data("/skills/".utf8)
    private static let skillFile = Data("SKILL.md".utf8)

    private(set) var activity = FileActivity()
    /// The session's own id, as the rollout states it; the caller makes it the
    /// analytics' identifier.
    private(set) var rawSessionID: String?
    /// A subagent's rollout holds what its parent told it, which is not the
    /// person's messages.
    private var isSubagent = false
    private var model: String?
    private var tier: String?
    private let days: ActivityDayResolver

    init(days: ActivityDayResolver) {
        self.days = days
    }

    mutating func consume(_ line: Data) {
        if LogBytes.contains(line, Self.sessionMeta) {
            rawSessionID = LogBytes.string(after: Self.sessionIDKey, in: line)?.value ?? rawSessionID
            isSubagent = LogBytes.string(after: Self.parentThread, in: line).map { !$0.value.isEmpty } ?? false
            return
        }
        if LogBytes.contains(line, Self.settings) {
            if let value = LogBytes.string(after: Self.tier, in: line)?.value { tier = value }
            return
        }
        guard let stamp = LogBytes.string(after: Self.timestamp, in: line)?.value,
              let day = days.day(of: stamp) else { return }

        if LogBytes.contains(line, Self.turnContext) {
            guard let model = LogBytes.string(after: Self.model, in: line)?.value, !model.isEmpty else { return }
            self.model = model
            activity.traits.models[model, default: 0] += 1
            if let effort = LogBytes.string(after: Self.effort, in: line)?.value, !effort.isEmpty {
                activity.traits.efforts[ActivityNames.effort(effort), default: 0] += 1
            }
            activity.traits.speeds[ActivityNames.speed(tier), default: 0] += 1
        } else if LogBytes.contains(line, Self.functionCall) || LogBytes.contains(line, Self.customCall) {
            guard let raw = LogBytes.string(after: Self.name, in: line)?.value, !raw.isEmpty else { return }
            let tool = ActivityNames.tool(raw)
            activity.edit(day) { $0.tools[tool, default: 0] += 1 }
            if let skill = skillRead(in: line) { activity.edit(day) { $0.skills[skill, default: 0] += 1 } }
        } else if LogBytes.contains(line, Self.userRole), LogBytes.contains(line, Self.message) {
            // What the person typed, not the context Codex adds around it.
            guard !isSubagent, let typed = LogBytes.string(after: Self.text, in: line)?.value,
                  !typed.hasPrefix("<"), !typed.hasPrefix("# AGENTS") else { return }
            let answeredBy = model ?? "Unknown"
            activity.edit(day) { $0.messagesByModel[answeredBy, default: 0] += 1 }
        }
    }

    /// A skill is used by reading its `SKILL.md`: `…/skills/<name>/SKILL.md`.
    private func skillRead(in line: Data) -> String? {
        var cursor = line.startIndex
        while let directory = line.range(of: Self.skillsDirectory, in: cursor..<line.endIndex) {
            cursor = directory.upperBound
            guard let slash = line[cursor...].firstIndex(of: UInt8(ascii: "/")),
                  line.distance(from: cursor, to: slash) < 128 else { continue }
            let after = line.index(after: slash)
            guard line[after...].starts(with: Self.skillFile) else { continue }
            let skill = String(decoding: line[cursor..<slash], as: UTF8.self)
            if !skill.isEmpty, !skill.contains("\\") { return skill }
        }
        return nil
    }

    func finish() -> FileActivity { activity }
}
