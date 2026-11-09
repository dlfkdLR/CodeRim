import Foundation

/// Read conversation activity from ~/.claude/projects/<slug>/<sessionId>.jsonl. Unlike the terminal
/// registry’s status, engine-written transcripts also cover desktop-hosted sessions.
enum ClaudeTranscript {
    /// Transcripts cannot distinguish permission waits from slow tools; only the terminal registry may
    /// claim waiting.
    enum Turn: Equatable { case inFlight, finished }

    struct Event {
        let turn: Turn
        let timestamp: Date?
    }

    /// How much of the end of the file to look at. The last few records settle
    /// it, and these transcripts run to megabytes.
    static let tailBytes = 64 * 1024

    // MARK: - Where the file is

    /// The directory Claude Code files a working directory's transcripts under:
    /// the path with every `/` and `.` turned into `-`, so
    /// `/Users/x/app/.claude/worktrees/y` becomes
    /// `-Users-x-app--claude-worktrees-y`.
    static func projectSlug(forCWD cwd: String) -> String {
        String(cwd.map { $0 == "/" || $0 == "." ? "-" : $0 })
    }

    /// Try the cwd-derived slug first; cache the directory-scan fallback for resumed/moved sessions.
    static func transcript(
        forSessionID sessionID: String,
        cwd: String,
        in projects: URL,
        scanning: Bool,
        fileManager: FileManager = .default
    ) -> URL? {
        let file = sessionID + ".jsonl"
        let direct = projects
            .appendingPathComponent(projectSlug(forCWD: cwd))
            .appendingPathComponent(file)
        if fileManager.fileExists(atPath: direct.path) { return direct }
        guard scanning else { return nil }

        let folders = (try? fileManager.contentsOfDirectory(atPath: projects.path)) ?? []
        for folder in folders {
            let candidate = projects
                .appendingPathComponent(folder)
                .appendingPathComponent(file)
            if fileManager.fileExists(atPath: candidate.path) { return candidate }
        }
        return nil
    }

    // MARK: - What it says

    static func turn(at url: URL, bytes: Int = tailBytes) -> Turn? {
        tail(of: url, bytes: bytes).flatMap(turn(inTail:))
    }

    /// The last records of the file, without reading the rest of it.
    static func tail(of url: URL, bytes: Int = tailBytes) -> Data? {
        guard bytes > 0, let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let end = try? handle.seekToEnd() else { return nil }
        let start = end > UInt64(bytes) ? end - UInt64(bytes) : 0
        guard (try? handle.seek(toOffset: start)) != nil else { return nil }
        return try? handle.read(upToCount: bytes)
    }

    /// Only conversation records decide activity; bookkeeping continues while idle. Return nil for an
    /// unused transcript rather than guessing.
    static func turn(inTail data: Data) -> Turn? {
        event(inTail: data)?.turn
    }

    /// The model and tier of the latest assistant message in the tail.
    static func details(inTail data: Data) -> ModelDetails? {
        for line in data.split(separator: UInt8(ascii: "\n"), omittingEmptySubsequences: true).reversed() {
            guard let json = (try? JSONSerialization.jsonObject(with: Data(line))) as? [String: Any],
                  json["type"] as? String == "assistant", json["isSidechain"] as? Bool != true,
                  let message = json["message"] as? [String: Any],
                  let model = message["model"] as? String, !model.hasPrefix("<") else { continue }
            let usage = message["usage"] as? [String: Any]
            return ModelDetails(model: model, effort: nil, fast: usage?["speed"] as? String == "fast")
        }
        return nil
    }

    static func event(inTail data: Data) -> Event? {
        let lines = data.split(separator: UInt8(ascii: "\n"), omittingEmptySubsequences: true)
        for line in lines.reversed() {
            // The first line of the tail is usually cut in half; it fails to
            // parse and is skipped, which is the whole handling it needs.
            guard let json = (try? JSONSerialization.jsonObject(with: Data(line)))
                    as? [String: Any] else { continue }
            // A subagent's records are interleaved with its parent's, and the
            // parent's turn is the one being reported on.
            if json["isSidechain"] as? Bool == true { continue }

            let timestamp = (json["timestamp"] as? String).flatMap(TranscriptLines.parse)
            switch json["type"] as? String {
            case "continued-in":
                // The old process may remain open after a conversation moves to
                // another session. Its last registry status is no longer live.
                return Event(turn: .finished, timestamp: timestamp)
            case "system" where json["subtype"] as? String == "turn_duration":
                return Event(turn: .finished, timestamp: timestamp)
            case "assistant":
                let message = json["message"] as? [String: Any]
                return Event(turn: assistantEndsTurn(message) ? .finished : .inFlight,
                             timestamp: timestamp)
            case "user":
                return Event(turn: isInterruption(json) ? .finished : .inFlight, timestamp: timestamp)
            default:
                continue
            }
        }
        return nil
    }

    /// Streamed thinking blocks may carry end_turn and incomplete blocks lack stop_reason. Finish only
    /// on a terminal reason plus a non-thinking/non-tool final block; explicit interruption closes
    /// interrupted turns.
    static func assistantEndsTurn(_ message: [String: Any]?) -> Bool {
        guard let reason = message?["stop_reason"] as? String,
              ["end_turn", "stop_sequence", "max_tokens"].contains(reason) else { return false }
        if let blocks = message?["content"] as? [[String: Any]],
           let last = blocks.last?["type"] as? String,
           last == "thinking" || last == "redacted_thinking" || last == "tool_use" {
            return false
        }
        return true
    }

    /// Count tool blocks, including parallel calls, but exclude sidechains. Screen bytes before parsing
    /// JSON.
    static func toolUses(inTail data: Data) -> [Date] {
        let needle = Data("\"tool_use\"".utf8)
        var stamps: [Date] = []
        for line in data.split(separator: UInt8(ascii: "\n"), omittingEmptySubsequences: true)
        where line.range(of: needle) != nil {
            guard let json = (try? JSONSerialization.jsonObject(with: Data(line))) as? [String: Any],
                  json["type"] as? String == "assistant", json["isSidechain"] as? Bool != true,
                  let message = json["message"] as? [String: Any],
                  let content = message["content"] as? [[String: Any]],
                  let stamp = (json["timestamp"] as? String).flatMap(TranscriptLines.parse) else { continue }
            let calls = content.filter { $0["type"] as? String == "tool_use" }.count
            stamps.append(contentsOf: Array(repeating: stamp, count: calls))
        }
        return stamps
    }

    /// Queued instructions do not start a new turn; retain their timestamps separately from activity
    /// transitions.
    static func queuedInstructions(inTail data: Data) -> [Date] {
        data.split(separator: UInt8(ascii: "\n"), omittingEmptySubsequences: true)
            .compactMap { line -> Date? in
                // Cheap rejection before parsing: the tail is mostly other records.
                guard line.range(of: Data("queue-operation".utf8)) != nil,
                      let json = (try? JSONSerialization.jsonObject(with: Data(line)))
                        as? [String: Any],
                      json["type"] as? String == "queue-operation",
                      json["operation"] as? String == "enqueue",
                      let stamp = json["timestamp"] as? String else { return nil }
                let formatter = ISO8601DateFormatter()
                formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                return formatter.date(from: stamp)
            }
    }

    /// Pressing Esc writes a user turn saying so, which is what makes "stopped"
    /// something the notch can know rather than wait out with a timeout. Two
    /// wordings exist, and both start the same way.
    private static let interruption = "[Request interrupted by user"

    static func isInterruption(_ json: [String: Any]) -> Bool {
        guard let message = json["message"] as? [String: Any] else { return false }
        switch message["content"] {
        case let text as String:
            return text.hasPrefix(interruption)
        case let blocks as [Any]:
            return blocks.contains { block in
                ((block as? [String: Any])?["text"] as? String)?.hasPrefix(interruption) == true
            }
        default:
            return false
        }
    }
}

extension ClaudeTranscript {
    private static let titleMarker = Data("-title\"".utf8)

    /// Folds every title entry in `data` into `state`, later entries winning.
    static func titles(in data: Data, into state: inout ClaudeTranscriptReader.TitleScan) {
        for line in data.split(separator: 0x0A) where line.range(of: titleMarker) != nil {
            guard let json = try? JSONSerialization.jsonObject(with: Data(line)) as? [String: Any] else { continue }
            func text(_ key: String) -> String? {
                (json[key] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines).nonEmpty
            }
            switch json["type"] as? String {
            case "custom-title": if let value = text("customTitle") { state.custom = value }
            case "ai-title": if let value = text("aiTitle") { state.generated = value }
            default: break
            }
        }
    }
}

private extension String {
    var nonEmpty: String? { isEmpty ? nil : self }
}

/// Cache transcript metadata per monitor; unchanged files need only stat, and growing files trigger
/// tail reads.
final class ClaudeTranscriptReader {
    private struct Cached {
        let modified: Date
        let size: UInt64
        let event: ClaudeTranscript.Event?
        let queued: [Date]
        let toolUses: [Date]
        var details: ModelDetails?
    }

    private let projects: URL
    /// Sub-agent ids known to be over.
    private var settledSubagents: Set<String> = []
    private let fileManager: FileManager
    /// Where each session's transcript turned out to be. Sessions come and go;
    /// this is bounded by how many have run since the app launched.
    private var paths: [String: URL] = [:]
    private var cache: [String: Cached] = [:]
    /// Keep state-entry time separately from append timestamps so continued output does not reset
    /// elapsed time.
    private var entered: [String: (turn: ClaudeTranscript.Turn, at: Date)] = [:]
    /// Sessions already looked for the hard way and not found. The directory
    /// scan is worth doing once for a session that has moved, and never worth
    /// repeating every two seconds for one that simply has no transcript.
    private var scanned: Set<String> = []
    private var titles: [String: TitleScan] = [:]
    private var titleAccess: UInt64 = 0
    private static let titleCacheLimit = 64
    /// Where each session's tool calls have been read up to, and when they were
    /// made. The end of the file shows only the last few calls of a long turn;
    /// reading on from where it stopped last time counts them all.
    private var toolScans: [String: (offset: UInt64, stamps: [Date])] = [:]
    /// The most read in one look, and how far back a first look goes.
    private static let toolScanChunk = 8 * 1024 * 1024
    private static let toolScanFirstLook: UInt64 = 4 * 1024 * 1024

    struct TitleScan {
        var access: UInt64 = 0
        var cursor = TranscriptLineCursor()
        var custom: String?
        var generated: String?
        /// A name the user gave the session outranks the generated one.
        var title: String? { custom ?? generated }
    }

    init(projects: URL, fileManager: FileManager = .default) {
        self.projects = projects
        self.fileManager = fileManager
    }

    /// What the session is doing, and when it last moved. Nil when there is no
    /// transcript to read, or nothing said in it yet.
    func activity(sessionID: String, cwd: String)
        -> (turn: ClaudeTranscript.Turn, since: Date, eventAt: Date?, followUps: Int, toolUses: [Date],
        details: ModelDetails?)? {
        guard let url = path(sessionID: sessionID, cwd: cwd),
              let attributes = try? fileManager.attributesOfItem(atPath: url.path),
              let modified = attributes[.modificationDate] as? Date
        else { return nil }
        let size = (attributes[.size] as? NSNumber)?.uint64Value ?? 0

        let event: ClaudeTranscript.Event?
        let queued: [Date]
        let toolUses: [Date]
        let details: ModelDetails?
        if let cached = cache[sessionID], cached.modified == modified, cached.size == size {
            event = cached.event
            queued = cached.queued
            toolUses = cached.toolUses
            details = cached.details
        } else {
            let tail = ClaudeTranscript.tail(of: url)
            event = tail.flatMap(ClaudeTranscript.event(inTail:))
            queued = tail.map(ClaudeTranscript.queuedInstructions(inTail:)) ?? []
            toolUses = scanToolUses(sessionID: sessionID, url: url, size: size)
            details = tail.flatMap(ClaudeTranscript.details(inTail:))
            cache[sessionID] = Cached(modified: modified, size: size, event: event, queued: queued,
                                      toolUses: toolUses, details: details)
        }

        guard let event else { return nil }
        let turn = event.turn
        if let previous = entered[sessionID], previous.turn == turn {
            return (turn, previous.at, event.timestamp, Self.followUps(queued, since: previous.at),
                    toolUses, details)
        }
        // First sight of a change, so the file's own timestamp is the closest
        // thing there is to when it happened.
        let since = event.timestamp ?? modified
        entered[sessionID] = (turn, since)
        return (turn, since, event.timestamp, Self.followUps(queued, since: since),
                toolUses, details)
    }

    private func scanToolUses(sessionID: String, url: URL, size: UInt64) -> [Date] {
        var state = toolScans[sessionID] ?? (size > Self.toolScanFirstLook ? size - Self.toolScanFirstLook : 0, [])
        if size < state.offset { state = (0, []) }
        if size > state.offset, let handle = try? FileHandle(forReadingFrom: url) {
            defer { try? handle.close() }
            if (try? handle.seek(toOffset: state.offset)) != nil,
               let data = try? handle.read(upToCount: Self.toolScanChunk), !data.isEmpty,
               let lastNewline = data.lastIndex(of: UInt8(ascii: "\n")) {
                let whole = data[data.startIndex...lastNewline]
                state.stamps += ClaudeTranscript.toolUses(inTail: Data(whole))
                state.offset += UInt64(whole.count)
                // A call older than any plausible turn belongs to none that is running.
                let cutoff = Date().addingTimeInterval(-6 * 3600)
                state.stamps.removeAll { $0 < cutoff }
            }
        }
        toolScans[sessionID] = state
        return state.stamps
    }

    /// Calls made since a turn began, from the stamps `activity` returns.
    static func toolCalls(_ stamps: [Date], since: Date) -> Int {
        stamps.filter { $0 >= since }.count
    }

    /// Only instructions queued after the turn began extend it. The first
    /// second is skipped because the prompt that starts a turn is itself
    /// briefly queued.
    private static func followUps(_ queued: [Date], since: Date) -> Int {
        queued.filter { $0 > since.addingTimeInterval(1) }.count
    }

    /// Custom and generated titles may appear anywhere; keep a bounded incremental cursor for each
    /// session.
    func title(sessionID: String, cwd: String) -> String? {
        guard let url = path(sessionID: sessionID, cwd: cwd),
              let size = (try? fileManager.attributesOfItem(atPath: url.path))?[.size] as? NSNumber
        else { return titles[sessionID]?.title }
        if titles[sessionID] == nil, titles.count >= Self.titleCacheLimit,
           let oldest = titles.min(by: { $0.value.access < $1.value.access }) {
            titles.removeValue(forKey: oldest.key)
        }
        var state = titles[sessionID] ?? TitleScan()
        let end = size.uint64Value
        if end < state.cursor.readOffset { state = TitleScan() }   // rewritten from scratch
        titleAccess &+= 1
        state.access = titleAccess
        if end > state.cursor.readOffset, let handle = try? FileHandle(forReadingFrom: url) {
            defer { try? handle.close() }
            var cursor = state.cursor
            try? cursor.read(handle, through: end) { line in
                ClaudeTranscript.titles(in: line, into: &state)
            }
            state.cursor = cursor
        }
        titles[sessionID] = state
        return state.title
    }

    /// A sub-agent one of this session's turns has started and not finished.
    struct Subagent: Equatable {
        let id: String
        let title: String
        let startedAt: Date
    }

    /// Sub-agents live in `<session>/subagents/agent-<id>.jsonl`, each with a
    /// `.meta.json` that carries what it was asked to do and, once it has
    /// stopped, `finished`. A transcript written to in the last two minutes and
    /// not marked finished is working; anything else is history.
    func subagents(sessionID: String, cwd: String, now: Date = Date()) -> [Subagent] {
        guard let transcript = path(sessionID: sessionID, cwd: cwd) else { return [] }
        let directory = transcript.deletingPathExtension().appendingPathComponent("subagents")
        guard let names = try? fileManager.contentsOfDirectory(atPath: directory.path) else { return [] }
        return names.filter { $0.hasSuffix(".meta.json") }.compactMap { name -> Subagent? in
            let id = String(name.dropLast(".meta.json".count))
            // A finished or long-quiet sub-agent never starts again, so it is
            // looked at once and then skipped without touching the disk.
            if settledSubagents.contains(id) { return nil }
            let log = directory.appendingPathComponent(id + ".jsonl")
            guard let logAttributes = try? fileManager.attributesOfItem(atPath: log.path),
                  let modified = logAttributes[.modificationDate] as? Date else { return nil }
            guard now.timeIntervalSince(modified) < 120 else {
                if now.timeIntervalSince(modified) > 3600 { settledSubagents.insert(id) }
                return nil
            }
            let meta = directory.appendingPathComponent(name)
            guard let data = try? Data(contentsOf: meta),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            else { return nil }
            if json["finished"] as? Bool == true { settledSubagents.insert(id); return nil }
            let started = ((try? fileManager.attributesOfItem(atPath: meta.path))?[.creationDate] as? Date) ?? modified
            let title = (json["description"] as? String).flatMap { $0.isEmpty ? nil : $0 }
                ?? (json["agentType"] as? String) ?? "Sub-agent"
            return Subagent(id: id, title: title, startedAt: started)
        }.sorted { $0.startedAt == $1.startedAt ? $0.id < $1.id : $0.startedAt < $1.startedAt }
    }

    private func path(sessionID: String, cwd: String) -> URL? {
        if let known = paths[sessionID] { return known }
        let scanning = !scanned.contains(sessionID)
        guard let found = ClaudeTranscript.transcript(
            forSessionID: sessionID, cwd: cwd, in: projects,
            scanning: scanning, fileManager: fileManager
        ) else {
            if scanning { scanned.insert(sessionID) }
            return nil
        }
        paths[sessionID] = found
        return found
    }
}
