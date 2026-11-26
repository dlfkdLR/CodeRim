import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting

// A published sample is immutable, so copying a watcher shares only read-only state.
private final class CompletionKeys {
    let raw = cr_completion_keys_new()
    deinit { cr_completion_keys_free(raw) }

    func swiftKeys() -> Set<String> {
        var result = Set<String>()
        defer { withExtendedLifetime(self) {} }
        withUnsafeMutablePointer(to: &result) { keys in
            cr_completion_copy_keys(raw, { context, bytes, count in
                guard let context, let bytes else { return }
                let key = String(decoding: UnsafeBufferPointer(start: bytes, count: count), as: UTF8.self)
                context.assumingMemoryBound(to: Set<String>.self).pointee.insert(key)
            }, keys)
        }
        return result
    }
}
#endif

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

    // A vanished session is forgotten. Newly seen sessions never announce a completion.
    private var swiftBusy: Set<String>?
#if CODERIM_RUST_ACCOUNTING
    private var asciiBusy: CompletionKeys?
#endif

    mutating func absorb(_ sessions: [String: [AgentSession]]) -> [Event] {
#if CODERIM_RUST_ACCOUNTING
        if swiftBusy == nil {
            if let events = absorbASCII(sessions) { return events }
            swiftBusy = asciiBusy?.swiftKeys()
            asciiBusy = nil
        }
#endif
        let prior = swiftBusy ?? []
        var busy = Set<String>()
        var events: [Event] = []
        for (provider, live) in sessions {
            for session in live {
                if session.state != .busy && busy.isEmpty && prior.isEmpty { continue }
                let key = "\(provider)\u{1}\(session.id)"
                if session.state == .busy { busy.insert(key) } else { busy.remove(key) }
                if prior.contains(key), let reason = Self.reason(from: .busy, to: session.state) {
                    events.append(Event(session: session, reason: reason, providerID: provider))
                }
            }
        }
        swiftBusy = busy.isEmpty ? nil : busy
        return events.sorted { $0.session.since > $1.session.since }
    }

#if CODERIM_RUST_ACCOUNTING
    // The monitor normally supplies ASCII provider and session IDs. An arbitrary
    // Unicode ID switches the whole sample to Swift's original String equality.
    private mutating func absorbASCII(_ sessions: [String: [AgentSession]]) -> [Event]? {
        let prior = asciiBusy
        var current: CompletionKeys?
        defer { withExtendedLifetime((prior, current)) {} }
        var events: [Event] = []
        for (provider, live) in sessions {
            for session in live {
                if session.state == .busy && current == nil { current = CompletionKeys() }
                guard prior != nil || current != nil else { continue }
                let state: UInt8 = switch session.state {
                case .busy: 0
                case .idle: 1
                case .waiting: 2
                case .unavailable: 3
                }
                var key = "\(provider)\u{1}\(session.id)"
                let observation = key.withUTF8 {
                    cr_completion_observe(prior?.raw, current?.raw, $0.baseAddress, $0.count, state)
                }
                if observation < 0 { return nil }
                if observation != 0 {
                    events.append(Event(session: session, reason: observation == 1 ? .finished : .blocked, providerID: provider))
                }
            }
        }
        asciiBusy = current
        return events.sorted { $0.session.since > $1.session.since }
    }
#endif

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
