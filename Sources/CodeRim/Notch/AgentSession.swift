import Darwin
import Foundation

/// Model metadata reported by the provider, independent of its registry or database format.
struct ModelDetails: Equatable, Sendable {
    var model: String?
    var effort: String?
    var fast = false
    var daybreak = false

    var isEmpty: Bool { model == nil && effort == nil && !fast && !daybreak }
}

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
    /// Used only for SessionFocus; database/log-only providers may have no process, and Codex uses its
    /// thread ID.
    let processID: pid_t?
    /// The original Codex thread ID, without the display model's profile prefix.
    let codexThreadID: String?
    let parentThread: ParentThread?
    /// Project basename supplied by the monitor, when available.
    let projectKey: String?
    /// Instructions queued while this turn was already running.
    let followUps: Int
    /// Tool calls observed in the current turn.
    let toolCalls: Int
    /// The id the agent files its own transcript and hooks under, when it
    /// differs from `id`. Claude Code's hooks name sessions this way.
    let transcriptID: String?
    /// The agent says it is summarising its own context right now.
    let isCompacting: Bool
    /// Stable host identity supplied by the collector; nil means this machine.
    let remoteHostID: String?
    /// Transcript identity for providers whose UI row is keyed by a process.
    let usageSessionID: String?
    /// Where a process-based session runs; lets a terminal find its tab.
    let workingDirectory: String?
    let details: ModelDetails?
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
        usageSessionID: String? = nil,
        workingDirectory: String? = nil,
        projectKey: String? = nil,
        followUps: Int = 0,
        toolCalls: Int = 0,
        transcriptID: String? = nil,
        isCompacting: Bool = false,
        details: ModelDetails? = nil
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
        self.workingDirectory = workingDirectory
        let host = remoteHostID?.trimmingCharacters(in: .whitespacesAndNewlines)
        self.remoteHostID = host == nil || host == "" || host == "local" ? nil : host
        self.projectKey = projectKey
        self.followUps = followUps
        self.toolCalls = toolCalls
        self.transcriptID = transcriptID
        self.isCompacting = isCompacting
        self.details = details
    }
}

extension AgentSession {
    /// The same session with what it runs on filled in.
    func with(details: ModelDetails?, toolCalls: Int? = nil) -> AgentSession {
        AgentSession(id: id, name: name, detail: detail, state: state, waitingFor: waitingFor, since: since,
                     processID: processID, codexThreadID: codexThreadID, parentThread: parentThread,
                     remoteHostID: remoteHostID, usageSessionID: usageSessionID,
                     workingDirectory: workingDirectory, projectKey: projectKey, followUps: followUps,
                     toolCalls: toolCalls ?? self.toolCalls,
                     transcriptID: transcriptID, isCompacting: isCompacting, details: details)
    }

    /// The same session under another display name.
    func renamed(_ name: String) -> AgentSession {
        AgentSession(id: id, name: name, detail: detail, state: state, waitingFor: waitingFor, since: since,
                     processID: processID, codexThreadID: codexThreadID, parentThread: parentThread,
                     remoteHostID: remoteHostID, usageSessionID: usageSessionID,
                     workingDirectory: workingDirectory, projectKey: projectKey, followUps: followUps, toolCalls: toolCalls,
                     transcriptID: transcriptID, isCompacting: isCompacting, details: details)
    }
}
