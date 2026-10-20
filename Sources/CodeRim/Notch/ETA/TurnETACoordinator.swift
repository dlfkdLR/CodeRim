import Foundation

/// Turns the monitors' session lists into a live estimate per working session,
/// and learns from every turn that finishes.
///
/// A value type with no clock, no files and no AppKit, like
/// `SessionCompletionWatcher`: `TurnETAStore` feeds it and persists its
/// history, tests feed it directly.
struct TurnETACoordinator {
    private(set) var history: TurnHistory
    private var trackers: [String: Tracked] = [:]

    private struct Tracked {
        var tracker: TurnETATracker
        var providerID: String
        var project: String?
        var followUps: Int
        var lastSeen: Date
    }

    /// Sessions for which nothing is reported: the person is not asking about
    /// them, or no estimate means anything yet.
    struct Result: Equatable {
        /// Keyed `"<provider>\u{1}<session id>"`.
        var snapshots: [String: TurnETATracker.Snapshot] = [:]
        /// True when a turn ended on this pass and history grew.
        var learned = false
    }

    init(history: TurnHistory = TurnHistory()) {
        self.history = history
    }

    static func key(_ providerID: String, _ sessionID: String) -> String {
        "\(providerID)\u{1}\(sessionID)"
    }

    /// `compacting` names sessions known to be summarising their own context.
    mutating func absorb(
        _ sessions: [String: [AgentSession]],
        compacting: Set<String> = [],
        now: Date
    ) -> Result {
        var result = Result()
        var seen: Set<String> = []

        for (providerID, live) in sessions {
            for session in live {
                let key = Self.key(providerID, session.id)
                seen.insert(key)
                let phase: TurnETATracker.Phase
                switch session.state {
                case .busy:    phase = compacting.contains(key) || session.isCompacting ? .compacting : .working
                case .waiting: phase = .waiting
                case .idle:    phase = .finished
                }

                if phase == .finished {
                    // Only a turn this coordinator watched end teaches it anything;
                    // one that was already finished when first seen has no measured length.
                    if var tracked = trackers.removeValue(forKey: key) {
                        let snapshot = tracked.tracker.update(
                            phase: .finished, now: now,
                            samples: [], project: tracked.project)
                        history.record(agent: providerID, project: tracked.project ?? "",
                                       seconds: snapshot.elapsed, at: now)
                        result.learned = true
                    }
                    continue
                }

                var tracked = trackers[key] ?? Tracked(
                    tracker: TurnETATracker(alreadyRunning: max(0, now.timeIntervalSince(session.since))),
                    providerID: providerID, project: session.projectKey,
                    followUps: session.followUps, lastSeen: now)
                if session.followUps > tracked.followUps {
                    tracked.tracker.noteFollowUp()
                    tracked.followUps = session.followUps
                }
                tracked.lastSeen = now
                let samples = history.samples[providerID] ?? []
                result.snapshots[key] = tracked.tracker.update(
                    phase: phase, now: now, samples: samples, project: tracked.project)
                trackers[key] = tracked
            }
        }
        // A session that disappeared mid-turn (the window closed, the process
        // was killed) never finished, so it teaches nothing.
        trackers = trackers.filter { seen.contains($0.key) }
        return result
    }

    mutating func merge(imported samples: [TurnHistory.Sample], agent: String) {
        history.absorb(agent: agent, samples)
    }
}
