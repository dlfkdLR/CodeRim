import CSQLite
import CodeRimUsageCore
import Foundation

let args = CommandLine.arguments
let url = URL(fileURLWithPath: args[1])
let database = try SQLiteDatabase(url: url)
let mode = args[2]
if mode == "init" { exit(0) }
func report(_ db: SQLiteDatabase) async throws -> String {
    let epoch = try await db.dataEpoch()
    let count = try await db.eventCount()
    let cutoff = try await db.importCutoff()
    return "\(epoch)|\(count)|" + (cutoff.map { String($0.timeIntervalSince1970.bitPattern) } ?? "none")
}
func append(_ key: String, _ seconds: Double, _ position: Int64, _ epoch: Int64, claude: Bool) async throws {
    let time = Date(timeIntervalSince1970: seconds)
    let event = UsageEvent(eventKey: key, occurredAt: time, sessionID: "session", model: "synthetic", projectPath: "project",
                           usage: TokenUsage(inputTokens: 7, cachedInputTokens: 2, outputTokens: 3), sourcePath: "owned-fixture",
                           sourcePosition: position)
    var checkpoint = SourceCheckpoint.fresh(sourcePath: "owned-fixture", fileIdentity: "fixture")
    checkpoint.sessionID = "session"
    checkpoint.committedOffset = position + 1
    try await database.commit(events: [event], checkpoint: checkpoint, normalizationState: nil, expectedEpoch: epoch, mergesMessageUsage: claude)
}

var reader: OpaquePointer?
defer { sqlite3_close(reader) }
if args.contains("reader") {
    precondition(sqlite3_open(args[1], &reader) == SQLITE_OK)
    precondition(sqlite3_exec(reader, "BEGIN; SELECT * FROM usage_events", nil, nil, nil) == SQLITE_OK)
}
if args.contains("permissions") {
    for path in [url.path, url.path + "-wal", url.path + "-shm"] where FileManager.default.fileExists(atPath:path) {
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath:path)
    }
}
let start = DispatchTime.now().uptimeNanoseconds
var result = ""
do {
    switch mode {
    case "read": result = "read"
    case "rebuild": try await database.rebuildStatistics(); result = "rebuilt"
    case "clear", "exclude":
        let date = Date(timeIntervalSince1970: Double(bitPattern: UInt64(args[3])!))
        let status = try await database.clearLocalHistory(at: date, preservesMessageExclusions: mode == "exclude")
        result = status == .complete ? "complete" : "deferred"
    case "append":
        try await append(args[3], Double(args[4])!, Int64(args[5])!, Int64(args[6])!, claude: args[7] == "claude")
        result = "appended"
    default: exit(2)
    }
} catch { result = error.localizedDescription }
let elapsed = DispatchTime.now().uptimeNanoseconds - start
let state = try await report(database)
let permissions = [url.path, url.path + "-wal", url.path + "-shm"].map {
    (try? FileManager.default.attributesOfItem(atPath:$0)[.posixPermissions] as? NSNumber)?.intValue ?? -1
}
let output: [String:Any] = ["result":result,"state":state,"permissions":permissions,"elapsedNs":elapsed]
print(String(decoding: try JSONSerialization.data(withJSONObject: output, options:[.sortedKeys]), as: UTF8.self))
