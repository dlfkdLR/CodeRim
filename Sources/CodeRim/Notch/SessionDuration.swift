import Foundation

/// Catalog timestamps and idle timestamps are not evidence of work duration.
enum SessionDuration {
    static func text(for session: AgentSession, enabled: Bool, now: Date) -> String? {
        guard enabled, session.state == .busy || session.state == .waiting,
              session.since.timeIntervalSince1970.isFinite,
              now.timeIntervalSince1970.isFinite, session.since <= now else { return nil }
        let seconds = now.timeIntervalSince(session.since)
        guard seconds.isFinite, Int(exactly: (seconds / 60).rounded()) != nil else { return nil }
        let text = ElapsedCopy.text(since: session.since, now: now)
        guard text != "unknown" else { return nil }
        return text == "just now" ? "<1 min" : text
    }
}
