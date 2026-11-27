import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

/// Reads the few values a log record carries by looking for them in its bytes.
///
/// These logs run to hundreds of megabytes and nearly every line is a record
/// that matters to somebody else, so most lines are not parsed as JSON: the
/// handful of fields wanted (a model, a timestamp) are written in
/// a fixed, compact form by the agents, and are found by their key. A value is
/// read up to its closing quote; none of the ones wanted contain one.
enum LogBytes {
    static func contains(_ line: Data, _ needle: Data) -> Bool {
        #if CODERIM_RUST_ACCOUNTING
        let start = line.withUnsafeBytes { bytes in
            lookup(bytes, marker: needle, from: 0, quoted: false).start
        }
        if start != -2 { return start >= 0 }
        #endif
        return line.range(of: needle) != nil
    }

    #if CODERIM_RUST_ACCOUNTING
    private static func lookup(_ bytes: UnsafeRawBufferPointer, marker: Data, from: Int, quoted: Bool) -> CRActivityField {
        marker.withUnsafeBytes { key in
            cr_activity_field(bytes.bindMemory(to: UInt8.self).baseAddress, bytes.count,
                              key.bindMemory(to: UInt8.self).baseAddress, key.count, from, quoted)
        }
    }
    #endif

    /// The text after the first `marker` (for instance `"model":"`) at or after
    /// `from`, up to the closing quote, and where that quote is.
    static func string(after marker: Data, in line: Data, from: Data.Index? = nil)
        -> (value: String, end: Data.Index)? {
        #if CODERIM_RUST_ACCOUNTING
        let offset = (from ?? line.startIndex) - line.startIndex
        if offset >= 0 && offset <= line.count {
            var delegate = false
            let result = line.withUnsafeBytes { raw -> (String, Data.Index)? in
                let bounds = lookup(raw, marker: marker, from: offset, quoted: true)
                if bounds.start == -2 { delegate = true; return nil }
                guard bounds.start >= 0 else { return nil }
                let bytes = raw.bindMemory(to: UInt8.self)
                let value = String(decoding: bytes[bounds.start..<bounds.end], as: UTF8.self)
                return (value, line.startIndex + bounds.end)
            }
            if !delegate { return result }
        }
        #endif
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
        let minute = key + ":00Z"
        #if CODERIM_RUST_ACCOUNTING
        let seconds = cr_log_reference_date(minute, minute.utf8.count, true)
        let resolved = seconds.isFinite ? Date(timeIntervalSinceReferenceDate: seconds) : formatter.date(from: minute)
        #else
        let resolved = formatter.date(from: minute)
        #endif
        guard let date = resolved else { return nil }
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

    private(set) var activity = FileActivity()
    private var waiting: [Date] = []
    private var lastModel: String?
    private var lastMessage: String?
    private var seenToolCalls: Set<String> = []
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
        guard LogBytes.contains(line, Self.toolUse),
              let record = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
              let message = record["message"] as? [String: Any],
              let blocks = message["content"] as? [[String: Any]] else { return }
        for block in blocks where block["type"] as? String == "tool_use" {
            guard let name = block["name"] as? String, !name.isEmpty else { continue }
            // Calls without an id remain countable; only an explicit id proves a duplicate.
            if let id = block["id"] as? String, !id.isEmpty, !seenToolCalls.insert(id).inserted { continue }
            let tool = ActivityNames.tool(name)
            activity.edit(day) { $0.tools[tool, default: 0] += 1 }
            if name == "Skill", let input = block["input"] as? [String: Any],
               let skill = input["skill"] as? String, !skill.isEmpty {
                activity.edit(day) { $0.skills[skill, default: 0] += 1 }
            }
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

    private enum Kind: UInt8 { case ignored, message, tool, turn, settings, metadata }

    mutating func consume(_ line: Data) {
#if CODERIM_RUST_ACCOUNTING
        let (kind, day, first, second) = line.withUnsafeBytes { raw -> (Kind, Date?, String?, String?) in
            let bytes = raw.bindMemory(to: UInt8.self)
            let record = cr_codex_activity_record(bytes.baseAddress, bytes.count)
            let kind = Kind(rawValue: record.kind) ?? .ignored
            func value(_ field: CRActivityField, _ marker: Data) -> String? {
                if field.start == -2 { return LogBytes.string(after: marker, in: line)?.value }
                guard field.start >= 0 else { return nil }
                return String(decoding: bytes[field.start..<field.end], as: UTF8.self)
            }
            let day: Date?
            switch kind {
            case .turn, .tool, .message:
                guard let stamp = value(record.stamp, Self.timestamp), let valid = days.day(of: stamp) else {
                    return (kind, nil, nil, nil)
                }
                day = valid
            default: day = nil
            }
            var first: String?
            var second: String?
            switch kind {
            case .metadata:
                first = value(record.primary, Self.sessionIDKey)
                second = value(record.secondary, Self.parentThread)
            case .settings: first = value(record.primary, Self.tier)
            case .turn:
                first = value(record.primary, Self.model)
                if first?.isEmpty == false { second = value(record.secondary, Self.effort) }
            case .tool:
                first = value(record.primary, Self.name)
                guard first?.isEmpty == false else { break }
                if record.secondary.start == -2 { second = skillRead(in: line) }
                else if record.secondary.start >= 0 {
                    second = String(decoding: bytes[record.secondary.start..<record.secondary.end], as: UTF8.self)
                }
            case .message: if !isSubagent { first = value(record.primary, Self.text) }
            case .ignored: break
            }
            return (kind, day, first, second)
        }
#else
        let kind: Kind
        if LogBytes.contains(line, Self.sessionMeta) { kind = .metadata }
        else if LogBytes.contains(line, Self.settings) { kind = .settings }
        else if LogBytes.contains(line, Self.turnContext) { kind = .turn }
        else if LogBytes.contains(line, Self.functionCall) || LogBytes.contains(line, Self.customCall) { kind = .tool }
        else if LogBytes.contains(line, Self.userRole), LogBytes.contains(line, Self.message) { kind = .message }
        else { return }
        let day: Date?
        if kind == .metadata || kind == .settings { day = nil }
        else {
            guard let stamp = LogBytes.string(after: Self.timestamp, in: line)?.value, let valid = days.day(of: stamp) else { return }
            day = valid
        }
        var first: String?
        var second: String?
        switch kind {
        case .metadata:
            first = LogBytes.string(after: Self.sessionIDKey, in: line)?.value
            second = LogBytes.string(after: Self.parentThread, in: line)?.value
        case .settings: first = LogBytes.string(after: Self.tier, in: line)?.value
        case .turn:
            first = LogBytes.string(after: Self.model, in: line)?.value
            if first?.isEmpty == false { second = LogBytes.string(after: Self.effort, in: line)?.value }
        case .tool:
            first = LogBytes.string(after: Self.name, in: line)?.value
            if first?.isEmpty == false { second = skillRead(in: line) }
        case .message: if !isSubagent { first = LogBytes.string(after: Self.text, in: line)?.value }
        case .ignored: break
        }
#endif
        if kind == .metadata {
            rawSessionID = first ?? rawSessionID
            isSubagent = second.map { !$0.isEmpty } ?? false
            return
        }
        if kind == .settings {
            if let first { tier = first }
            return
        }
        guard let day, let first else { return }
        switch kind {
        case .turn:
            guard !first.isEmpty else { return }
            model = first
            activity.traits.models[first, default: 0] += 1
            if let second, !second.isEmpty { activity.traits.efforts[ActivityNames.effort(second), default: 0] += 1 }
            activity.traits.speeds[ActivityNames.speed(tier), default: 0] += 1
        case .tool:
            guard !first.isEmpty else { return }
            let tool = ActivityNames.tool(first)
            activity.edit(day) { $0.tools[tool, default: 0] += 1 }
            if let second { activity.edit(day) { $0.skills[second, default: 0] += 1 } }
        case .message:
            guard !isSubagent, !first.hasPrefix("<"), !first.hasPrefix("# AGENTS") else { return }
            let answeredBy = model ?? "Unknown"
            activity.edit(day) { $0.messagesByModel[answeredBy, default: 0] += 1 }
        default: return
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
