import Darwin
import Foundation

struct Row: Codable {
    let provider, id: String
    let state, rank, position: Int
}
struct Input: Codable {
    let lane: Int
    let copyFrom: Int?
    let rows: [Row]
}
let states: [AgentSession.State] = [.busy, .waiting, .idle, .unavailable]
func session(_ id: String, state: AgentSession.State, rank: Int, position: Int) -> AgentSession {
    AgentSession(id: id, name: String(position), detail: "row \(position)", state: state, waitingFor: nil,
                 since: Date(timeIntervalSinceReferenceDate: Double(rank)), processID: Int32(position),
                 codexThreadID: "thread-\(position)", parentThread: position % 5 == 0 ? .init(id: "parent", title: "p") : nil)
}
func snapshot(_ rows: [Row]) -> [String: [AgentSession]] {
    var result: [String: [AgentSession]] = [:]
    for row in rows {
        result[row.provider, default: []].append(session(row.id, state: states[row.state], rank: row.rank, position: row.position))
    }
    return result
}
let args = CommandLine.arguments
if args.count == 1 {
    var levels = (0..<4).map { _ in SessionCompletionWatcher() }
    while let line = readLine() {
        let frame = try JSONDecoder().decode(Input.self, from: Data(line.utf8))
        if let src = frame.copyFrom { levels[frame.lane] = levels[src] }
        let events = levels[frame.lane].absorb(snapshot(frame.rows))
        var text = events.map { "\($0.session.name):\($0.reason == .finished ? 1 : 2):\($0.session.processID ?? -1)" }
        for allow in [false, true] {
            text.append(SessionCompletionWatcher.soundEvent(in: events, includingSubagents: allow)?.session.name ?? "_")
        }
        print(text.joined(separator: ","))
    }
} else {
    let count = Int(args[1])!, shape = args[2], reps = Int(args[3])!
    let frames = (0..<4).map { phase in
        snapshot((0..<count).map { i in
            let mode: Int
            switch shape {
            case "idle": mode = 2
            case "busy": mode = 0
            case "one-busy": mode = i == 0 ? 0 : 2
            default: mode = (i + phase) % 4
            }
            return Row(provider: "provider-\(i % 3)", id: (shape == "unicode" ? "작업-" : "session-") + String(i),
                       state: mode, rank: i % 9, position: i)
        })
    }
    var watcher = SessionCompletionWatcher()
    var total = 0
    for i in 0..<8 { total += watcher.absorb(frames[i % 4]).count }
    let cpu = clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID)
    let time = DispatchTime.now().uptimeNanoseconds
    for i in 0..<reps { total += watcher.absorb(frames[i % 4]).count }
    print(DispatchTime.now().uptimeNanoseconds-time, clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID)-cpu, total)
}
