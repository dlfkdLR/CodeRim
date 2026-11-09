import Foundation

/// Compare successive session states and report crossings only. Keep this state machine free of
/// AppKit for direct tests.
struct SessionCompletionWatcher {
    /// Why a session is being announced.
    enum Reason: Equatable {
        /// Ran to the end of its turn.
        case finished
        /// Stopped to ask something, and is waiting on an answer.
        case blocked
    }

    struct Event: Equatable {
        let session: AgentSession
        let reason: Reason
        /// Which provider's ring it belongs to, so the notch can point at it.
        let providerID: String
    }

    /// The last state seen for every session, keyed by provider and session id.
    private var previous: [String: AgentSession.State] = [:]
    /// Seed the first reading without events so app launches and updates do not chime for existing
    /// sessions.
    private var hasSeeded = false

    /// Feed the monitors' current view; get back what just changed.
    mutating func absorb(_ sessions: [String: [AgentSession]]) -> [Event] {
        var current: [String: AgentSession.State] = [:]
        var events: [Event] = []

        for (providerID, live) in sessions {
            for session in live {
                let key = "\(providerID)\u{1}\(session.id)"
                current[key] = session.state
                guard hasSeeded, let was = previous[key] else { continue }
                guard let reason = Self.reason(from: was, to: session.state) else { continue }
                events.append(Event(session: session, reason: reason, providerID: providerID))
            }
        }

        // Sessions that vanished are dropped rather than announced. A session
        // file disappears when the process exits — often *while* it was busy,
        // because quitting Claude Code mid-turn is an ordinary thing to do —
        // and a chime for a window that is already gone points at nothing.
        previous = current
        hasSeeded = true
        // Newest first, so the one that just landed is the one a single click
        // reaches.
        return events.sorted { $0.session.since > $1.session.since }
    }

    /// A session started by another session rather than by the user.
    static func isSubagent(_ session: AgentSession) -> Bool {
        guard let parent = session.parentThread else { return false }
        return parent.id != session.codexThreadID
    }

    /// Select an eligible event, not merely the newest: a silent subagent must not hide its parent’s
    /// completion.
    static func soundEvent(in events: [Event], includingSubagents: Bool) -> Event? {
        events.first { includingSubagents || !isSubagent($0.session) }
    }

    /// Only transitions out of busy end work; waiting→idle and idle→idle must not announce completion.
    static func reason(from was: AgentSession.State, to now: AgentSession.State) -> Reason? {
        guard was == .busy else { return nil }
        switch now {
        case .idle:    return .finished
        case .waiting: return .blocked
        case .busy, .unavailable: return nil
        }
    }
}
