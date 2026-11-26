import CodeRimUsageCore
import Foundation

let rows = try JSONDecoder().decode([Data].self, from: Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1])))
switch CommandLine.arguments[2] {
case "mask":
    FileHandle.standardOutput.write(Data(rows.map { fixtureSelects($0) ? 1 : 0 }))
case "bench":
    let rounds = max(1, 16*1024*1024 / max(1, rows.reduce(0) { $0 + $1.count }))
    var selected = 0
    let start = DispatchTime.now().uptimeNanoseconds
    for _ in 0..<rounds {
        for row in rows where fixtureSelects(row) { selected &+= 1 }
    }
    print(selected, (DispatchTime.now().uptimeNanoseconds-start)/UInt64(rounds))
default:
    let url = URL(fileURLWithPath: "/private/tmp/coderim-synthetic-import.jsonl")
    var session = SourceImportSession(checkpoint: .fresh(sourcePath: url.path, fileIdentity: "fixture"),
        state: .empty, provider: .codex, sourceURL: url, importCutoff: nil,
        projectKey: Data(repeating: 4, count: 32), loadState: { _ in .empty })
    var offset: Int64 = 0
    for row in rows {
        try await session.consume(row, position: offset)
        offset += Int64(row.count+1)
    }
    let checkpoint = session.checkpoint
    let events: [[String: Any]] = session.pendingEvents.map { event in
        ["key":event.eventKey, "session":event.sessionID as Any, "position":event.sourcePosition,
         "time":String(event.occurredAt.timeIntervalSinceReferenceDate.bitPattern), "model":event.model as Any,
         "project":event.projectPath as Any, "pricing":event.pricingContext?.rawValue as Any,
         "usage":[event.usage.inputTokens,event.usage.cachedInputTokens,event.usage.cacheWriteInputTokens,event.usage.outputTokens]]
    }
    let result: [String: Any] = ["events":events, "evidencePositions":session.pendingEvidence.map(\.position),
        "excluded":session.excludedEventKeys.sorted(), "images":checkpoint.imageAttachmentCount,
        "inheritsHistory":checkpoint.inheritsHistory, "replayComplete":checkpoint.historyReplayComplete,
        "model":checkpoint.model as Any, "project":checkpoint.projectPath as Any, "session":checkpoint.sessionID as Any]
    FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: result, options: .sortedKeys))
}
