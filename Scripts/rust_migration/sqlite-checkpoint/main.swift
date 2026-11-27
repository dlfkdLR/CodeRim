import CSQLite
import Foundation

func inspect(_ c: SourceCheckpoint?) -> String {
    guard let c else { return "missing" }
    func field(_ value: String?) -> String { value.map { Data($0.utf8).base64EncodedString() } ?? "-" }
    let words = [field(c.sourcePath), field(c.fileIdentity), String(c.generation), String(c.committedOffset),
                 String(c.isSkippingOversizedLine), String(c.observedSize), String(c.modificationTimeNanoseconds),
                 field(c.contentFingerprint), String(c.hasPendingImport), field(c.sessionID), String(c.inheritsHistory),
                 c.sessionStartedAt.map { String($0.timeIntervalSinceReferenceDate.bitPattern) } ?? "-",
                 c.inheritedHistoryEndOrdinal.map { String($0) } ?? "-", String(c.historyReplayComplete),
                 field(c.model), field(c.projectPath), field(c.parentSessionID), field(c.projectName), String(c.imageAttachmentCount)]
    return words.joined(separator: "|")
}

extension SQLiteDatabase {
    func benchmark(keys: [String], repeats: Int) throws -> String {
        var checksum: Int64 = 0
        let start = DispatchTime.now().uptimeNanoseconds
        for _ in 0..<repeats {
            for key in keys {
                checksum &+= try checkpoint(for: key)?.committedOffset ?? -1
            }
        }
        return "\(DispatchTime.now().uptimeNanoseconds - start) \(checksum)"
    }
}

private actor CollectorCaller {
    func benchmark(database: SQLiteDatabase, keys: [String], repeats: Int) async throws -> String {
        var checksum: Int64 = 0
        let start = DispatchTime.now().uptimeNanoseconds
        for _ in 0..<repeats {
            for key in keys {
                checksum &+= try await database.checkpoint(for: key)?.committedOffset ?? -1
            }
        }
        return "\(DispatchTime.now().uptimeNanoseconds - start) \(checksum)"
    }
}

let args = CommandLine.arguments
let database = try SQLiteDatabase(url: URL(fileURLWithPath: args[1]))
if args[2] == "bench" || args[2] == "collector" {
    let modulus = Int(args[3])!, hit = Int(args[4])!, reps = Int(args[5])!
    let keys = (0..<modulus).map { String(format: "%064llx", Int64($0 % 100 < hit ? $0 : 65536 + $0)) }
    let caller = CollectorCaller()
    let samples = args.count > 6 ? Int(args[6])! : 9
    for _ in 0..<samples {
        if args[2] == "collector" {
            print(try await caller.benchmark(database: database, keys: keys, repeats: reps))
        } else {
            print(try await database.benchmark(keys: keys, repeats: reps))
        }
    }
} else if args[2] == "commands" {
    var external: OpaquePointer?
    guard sqlite3_open_v2(args[1], &external, SQLITE_OPEN_READWRITE, nil) == SQLITE_OK else { fatalError() }
    defer { sqlite3_close(external) }
    while let line = readLine() {
        let input = try JSONDecoder().decode([String].self, from: Data(line.utf8))
        if input[0] == "query" {
            do { print(inspect(try await database.checkpoint(for: input[1]))) }
            catch { print(error.localizedDescription) }
        } else if input[0] == "write" {
            var checkpoint = SourceCheckpoint.fresh(sourcePath: input[1], fileIdentity: "synthetic")
            let text = input[3]
            switch input[2] {
            case "path": checkpoint = .fresh(sourcePath: text, fileIdentity: "synthetic")
            case "identity": checkpoint.fileIdentity = text
            case "fingerprint": checkpoint.contentFingerprint = text
            case "session": checkpoint.sessionID = text
            case "model": checkpoint.model = text
            case "project": checkpoint.projectPath = text
            case "parent": checkpoint.parentSessionID = text
            case "name": checkpoint.projectName = text
            default: fatalError("unknown checkpoint field")
            }
            do {
                _ = try await database.commit(events: [], checkpoint: checkpoint, normalizationState: nil)
                print("saved")
            } catch { print(error.localizedDescription) }
        } else if input[0] == "observe" {
            print("events", try await database.eventCount())
        } else {
            let rc = sqlite3_exec(external, input[1], nil, nil, nil)
            print("SQL", rc)
        }
    }
}
