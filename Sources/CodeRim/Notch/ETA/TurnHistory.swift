import Foundation

/// How long finished turns actually took, kept per agent so the estimator has
/// something to condition on.
///
/// Claude and Codex spend their time differently, and a project's turns are
/// more alike than the world's — so samples are filed by agent and carry the
/// project they came from. Nothing here is a prompt or a path; a turn is a
/// length of time and the folder name it ran in.
struct TurnHistory: Codable, Equatable, Sendable {
    struct Sample: Codable, Equatable, Sendable {
        let seconds: Double
        let project: String
        let recordedAt: Date
        /// Tools the agent called in the turn, when its log says. A turn that
        /// called forty is not the same kind of turn as one that called none,
        /// whatever their lengths; older records and agents that do not say
        /// leave it nil.
        let toolCalls: Int?

        init(seconds: Double, project: String, recordedAt: Date, toolCalls: Int? = nil) {
            self.seconds = seconds
            self.project = project
            self.recordedAt = recordedAt
            self.toolCalls = toolCalls
        }
    }

    /// Per agent, oldest first. Capped so the file stays small and the
    /// estimator keeps following the user's current habits rather than last
    /// year's.
    private(set) var samples: [String: [Sample]] = [:]

    static let perAgentLimit = 400
    /// A turn under two seconds is a refusal or a slash command, and one over
    /// six hours is a session somebody walked away from. Neither says how long
    /// work takes.
    static let plausible: ClosedRange<Double> = 2...(6 * 60 * 60)

    mutating func record(agent: String, project: String, seconds: Double, at date: Date,
                          toolCalls: Int? = nil) {
        guard seconds.isFinite, Self.plausible.contains(seconds) else { return }
        var list = samples[agent, default: []]
        list.append(Sample(seconds: seconds, project: project, recordedAt: date, toolCalls: toolCalls))
        if list.count > Self.perAgentLimit { list.removeFirst(list.count - Self.perAgentLimit) }
        samples[agent] = list
    }

    func durations(agent: String, project: String? = nil) -> [Double] {
        (samples[agent] ?? [])
            .filter { project == nil || $0.project == project }
            .map(\.seconds)
    }

    /// Merge turns read from an agent's own logs. A turn is identified by when
    /// it was recorded and how long it took, so reading the same log twice
    /// adds nothing.
    ///
    /// A turn kept before its tool calls were known gains them when the log is
    /// read again, so a history built on older versions is filled in over time.
    mutating func absorb(agent: String, _ imported: [Sample]) {
        var existing = samples[agent] ?? []
        var index: [Key: Int] = [:]
        for (position, sample) in existing.enumerated() { index[Key(seconds: sample.seconds, at: sample.recordedAt)] = position }
        var filled = false
        var fresh: [Sample] = []
        var added = Set<Key>()
        for sample in imported where Self.plausible.contains(sample.seconds) && sample.seconds.isFinite {
            let key = Key(seconds: sample.seconds, at: sample.recordedAt)
            if let position = index[key] {
                if existing[position].toolCalls == nil, let calls = sample.toolCalls {
                    existing[position] = Sample(seconds: sample.seconds, project: existing[position].project,
                                                recordedAt: existing[position].recordedAt, toolCalls: calls)
                    filled = true
                }
            } else if added.insert(key).inserted {
                fresh.append(sample)
            }
        }
        guard !fresh.isEmpty || filled else { return }
        if filled { samples[agent] = existing }
        var list = existing + fresh
        list.sort { $0.recordedAt < $1.recordedAt }
        if list.count > Self.perAgentLimit { list.removeFirst(list.count - Self.perAgentLimit) }
        samples[agent] = list
    }

    private struct Key: Hashable {
        let milliseconds: Int
        let second: Int
        init(seconds: Double, at date: Date) {
            milliseconds = Int((seconds * 1000).rounded())
            second = Int(date.timeIntervalSince1970.rounded())
        }
    }
}
