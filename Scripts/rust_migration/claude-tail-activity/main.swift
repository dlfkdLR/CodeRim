import Foundation

// Only the caller-provided temporary project directory is available to the reader.
struct Step: Decodable {
    let action: String
    let file: String?
}

func bits(_ date: Date) -> String { String(date.timeIntervalSinceReferenceDate.bitPattern) }

func state(_ reader: ClaudeTranscriptReader) -> [String: Any] {
    guard let value = reader.activity(sessionID: "test", cwd: "/fixture") else { return ["missing": true] }
    return [
        "turn": value.turn == .finished ? "finished" : "inFlight",
        "since": bits(value.since), "event": value.eventAt.map(bits) as Any,
        "followUps": value.followUps, "tools": value.toolUses.map(bits),
        "currentTools": ClaudeTranscriptReader.toolCalls(value.toolUses, since: value.since),
        "model": value.details?.model as Any, "fast": value.details?.fast as Any,
        "effort": value.details?.effort as Any, "daybreak": value.details?.daybreak as Any
    ]
}

let args = CommandLine.arguments
let scratch = URL(fileURLWithPath: args[1])
let fixtures = URL(fileURLWithPath: args[2])
let fm = FileManager.default
let project = scratch.appendingPathComponent("-fixture")
try fm.createDirectory(at: project, withIntermediateDirectories: true)
let log = project.appendingPathComponent("test.jsonl")
if args[3] == "measure" {
    try fm.copyItem(at: fixtures.appendingPathComponent(args[4]), to: log)
    let rounds = Int(args[5])!
    var reader = ClaudeTranscriptReader(projects: scratch)
    var checksum = 0
    func poll() {
        let value = reader.activity(sessionID: "test", cwd: "/fixture")
        checksum += (value?.followUps ?? 0) + (value?.toolUses.count ?? 0) + (value?.details?.model?.utf8.count ?? 0)
    }
    poll()
    let start = DispatchTime.now().uptimeNanoseconds
    for _ in 0..<rounds {
        if args[6] == "cold" { reader = ClaudeTranscriptReader(projects: scratch) }
        poll()
    }
    print(checksum, (DispatchTime.now().uptimeNanoseconds - start) / UInt64(rounds))
} else {
    var reader = ClaudeTranscriptReader(projects: scratch)
    var rows = [[String: Any]]()
    for step in try JSONDecoder().decode([Step].self, from: Data(contentsOf: fixtures.appendingPathComponent("steps.json"))) {
        switch step.action {
        case "write", "append":
            let data = try Data(contentsOf: fixtures.appendingPathComponent(step.file!))
            if step.action == "write" {
                try data.write(to: log)
            } else {
                let file = try FileHandle(forWritingTo: log)
                _ = try file.seekToEnd()
                try file.write(contentsOf: data)
                try file.close()
            }
            // Make the no-timestamp fallback identical between comparison executables.
            try fm.setAttributes([.modificationDate: Date(timeIntervalSince1970: 1_780_000_000 + Double(rows.count))], ofItemAtPath: log.path)
        case "restart": reader = ClaudeTranscriptReader(projects: scratch)
        case "retire": reader.retainSessions([])
        case "remove": try fm.removeItem(at: log)
        case "poll": break
        default: preconditionFailure("Unknown fixture operation")
        }
        rows.append(state(reader))
    }
    FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: rows, options: [.sortedKeys]))
}
