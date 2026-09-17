import SwiftUI

/// What the activity cell shows: the state of every live session, reduced to
/// the one thing worth knowing at a glance.
struct ActivitySummary: Equatable {
    enum State: Equatable {
        case working
        case waiting
        case idle
    }

    let state: State
    let sessions: [AgentSession]

    /// Nil when nothing is running — the cell disappears rather than sitting
    /// there saying nothing.
    init?(sessions: [AgentSession]) {
        guard !sessions.isEmpty else { return nil }
        self.sessions = sessions
        // Anything blocked on you outranks anything merely busy: it is the only
        // state where the notch is asking for something.
        if sessions.contains(where: { $0.state == .waiting }) {
            state = .waiting
        } else if sessions.contains(where: { $0.state == .busy }) {
            state = .working
        } else {
            state = .idle
        }
    }

    /// One short word, for the tooltip.
    var label: String {
        switch state {
        case .working: return "working"
        case .waiting: return "waiting"
        case .idle:    return "idle"
        }
    }

    /// White for working, deliberately: the indicator sits inside a ring whose
    /// colour already means "how much of your limit is gone", and a neutral
    /// tone cannot be misread as part of that scale. Waiting gets amber because
    /// it is the one state that wants something from you.
    var color: Color {
        switch state {
        case .working: return NotchPalette.textPrimary
        case .waiting: return NotchPalette.watch
        case .idle:    return NotchPalette.ringTrack
        }
    }

    /// Display-only context rows never enter activity/completion detection.
    struct Row: Identifiable, Equatable {
        let session: AgentSession
        let depth: Int
        let isContextOnly: Bool
        let isParent: Bool
        var inlineParent: AgentSession.ParentThread? = nil
        var id: String { session.id }
    }

    var displayRows: [Row] {
        var nodes = Dictionary(sessions.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let byThread = Dictionary(sessions.compactMap { session in
            session.codexThreadID.map { ($0, session.id) }
        }, uniquingKeysWith: { first, _ in first })
        var contextIDs = Set<String>()
        var parentIDs: [String: String] = [:]
        for session in sessions {
            guard let parent = session.parentThread, parent.id != session.codexThreadID else { continue }
            let key = byThread[parent.id] ?? "parent.\(parent.id)"
            if nodes[key] == nil {
                nodes[key] = AgentSession(id: key, name: session.name, detail: parent.title,
                    state: .idle, waitingFor: nil, since: .distantPast, codexThreadID: parent.id)
                contextIDs.insert(key)
            }
            parentIDs[session.id] = key
        }
        // A corrupt relationship must not recurse forever or hide an agent.
        for key in parentIDs.keys.sorted() {
            var seen: Set<String> = [key]
            var cursor = parentIDs[key]
            while let current = cursor {
                if !seen.insert(current).inserted { parentIDs.removeValue(forKey: key); break }
                cursor = parentIDs[current]
            }
        }
        let children = Dictionary(grouping: parentIDs.keys, by: { parentIDs[$0]! })
        func rank(_ state: AgentSession.State) -> Int {
            switch state { case .waiting: 0; case .busy: 1; case .idle: 2 }
        }
        var priorities: [String: (rank: Int, since: Date)] = [:]
        func priority(_ key: String) -> (rank: Int, since: Date) {
            if let cached = priorities[key] { return cached }
            let node = nodes[key]!
            var result = (rank: rank(node.state), since: node.since)
            for child in children[key] ?? [] {
                let next = priority(child)
                result.rank = min(result.rank, next.rank)
                result.since = max(result.since, next.since)
            }
            priorities[key] = result
            return result
        }
        func ordered(_ keys: [String]) -> [String] {
            keys.sorted {
                let a = priority($0), b = priority($1)
                if a.rank != b.rank { return a.rank < b.rank }
                if a.since != b.since { return a.since > b.since }
                return $0 < $1
            }
        }
        var rows: [Row] = []
        func append(_ key: String, depth: Int) {
            let descendants = children[key] ?? []
            rows.append(Row(session: nodes[key]!, depth: depth, isContextOnly: contextIDs.contains(key),
                            isParent: !descendants.isEmpty))
            for child in ordered(descendants) { append(child, depth: depth + 1) }
        }
        for root in ordered(nodes.keys.filter { parentIDs[$0] == nil }) { append(root, depth: 0) }
        return rows
    }

    func presentation(cap: Int) -> (rows: [Row], hidden: Int) {
        let all = displayRows
        var shown = Array(all.prefix(max(0, cap)))
        // Never spend the last slot on a parent heading without its child.
        // In the one-slot case keep both names inside the child's two lines.
        if let last = shown.last, last.isContextOnly, shown.count < all.count {
            let next = all[shown.count]
            shown[shown.count - 1] = Row(session: next.session, depth: 0, isContextOnly: false,
                isParent: next.isParent, inlineParent: next.session.parentThread)
        }
        let visible = Set(shown.filter { !$0.isContextOnly }.map(\.id))
        return (shown, sessions.filter { !visible.contains($0.id) }.count)
    }

    var waitingSessions: [AgentSession] { sessions.filter { $0.state == .waiting } }
}
