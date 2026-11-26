import Darwin
import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

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
        guard bytes > 0, let handle = openTranscript(at: url) else { return nil }
        defer { try? handle.close() }
        guard let end = try? handle.seekToEnd() else { return nil }
        let count = min(UInt64(bytes), end)
        guard count > 0 else { return Data() }
        guard (try? handle.seek(toOffset: end - count)) != nil else { return nil }
        return try? handle.read(upToCount: Int(count))
    }

    /// Validate the descriptor before any registry-driven transcript read can block or follow a link.
    fileprivate static func openTranscript(at url: URL) -> FileHandle? {
        let descriptor = Darwin.open(url.path, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC)
        guard descriptor >= 0 else { return nil }
        var info = stat()
        guard fstat(descriptor, &info) == 0, info.st_mode & S_IFMT == S_IFREG,
              info.st_uid == getuid(), info.st_size >= 0 else {
            close(descriptor)
            return nil
        }
        return FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
    }

    /// Only conversation records decide activity; bookkeeping continues while idle. Return nil for an
    /// unused transcript rather than guessing.
    static func turn(inTail data: Data) -> Turn? {
        event(inTail: data)?.turn
    }

    /// An activity tail can end with a complete JSON object that has no LF yet.
    /// The incremental tool reader uses the same iterator on newline-terminated input.
    private struct Lines: Sequence, IteratorProtocol {
        let data: Data
        var start: Data.Index
        var breaks = SourceLineBreaks()

        init(_ data: Data) {
            self.data = data
            start = data.startIndex
            breaks.start(at: start)
        }

        mutating func next() -> Data? {
            while start < data.endIndex {
                let lower = start
                let upper = breaks.next(in: data) ?? data.endIndex
                start = upper == data.endIndex ? upper : data.index(after: upper)
                if lower < upper { return data[lower..<upper] }
            }
            return nil
        }
    }

    fileprivate static func activity(inTail data: Data) -> (event: Event?, queued: [Date], details: ModelDetails?) {
        let lines = Array(Lines(data))
        return (event(in: lines), queuedInstructions(in: lines), details(in: lines))
    }

    /// The model and tier of the latest assistant message in the tail.
    static func details(inTail data: Data) -> ModelDetails? {
        details(in: Array(Lines(data)))
    }

    private static func details(in lines: [Data]) -> ModelDetails? {
        for line in lines.reversed() {
            guard let json = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
                  json["type"] as? String == "assistant", json["isSidechain"] as? Bool != true,
                  let message = json["message"] as? [String: Any],
                  let model = message["model"] as? String, !model.hasPrefix("<") else { continue }
            let usage = message["usage"] as? [String: Any]
            return ModelDetails(model: model, effort: nil, fast: usage?["speed"] as? String == "fast")
        }
        return nil
    }

    static func event(inTail data: Data) -> Event? {
        event(in: Array(Lines(data)))
    }

    private static func event(in lines: [Data]) -> Event? {
        for line in lines.reversed() {
            // The first line of the tail is usually cut in half; it fails to
            // parse and is skipped, which is the whole handling it needs.
            guard let json = (try? JSONSerialization.jsonObject(with: line))
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
        guard data.range(of: needle) != nil else { return [] }
        var stamps: [Date] = []
        for line in Lines(data) where line.range(of: needle) != nil {
            guard let json = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
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
        queuedInstructions(in: Lines(data))
    }

    private static func queuedInstructions(in lines: some Sequence<Data>) -> [Date] {
        let needle = Data("queue-operation".utf8)
        return lines.compactMap { line -> Date? in
            // Cheap rejection before parsing: the tail is mostly other records.
            guard line.range(of: needle) != nil,
                  let json = (try? JSONSerialization.jsonObject(with: line))
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
#if DEBUG
    @TaskLocal static var beforeSubagentMetadataOpenForTesting: (@Sendable (URL) -> Void)?
    @TaskLocal static var afterSubagentMetadataOpenForTesting: (@Sendable (URL) -> Void)?
#endif
    typealias Activity = (turn: ClaudeTranscript.Turn, since: Date, eventAt: Date?, followUps: Int,
                          toolUses: [Date], details: ModelDetails?)

    private struct Cached {
        let modified: Date
        let size: UInt64
        let activity: Activity?
    }

    private let projects: URL
    private struct SettledSubagent: Equatable {
        let finished: Bool
        let modifiedSeconds: Int64
        let modifiedNanoseconds: Int64
        let size: Int64
        let device: Int32
        let inode: UInt64
        let type: mode_t
        var modified: Date {
            Date(timeIntervalSince1970: Double(modifiedSeconds) + Double(modifiedNanoseconds) / 1_000_000_000)
        }

        init?(path: String, finished: Bool) {
            var info = stat()
            guard lstat(path, &info) == 0 else { return nil }
            self.init(info: info, finished: finished)
        }

        init(info: stat, finished: Bool) {
            self.finished = finished
            modifiedSeconds = Int64(info.st_mtimespec.tv_sec)
            modifiedNanoseconds = Int64(info.st_mtimespec.tv_nsec)
            size = info.st_size
            device = info.st_dev
            inode = info.st_ino
            type = info.st_mode & S_IFMT
        }
    }
    /// A settled result is reusable only while its observed file stays unchanged.
    private var settledSubagents: [String: [String: SettledSubagent]] = [:]
    private let fileManager: FileManager
    /// Where each live session's transcript turned out to be. The registry
    /// retires all associated caches when the session leaves.
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

    func retainSessions(_ sessionIDs: Set<String>) {
        paths = paths.filter { sessionIDs.contains($0.key) }
        cache = cache.filter { sessionIDs.contains($0.key) }
        entered = entered.filter { sessionIDs.contains($0.key) }
        scanned.formIntersection(sessionIDs)
        titles = titles.filter { sessionIDs.contains($0.key) }
        toolScans = toolScans.filter { sessionIDs.contains($0.key) }
        settledSubagents = settledSubagents.filter { sessionIDs.contains($0.key) }
    }

    /// What the session is doing, and when it last moved. Nil when there is no
    /// transcript to read, or nothing said in it yet.
    func activity(sessionID: String, cwd: String) -> Activity? {
        let previous = cache[sessionID]
        guard let url = path(sessionID: sessionID, cwd: cwd),
              let stamp = fileStamp(at: url, previous: previous),
              let modified = stamp.modified else { return nil }
        let size = stamp.size ?? 0
        if stamp.unchanged, let previous { return previous.activity }

        let tail = ClaudeTranscript.tail(of: url).map(ClaudeTranscript.activity(inTail:))
        let toolUses = scanToolUses(sessionID: sessionID, url: url, size: size)
        var activity: Activity?
        if let tail, let event = tail.event {
            let since: Date
            if let previous = entered[sessionID], previous.turn == event.turn {
                since = previous.at
            } else {
                since = event.timestamp ?? modified
                entered[sessionID] = (event.turn, since)
            }
            activity = (event.turn, since, event.timestamp, Self.followUps(tail.queued, since: since),
                        toolUses, tail.details)
        }
        cache[sessionID] = Cached(modified: modified, size: size, activity: activity)
        return activity
    }

    private func scanToolUses(sessionID: String, url: URL, size: UInt64) -> [Date] {
        var state = toolScans[sessionID] ?? (size > Self.toolScanFirstLook ? size - Self.toolScanFirstLook : 0, [])
        if size < state.offset { state = (0, []) }
        if size > state.offset, let handle = ClaudeTranscript.openTranscript(at: url) {
            defer { try? handle.close() }
            if (try? handle.seek(toOffset: state.offset)) != nil,
               let data = try? handle.read(upToCount: Self.toolScanChunk), !data.isEmpty,
               let lastNewline = data.lastIndex(of: UInt8(ascii: "\n")) {
                let whole = data[data.startIndex...lastNewline]
                state.stamps += ClaudeTranscript.toolUses(inTail: whole)
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
              let end = fileStamp(at: url)?.size else { return titles[sessionID]?.title }
        if titles[sessionID] == nil, titles.count >= Self.titleCacheLimit,
           let oldest = titles.min(by: { $0.value.access < $1.value.access }) {
            titles.removeValue(forKey: oldest.key)
        }
        var state = titles[sessionID] ?? TitleScan()
        if end < state.cursor.readOffset { state = TitleScan() }   // rewritten from scratch
        titleAccess &+= 1
        state.access = titleAccess
        if end > state.cursor.readOffset, let handle = ClaudeTranscript.openTranscript(at: url) {
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
        guard let names = try? fileManager.contentsOfDirectory(atPath: directory.path) else {
            settledSubagents[sessionID] = nil
            return []
        }
        let metadata = names.filter { $0.hasSuffix(".meta.json") }
        if let settled = settledSubagents[sessionID] {
            let present = Set(metadata.map { String($0.dropLast(".meta.json".count)) })
            for id in settled.keys where !present.contains(id) { settledSubagents[sessionID]?[id] = nil }
            if settledSubagents[sessionID]?.isEmpty == true { settledSubagents[sessionID] = nil }
        }
        let directoryPath = directory.path + "/"
        return metadata.compactMap { name -> Subagent? in
            let id = String(name.dropLast(".meta.json".count))
            let metaPath = directoryPath + name
            if let settled = settledSubagents[sessionID]?[id] {
                let watched = settled.finished ? metaPath : directoryPath + id + ".jsonl"
                if SettledSubagent(path: watched, finished: settled.finished) == settled { return nil }
                settledSubagents[sessionID]?[id] = nil
            }
            guard let log = SettledSubagent(path: directoryPath + id + ".jsonl", finished: false) else { return nil }
            let modified = log.modified
            guard now.timeIntervalSince(modified) < 120 else {
                if now.timeIntervalSince(modified) > 3600 { settledSubagents[sessionID, default: [:]][id] = log }
                return nil
            }
            let meta = directory.appendingPathComponent(name)
#if DEBUG
            Self.beforeSubagentMetadataOpenForTesting?(meta)
#endif
            guard let input = ClaudeTranscript.openTranscript(at: meta) else { return nil }
            defer { try? input.close() }
#if DEBUG
            Self.afterSubagentMetadataOpenForTesting?(meta)
#endif
            var before = stat()
            let maximum = CredentialFileReader.defaultMaximumBytes
            guard fstat(input.fileDescriptor, &before) == 0,
                  before.st_size >= 0, before.st_size <= maximum else { return nil }
            let observed = SettledSubagent(info: before, finished: true)
            guard let data = try? input.read(upToCount: Int(before.st_size) + 1), data.count <= maximum,
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            else { return nil }
            if json["finished"] as? Bool == true {
                // Bind the parse to the open inode, including when the pathname changes and returns.
                var after = stat()
                if fstat(input.fileDescriptor, &after) == 0,
                   SettledSubagent(info: after, finished: true) == observed,
                   SettledSubagent(path: metaPath, finished: true) == observed {
                    settledSubagents[sessionID, default: [:]][id] = observed
                }
                return nil
            }
            let started = ((try? fileManager.attributesOfItem(atPath: meta.path))?[.creationDate] as? Date) ?? modified
            let title = (json["description"] as? String).flatMap { $0.isEmpty ? nil : $0 }
                ?? (json["agentType"] as? String) ?? "Sub-agent"
            return Subagent(id: id, title: title, startedAt: started)
        }.sorted { $0.startedAt == $1.startedAt ? $0.id < $1.id : $0.startedAt < $1.startedAt }
    }

    private func fileStamp(at url: URL, previous: Cached? = nil) -> (modified: Date?, size: UInt64?, unchanged: Bool)? {
#if CODERIM_RUST_ACCOUNTING
        if fileManager === FileManager.default {
            var stamp = CRClaudeTranscriptStamp()
            let result = url.withUnsafeFileSystemRepresentation {
                cr_claude_transcript_stamp($0, previous?.size ?? 0, previous?.modified.timeIntervalSinceReferenceDate ?? .nan, &stamp)
            }
            if result == 1 { return (Date(timeIntervalSinceReferenceDate: stamp.modified), stamp.size, stamp.unchanged) }
            if result == 0 { return nil }
        }
#endif
        guard let attributes = try? fileManager.attributesOfItem(atPath: url.path) else { return nil }
        let modified = attributes[.modificationDate] as? Date
        let size = (attributes[.size] as? NSNumber)?.uint64Value
        return (modified, size, previous?.modified == modified && previous?.size == (size ?? 0))
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
