import CSQLite
import CodeRimUsageCore
import Foundation

let args = CommandLine.arguments
let database = try SQLiteDatabase(url: URL(fileURLWithPath: args[1]))
guard args[2] != "init" else { exit(0) }
var handle: OpaquePointer?
precondition(sqlite3_open_v2(args[1], &handle, SQLITE_OPEN_READWRITE, nil) == SQLITE_OK)
defer { sqlite3_close(handle) }

var calendar = Calendar(identifier: .gregorian)
calendar.timeZone = TimeZone(secondsFromGMT: 0)!
let now = Date(timeIntervalSince1970: 1_704_196_800)
while let line = readLine() {
    let sql = try JSONDecoder().decode(String.self, from: Data(line.utf8))
    if !sql.isEmpty {
        precondition(sqlite3_exec(handle, sql, nil, nil, nil) == SQLITE_OK, String(cString: sqlite3_errmsg(handle)))
    }
    do {
        let snapshot = try await database.usageSnapshot(now: now, calendar: calendar, weekStart: .monday)
        let fields = [snapshot.today, snapshot.week, snapshot.month, snapshot.allTime].map {
            "\($0.inputTokens)/\($0.cachedInputTokens)/\($0.cacheWriteInputTokens.map(String.init) ?? "_")/\($0.outputTokens)"
        }
        print(snapshot.quality.rawValue, snapshot.historyIncomplete,
              snapshot.updatedAt.map { String($0.timeIntervalSince1970.bitPattern) } ?? "nil", fields.joined(separator: ","))
    } catch {
        print("error", error.localizedDescription)
    }
}
