import Combine
import Foundation

/// Turn boundaries keep activity visible through silent reasoning and long tools.
/// Catalogue timestamps are metadata updates, not evidence of a running turn.
@MainActor
final class CodexActivityMonitor: ObservableObject, AgentActivityMonitor {
    @Published private(set) var sessions: [AgentSession] = []
    var sessionsPublisher: AnyPublisher<[AgentSession], Never> { $sessions.eraseToAnyPublisher() }

    private let stateStore: URL
    private let desktopStore: URL
    private let profile: CodexProfile
    private let interval: TimeInterval
    private let reader = CodexActivityReader()
    private var timer: Timer?
    private var scan: Task<Void, Never>?

    init(
        profile: CodexProfile = .default(),
        stateStore: URL? = nil,
        desktopStore: URL? = nil,
        interval: TimeInterval = 2
    ) {
        self.profile = profile
        self.stateStore = stateStore ?? profile.stateURL
        self.desktopStore = desktopStore ?? profile.desktopStoreURL
        self.interval = interval
    }

    func start() {
        stop()
        rescan()
        let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.rescan() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        scan?.cancel()
        scan = nil
    }

    private func rescan() {
        guard scan == nil else { return }
        scan = Task { [weak self] in
            guard let self else { return }
            let found = await reader.read(stateStore: stateStore, desktopStore: desktopStore,
                                          profile: profile)
            guard !Task.isCancelled else { return }
            if found != sessions { sessions = found }
            scan = nil
        }
    }
}

/// A question Codex has put to the person and not had an answer to.
///
/// The turn is still open while it waits, so the start/complete markers alone
/// call it working. The question is a `request_user_input*` tool call; it is
/// answered when something later in the log names its call id: the tool's
/// output, or the person's reply written back as a message.
///
/// The log only grows, so each poll reads what was added since the last one.
/// The agent keeps working while a question is open (screenshots, tool
/// results), which can bury the question under megabytes, so a fixed window at
/// the end of the file cannot be relied on.
struct CodexPendingQuestion {
    /// How far back the first read goes in a log that is already long.
    static let firstReadBytes: UInt64 = 64 * 1_024 * 1_024

    private static let marker = Data("request_user_input".utf8)
    private var offset: UInt64 = 0
    private var since: Date = .distantPast
    private var identity: String?
    private var open: [String: String] = [:]   // call id -> question
    private var order: [String] = []
    /// The `_async` form returns `accepted` at once and the answer comes later
    /// as a message; the plain form is answered by its own output.
    private var isAsync: Set<String> = []

    /// The last unanswered question, as its text.
    var question: String? { order.last(where: { open[$0] != nil }).flatMap { open[$0] } }

    mutating func update(_ url: URL, since newSince: Date) {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = (attributes[.size] as? NSNumber)?.uint64Value,
              let device = attributes[.systemNumber] as? NSNumber,
              let inode = attributes[.systemFileNumber] as? NSNumber,
              let handle = try? FileHandle(forReadingFrom: url) else { return }
        defer { try? handle.close() }
        let id = "\(device):\(inode)"
        var aligning = false
        if identity != id || size < offset || newSince != since {
            self = CodexPendingQuestion()
            identity = id
            since = newSince
            offset = size - min(size, Self.firstReadBytes)
            aligning = offset > 0
        }
        guard size > offset, (try? handle.seek(toOffset: offset)) != nil,
              var data = try? handle.read(upToCount: Int(size - offset)) else { return }
        var base = offset
        if aligning {
            // The window began mid-record: start after its first newline.
            guard let newline = data.firstIndex(of: 10) else { return }
            let skipped = data.distance(from: data.startIndex, to: newline) + 1
            data = Data(data.dropFirst(skipped))
            base += UInt64(skipped)
        }
        consume(data[...])
        let used = data.lastIndex(of: 10).map { data.distance(from: data.startIndex, to: $0) + 1 } ?? 0
        offset = base + UInt64(used)
    }

    /// Reads the complete lines of `data`; a final unfinished line is left for the next read.
    mutating func consume(_ data: Data.SubSequence) {
        guard data.contains(10) else { return }
        for line in data.split(separator: 10, omittingEmptySubsequences: true) {
            if !open.isEmpty { answer(line) }
            guard line.range(of: Self.marker) != nil,
                  let json = try? JSONSerialization.jsonObject(with: Data(line)) as? [String: Any],
                  let payload = json["payload"] as? [String: Any],
                  payload["type"] as? String == "function_call",
                  let name = payload["name"] as? String, name.hasPrefix("request_user_input"),
                  let id = payload["call_id"] as? String else { continue }
            if let stamp = json["timestamp"] as? String, let date = Self.timestamp(stamp), date < since { continue }
            open[id] = Self.question(from: payload["arguments"] as? String)
            order.append(id)
            if name.hasSuffix("_async") { isAsync.insert(id) }
        }
    }

    /// Only the tool's output (for the plain form) or the person's reply
    /// answers a question: the log also records the call itself a second time,
    /// as an event, and the async form's output only says it was accepted.
    private mutating func answer(_ line: Data.SubSequence) {
        let reply = line.range(of: Data("send_user_message_question_reply".utf8)) != nil
        let output = line.range(of: Data("\"function_call_output\"".utf8)) != nil
        guard reply || output else { return }
        for id in open.keys where line.range(of: Data(id.utf8)) != nil {
            if reply || !isAsync.contains(id) { open[id] = nil }
        }
    }

    /// Everything in `data`, for tests and one-off reads.
    static func pending(in data: Data, since: Date) -> String? {
        var tracker = CodexPendingQuestion()
        tracker.since = since
        tracker.consume(data[...])
        return tracker.question
    }

    private static func question(from arguments: String?) -> String {
        guard let arguments, let data = arguments.data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let questions = json["questions"] as? [[String: Any]],
              let first = (questions.first?["title"] ?? questions.first?["question"]) as? String
        else { return "A question for you" }
        return first
    }

    private static func timestamp(_ value: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: value) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: value)
    }
}

actor CodexActivityReader {
    /// Whether Codex is set to its fast service tier. The tier lives in the
    /// profile's config, not per chat, so every chat shows the same answer.
    static func fastTier(profile: CodexProfile) -> Bool {
        let config = profile.configDirectory.appendingPathComponent("config.toml")
        guard let text = try? String(contentsOf: config, encoding: .utf8) else { return false }
        for line in text.split(separator: "\n").prefix(200) {
            // Only the top level counts: a key under a [table] belongs to something else.
            if line.trimmingCharacters(in: .whitespaces).hasPrefix("[") { break }
            let parts = line.split(separator: "=", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
            guard parts.count == 2, parts[0] == "service_tier" else { continue }
            let value = parts[1].trimmingCharacters(in: CharacterSet(charactersIn: "\"' "))
            return value == "fast" || value == "priority"
        }
        return false
    }

    private var cache: [String: CodexTurnActivity.Reader] = [:]
    private var questions: [String: CodexPendingQuestion] = [:]

    /// Reads only what the log gained since the last look.
    private func pendingQuestion(_ url: URL, since: Date) -> String? {
        var tracker = questions[url.path] ?? CodexPendingQuestion()
        tracker.update(url, since: since)
        questions[url.path] = tracker
        return tracker.question
    }

    func read(stateStore: URL, desktopStore: URL, profile: CodexProfile = .default(),
              now: Date = Date()) -> [AgentSession] {
        let threads = CodexStore.threads(in: stateStore, desktopStore: desktopStore, limit: 64)
        let paths = Set(threads.map { $0.rollout.path })
        cache = cache.filter { paths.contains($0.key) }
        questions = questions.filter { paths.contains($0.key) }
        let fast = Self.fastTier(profile: profile)
        let local: [AgentSession] = threads.compactMap { thread in
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: thread.rollout.path),
                  let modified = attributes[.modificationDate] as? Date,
                  // Bound orphaned turns after a client crash with no end event.
                  now.timeIntervalSince(modified) < 6 * 60 * 60 else { return nil }
            var reader = cache[thread.rollout.path] ?? CodexTurnActivity.Reader()
            let event = reader.read(thread.rollout)
            cache[thread.rollout.path] = reader
            guard let event else { return nil }
            // Keep ended sessions briefly so completion detection can observe busy -> idle.
            guard event.isRunning || now.timeIntervalSince(event.since) < 90 else { return nil }
            let question = event.isRunning ? pendingQuestion(thread.rollout, since: event.since) : nil
            return AgentSession(id: "\(profile.id).\(thread.id)", name: thread.projectName,
                                detail: thread.displayTitle,
                                state: question != nil ? .waiting : event.isRunning ? .busy : .idle,
                                waitingFor: question, since: event.since, codexThreadID: thread.id,
                                parentThread: thread.parentThread,
                                projectKey: URL(fileURLWithPath: thread.cwd).lastPathComponent,
                                details: ModelDetails(model: thread.model, effort: thread.effort,
                                                      fast: fast, daybreak: thread.daybreak))
        }
        return local + CodexRemoteCatalog.sessions(in: desktopStore, profile: profile, now: now)
    }
}
