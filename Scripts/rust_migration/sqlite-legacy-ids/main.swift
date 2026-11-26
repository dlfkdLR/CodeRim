import CSQLite
import Foundation

// The verifier adds a same-file extension for constructing historical schema fixtures.
// Normal upgrades call the application's complete configure/migrate functions.
let arguments = Array(CommandLine.arguments.dropFirst())
guard (2...3).contains(arguments.count) else { exit(2) }
var connection: OpaquePointer?
guard sqlite3_open(arguments[0], &connection) == SQLITE_OK, let db = connection else { exit(3) }
defer { sqlite3_close(db) }
do {
    try SQLiteSchema.configure(db)
    if arguments.count == 3, let limit = Int32(arguments[2]) {
        sqlite3_limit(db, SQLITE_LIMIT_LENGTH, limit)
    }
    let started = DispatchTime.now().uptimeNanoseconds
    if arguments[1] == "upgrade" {
        try SQLiteSchema.migrate(db)
    } else if let target = Int(arguments[1]) {
        try SQLiteSchema.fixtureStep(target, db)
    } else { exit(4) }
    print("ok")
    print(DispatchTime.now().uptimeNanoseconds - started)
} catch {
    print(error.localizedDescription)
}
