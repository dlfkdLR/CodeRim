import CSQLite
import Foundation

let args = CommandLine.arguments
let database = try SQLiteDatabase(url: URL(fileURLWithPath: args[1]))
guard args[2] != "init" else { exit(0) }
var handle: OpaquePointer?
guard sqlite3_open_v2(args[1], &handle, SQLITE_OPEN_READWRITE, nil) == SQLITE_OK,
      let external = handle else { fatalError("fixture connection") }
defer { sqlite3_close(external) }

func run(_ query: String) {
    precondition(sqlite3_exec(external, query, nil, nil, nil) == SQLITE_OK)
}

if args[2] == "time" {
    run("INSERT OR REPLACE INTO app_metadata VALUES('statistics-fixture','0')")
    var elapsed: UInt64 = 0
    var cpu: UInt64 = 0
    var values: [String] = []
    for i in 0..<Int(args[4])! {
        if args[3] == "changed" { run("UPDATE app_metadata SET value='\(i)' WHERE key='statistics-fixture'") }
        let start = DispatchTime.now().uptimeNanoseconds
        let before = clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID)
        let state = try await database.dataStatistics()
        cpu += clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID) - before
        elapsed += DispatchTime.now().uptimeNanoseconds - start
        values = [state.oldestRecord, state.newestRecord].map { $0.map { String($0.timeIntervalSinceReferenceDate.bitPattern) } ?? "nil" }
    }
    print(elapsed, cpu, values.joined(separator: ","))
} else {
    while let text = readLine() {
        let operation = try JSONDecoder().decode(String.self, from: Data(text.utf8))
        if operation != "read" { run(operation) }
        do {
            let state = try await database.dataStatistics()
            let values = [state.oldestRecord, state.newestRecord].map { $0.map { String($0.timeIntervalSinceReferenceDate.bitPattern) } ?? "nil" }
            print(values.joined(separator: ","), state.databaseBytes)
        } catch {
            print(error.localizedDescription)
        }
    }
}
