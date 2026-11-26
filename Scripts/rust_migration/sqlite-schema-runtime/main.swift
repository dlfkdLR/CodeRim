import CSQLite
import Foundation

final class Probe {
    let mode: String
    let argument: String
    var seen = 0
    var trace: [String] = []
    init(_ mode: String, _ argument: String) {
        self.mode = mode
        self.argument = argument
    }

    func authorize(_ action: Int32, _ first: String, _ second: String) -> Int32 {
        if mode == "barrier", action == SQLITE_TRANSACTION, first == "BEGIN", seen == 0 {
            seen += 1
            guard FileManager.default.createFile(atPath: argument + ".ready", contents: Data()) else { return SQLITE_DENY }
            let deadline = Date().addingTimeInterval(8)
            while !FileManager.default.fileExists(atPath: argument + ".go") {
                guard Date() < deadline else { return SQLITE_DENY }
                Thread.sleep(forTimeInterval: 0.002)
            }
        }
        if mode == "deny-rollback", action == SQLITE_TRANSACTION, first == "ROLLBACK" { return SQLITE_DENY }
        if mode == "deny-write", action == SQLITE_PRAGMA, first == "user_version", second == argument {
            return SQLITE_DENY
        }
        let matches = (mode == "deny-read" && action == SQLITE_PRAGMA && first == "user_version" && second.isEmpty)
            || (mode == "deny-begin" && action == SQLITE_TRANSACTION && first == "BEGIN")
            || (mode == "deny-commit" && action == SQLITE_TRANSACTION && first == "COMMIT")
        if matches {
            seen += 1
            if seen == Int(argument) { return SQLITE_DENY }
        }
        return SQLITE_OK
    }
}

let args = CommandLine.arguments
guard args.count == 4 else { exit(2) }
let probe = Probe(args[2], args[3])
var handle: OpaquePointer?
guard sqlite3_open(args[1], &handle) == SQLITE_OK, let db = handle else { exit(3) }
defer { sqlite3_close(db) }
let context = Unmanaged.passUnretained(probe).toOpaque()
var outcome = "ok"
var nanos: UInt64 = 0
do {
    try SQLiteSchema.configure(db)
    if probe.mode == "seed" {
        try SQLiteSchema.fixtureStep(Int(args[3])!, db)
    } else {
        if probe.mode == "nested" {
            try SQLiteSchema.execute("BEGIN; CREATE TABLE caller_owned(value); INSERT INTO caller_owned VALUES(3)", on: db)
        }
        if probe.mode == "length" {
            sqlite3_limit(db, SQLITE_LIMIT_SQL_LENGTH, Int32(args[3])!)
        }
        if probe.mode == "busy" { sqlite3_busy_timeout(db, 0) }
        if probe.mode == "busy-recheck" {
            sqlite3_busy_handler(db, { context, retries in
                let p = Unmanaged<Probe>.fromOpaque(context!).takeUnretainedValue()
                if retries == 0 {
                    guard FileManager.default.createFile(atPath: p.argument + ".ready", contents: Data()) else { return 0 }
                }
                let deadline = Date().addingTimeInterval(8)
                while !FileManager.default.fileExists(atPath: p.argument + ".go") {
                    guard Date() < deadline else { return 0 }
                    Thread.sleep(forTimeInterval: 0.002)
                }
                return retries < 8 ? 1 : 0
            }, context)
        }
        if probe.mode != "timed" {
            sqlite3_set_authorizer(db, { context, action, a, b, _, _ in
                let p = Unmanaged<Probe>.fromOpaque(context!).takeUnretainedValue()
                return p.authorize(action, a.map(String.init(cString:)) ?? "", b.map(String.init(cString:)) ?? "")
            }, context)
            sqlite3_trace_v2(db, UInt32(SQLITE_TRACE_STMT), { _, context, stmt, _ in
                let p = Unmanaged<Probe>.fromOpaque(context!).takeUnretainedValue()
                guard let sql = sqlite3_sql(OpaquePointer(stmt)) else { return 0 }
                let text = String(cString:sql).split(whereSeparator: { $0.isWhitespace }).joined(separator:" ")
                p.trace.append(text)
                return 0
            }, context)
        }
        let start = DispatchTime.now().uptimeNanoseconds
        let count = probe.mode == "timed" ? Int(probe.argument) ?? 1 : 1
        for _ in 0..<count { try SQLiteSchema.migrate(db) }
        nanos = DispatchTime.now().uptimeNanoseconds - start
    }
} catch { outcome = error.localizedDescription }
let result: [String: Any] = ["outcome":outcome, "autocommit":sqlite3_get_autocommit(db), "trace":probe.trace, "ns":nanos]
print(String(decoding: try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys]), as: UTF8.self))
