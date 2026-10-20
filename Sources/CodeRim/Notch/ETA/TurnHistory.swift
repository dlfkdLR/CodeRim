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

    mutating func record(agent: String, project: String, seconds: Double, at date: Date) {
        guard seconds.isFinite, Self.plausible.contains(seconds) else { return }
        var list = samples[agent, default: []]
        list.append(Sample(seconds: seconds, project: project, recordedAt: date))
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
    mutating func absorb(agent: String, _ imported: [Sample]) {
        var seen = Set((samples[agent] ?? []).map { Key(seconds: $0.seconds, at: $0.recordedAt) })
        let fresh = imported
            .filter { Self.plausible.contains($0.seconds) && $0.seconds.isFinite }
            .filter { seen.insert(Key(seconds: $0.seconds, at: $0.recordedAt)).inserted }
        guard !fresh.isEmpty else { return }
        var list = (samples[agent] ?? []) + fresh
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
