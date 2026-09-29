import Darwin
import Foundation

/// One agent session, whichever tool it belongs to.
///
/// Deliberately a *display* model rather than a mirror of any one tool's file
/// format: Claude Code publishes a session registry, Cursor keeps composer rows
/// in SQLite, and neither shape belongs in the notch. Each monitor does its own
/// parsing and hands back this.
struct AgentSession: Identifiable, Equatable, Sendable {
    /// What the session is doing right now.
    enum State: Equatable, Sendable {
        case busy
        case waiting
        case idle
        /// The catalog knows the task, but no live runtime evidence is available.
        case unavailable
    }

    struct ParentThread: Equatable, Sendable {
        let id: String
        let title: String
    }

    let id: String
    /// What to call it in the tooltip.
    let name: String
    /// The quieter second line — where it is running, or what it is doing.
    let detail: String
    let state: State
    /// Set while `waiting`: what it wants from you.
    let waitingFor: String?
    /// When it entered its current state.
    let since: Date
    /// The agent's own process, when the tool publishes one.
    ///
    /// Only used to find the window it is running in — see `SessionFocus`.
    /// Nil for the tools that report activity from a database or a log file
    /// rather than from a process. Codex uses its thread ID instead.
    let processID: pid_t?
    /// The original Codex thread ID, without the display model's profile prefix.
    let codexThreadID: String?
    let parentThread: ParentThread?
    /// Stable host identity supplied by the collector; nil means this machine.
    let remoteHostID: String?
    /// Transcript identity for providers whose UI row is keyed by a process.
    let usageSessionID: String?
    var isRemote: Bool { remoteHostID != nil }
    var locationDescription: String? {
        guard isRemote else { return nil }
        return state == .unavailable
            ? "Remote task · live status unavailable" : "Remote task"
    }

    /// Navigation metadata is optional for providers without a destination.
    init(
        id: String,
        name: String,
        detail: String,
        state: State,
        waitingFor: String?,
        since: Date,
        processID: pid_t? = nil,
        codexThreadID: String? = nil,
        parentThread: ParentThread? = nil,
        remoteHostID: String? = nil,
        usageSessionID: String? = nil
    ) {
        self.id = id
        self.name = name
        self.detail = detail
        self.state = state
        self.waitingFor = waitingFor
        self.since = since
        self.processID = processID
        self.codexThreadID = codexThreadID
        self.parentThread = parentThread
        self.usageSessionID = usageSessionID
        let host = remoteHostID?.trimmingCharacters(in: .whitespacesAndNewlines)
        self.remoteHostID = host == nil || host == "" || host == "local" ? nil : host
    }
}
