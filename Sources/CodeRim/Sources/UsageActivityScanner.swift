import CryptoKit
import Foundation

/// Counts what the agents did, from their own logs: the messages sent to each
/// model, the tools and skills they called, and what each session ran on.
///
/// The logs are read where they lie and never leave this Mac. A file is read
/// once and again only when its size or date changes, so reopening the Usage
/// screen costs a look at the folder, not a read of every transcript.
actor UsageActivityScanner {
    struct Roots: Sendable {
        var claudeProjects: URL
        var codexSessions: [URL]

        static func standard(home: URL = FileManager.default.homeDirectoryForCurrentUser,
                             environment: [String: String] = ProcessInfo.processInfo.environment) -> Roots {
            let claudeHome = environment["CLAUDE_CONFIG_DIR"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0) }
                ?? home.appendingPathComponent(".claude", isDirectory: true)
            let codex = home.appendingPathComponent(".codex", isDirectory: true)
            return Roots(claudeProjects: claudeHome.appendingPathComponent("projects", isDirectory: true),
                         codexSessions: [codex.appendingPathComponent("sessions", isDirectory: true),
                                         codex.appendingPathComponent("archived_sessions", isDirectory: true)])
        }
    }

    private struct Cached {
        let size: UInt64
        let modified: Date
        let activity: FileActivity
    }

    /// The one the Usage screen asks, so what it has read is kept between visits.
    static let shared = UsageActivityScanner()

    /// `snapshot` from outside the actor, for a place that takes a plain function.
    nonisolated static func loadShared(provider: UsageProvider, range: AnalyticsRange, through: Date,
                                       calendar: Calendar) async -> ActivitySnapshot {
        await shared.snapshot(provider: provider, range: range, now: through, calendar: calendar)
    }

    /// A file larger than this is not read: it is not a transcript anyone had.
    static let fileLimit: UInt64 = 256 * 1024 * 1024

    private let roots: Roots
    private let fixedCalendar: Calendar?
    private var calendar: Calendar
    private var days: ActivityDayResolver
    private var cache: [String: Cached] = [:]

    init(roots: Roots = .standard(), calendar: Calendar? = nil) {
        self.roots = roots
        self.fixedCalendar = calendar
        self.calendar = calendar ?? .current
        self.days = ActivityDayResolver(calendar: calendar ?? .current)
    }

    func snapshot(provider: UsageProvider, range: AnalyticsRange, now: Date = Date(),
                  calendar reportingCalendar: Calendar? = nil) -> ActivitySnapshot {
        let current = reportingCalendar ?? fixedCalendar ?? .current
        if current != calendar {
            calendar = current
            days = ActivityDayResolver(calendar: current)
            cache.removeAll()
        }
        let interval = range.interval(through: now, calendar: calendar)
        var chartDays: [Date] = []
        var cursor = calendar.startOfDay(for: interval.start)
        let last = calendar.startOfDay(for: now)
        while cursor <= last {
            chartDays.append(cursor)
            guard let next = calendar.date(byAdding: .day, value: 1, to: cursor) else { break }
            cursor = next
        }

        let files = logFiles(for: provider, since: interval.start)
        var messages: [Date: [String: Int]] = [:]
        var tools: [Date: [String: Int]] = [:]
        var skills: [Date: [String: Int]] = [:]
        var traits: [String: SessionTraits] = [:]
        var seen = Set<String>()
        let wanted = Set(chartDays)

        for file in files {
            guard !Task.isCancelled else { break }
            seen.insert(key(file, provider: provider))
            let activity = read(file, provider: provider)
            if let id = activity.sessionID, !activity.traits.isEmpty { traits[id] = activity.traits }
            for (day, counts) in activity.days where wanted.contains(day) {
                for (model, count) in counts.messagesByModel { messages[day, default: [:]][model, default: 0] += count }
                for (tool, count) in counts.tools { tools[day, default: [:]][tool, default: 0] += count }
                for (skill, count) in counts.skills { skills[day, default: [:]][skill, default: 0] += count }
            }
        }
        cache = cache.filter { seen.contains($0.key) || !$0.key.hasPrefix(prefix(for: provider)) }

        return ActivitySnapshot(
            range: range,
            messages: .build(days: chartDays, counts: messages),
            tools: .build(days: chartDays, counts: tools),
            skills: .build(days: chartDays, counts: skills),
            traits: traits)
    }

    // MARK: - Files

    private struct LogFile {
        let url: URL
        let size: UInt64
        let modified: Date
    }

    private func prefix(for provider: UsageProvider) -> String {
        provider == .claude ? "claude|" : "codex|"
    }

    private func key(_ file: LogFile, provider: UsageProvider) -> String {
        prefix(for: provider) + file.url.path
    }

    /// Every transcript touched since `date`, which is all that can hold a day
    /// of the range.
    private func logFiles(for provider: UsageProvider, since date: Date) -> [LogFile] {
        let directories = provider == .claude ? [roots.claudeProjects] : roots.codexSessions
        var found: [LogFile] = []
        for directory in directories {
            guard let walker = FileManager.default.enumerator(
                at: directory, includingPropertiesForKeys: [.contentModificationDateKey, .fileSizeKey],
                options: [.skipsHiddenFiles]) else { continue }
            for case let url as URL in walker where url.pathExtension == "jsonl" {
                let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey])
                guard let modified = values?.contentModificationDate, modified >= date,
                      let size = values?.fileSize.map(UInt64.init), size <= Self.fileLimit else { continue }
                found.append(LogFile(url: url, size: size, modified: modified))
            }
        }
        return found
    }

    private func read(_ file: LogFile, provider: UsageProvider) -> FileActivity {
        let cacheKey = key(file, provider: provider)
        if let cached = cache[cacheKey], cached.size == file.size, cached.modified == file.modified {
            return cached.activity
        }
        let activity = provider == .claude ? readClaude(file.url) : readCodex(file.url)
        cache[cacheKey] = Cached(size: file.size, modified: file.modified, activity: activity)
        return activity
    }

    private func readClaude(_ url: URL) -> FileActivity {
        var parser = ClaudeActivityParser(days: days)
        TurnHistoryImporter.forEachLine(of: url, containingAny: ClaudeActivityParser.needles, copying: false) {
            parser.consume($0)
        }
        var activity = parser.finish()
        // A subagent's file sits in a folder of that name and is not a session of its own.
        if !url.pathComponents.contains("subagents") {
            activity.sessionID = Self.identifier("claude-session|\(url.deletingPathExtension().lastPathComponent)")
        }
        return activity
    }

    private func readCodex(_ url: URL) -> FileActivity {
        var parser = CodexActivityParser(days: days)
        TurnHistoryImporter.forEachLine(of: url, containingAny: CodexActivityParser.needles, copying: false) {
            parser.consume($0)
        }
        var activity = parser.finish()
        // The rollout's name ends in the session's id.
        let stem = url.deletingPathExtension().lastPathComponent
        let raw = parser.rawSessionID ?? (stem.count >= 36 ? String(stem.suffix(36)) : nil)
        activity.sessionID = raw.map(Self.identifier)
        return activity
    }

    /// The analytics keeps no raw session id; it files each under the SHA-256 of it.
    static func identifier(_ value: String) -> String {
        SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined()
    }
}
