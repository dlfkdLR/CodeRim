import Foundation
import CryptoKit
import CSQLite
import Darwin

/// Modern WebKit WebsiteData uses SHA256(origin UTF8 || eight-byte salt), base64url,
/// first top origin then client origin. Selecting the same exact origin twice excludes partitions.
/// Source: WebKit NetworkStorageManager.cpp originDirectoryPath / StorageUtilities.cpp.
enum SafeSafariLocalStorageReader {
    static func factoryCredentials(root: URL) throws -> [SafeBrowserSessionCredential] {
        try BoundedLocalStorageReader.withFiles(directory: root, required: ["salt"], maximum: 8) { files in
            guard let salt = files["salt"], salt.count == 8 else { throw LocalStorageReadError.unsupported }
            var credentials: [SafeBrowserSessionCredential] = []
            for origin in SafeBrowserSessionCredentials.origins("factory") {
                let name = directoryName(origin: origin, salt: salt)
                let directory = root.appendingPathComponent(name).appendingPathComponent(name).appendingPathComponent("LocalStorage")
                do {
                    let values = try values(directory: directory)
                    if let credential = try SafeBrowserSessionCredentials.credential(provider: "factory", profileID: "safari:Default",
                        profileURL: directory, origin: origin, values: values) { credentials.append(credential) }
                } catch LocalStorageReadError.missing { continue }
            }
            return credentials
        }
    }
    static func directoryName(origin: String, salt: Data) -> String {
        Data(SHA256.hash(data: Data(origin.utf8) + salt)).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }
    static func values(directory: URL) throws -> [String: String] {
        let name = "localstorage.sqlite3"
        return try BoundedLocalStorageReader.withFiles(directory: directory, required: [name],
            optional: [name + "-wal", name + "-shm", name + "-journal"], maximum: 32 * 1024 * 1024) { files in
            guard files[name + "-journal"] == nil else { throw LocalStorageReadError.unsupported }
            let wal = try files[name + "-wal"].map { try validatedWAL($0, sharedMemory: files[name + "-shm"]) }
            // SQLite touches only an owner-private temporary copy; source DB/WAL/SHM are never opened by SQLite.
            var template = Array((FileManager.default.temporaryDirectory.appendingPathComponent("coderim-safari-XXXXXX").path + "\0").utf8).map { CChar(bitPattern: $0) }
            guard let path = mkdtemp(&template) else { throw LocalStorageReadError.invalid }
            let temporary = URL(fileURLWithPath: String(cString: path), isDirectory: true)
            defer { try? FileManager.default.removeItem(at: temporary) }
            for file in [name, name + "-wal"] {
                if let data = file.hasSuffix("-wal") ? wal : files[file] {
                    let url = temporary.appendingPathComponent(file)
                    try data.write(to: url, options: [.withoutOverwriting])
                    guard chmod(url.path, S_IRUSR | S_IWUSR) == 0 else { throw LocalStorageReadError.invalid }
                }
            }
            return try query(temporary.appendingPathComponent(name))
        }
    }
    /// SQLite otherwise silently ignores a malformed WAL and may return an older database token.
    /// Verify all current-generation frames, then expose only the last complete committed prefix.
    static func validatedWAL(_ input: Data, sharedMemory: Data? = nil) throws -> Data {
        if input.isEmpty { return input }
        let bytes = [UInt8](input)
        guard bytes.count >= 32 else { throw LocalStorageReadError.invalid }
        func word(_ at: Int, little: Bool = false) -> UInt32 {
            (0..<4).reduce(UInt32(0)) { $0 | (UInt32(bytes[at + $1]) << (little ? $1 * 8 : (3 - $1) * 8)) }
        }
        let magic = word(0), page = Int(word(8))
        guard [UInt32(0x377f0682), 0x377f0683].contains(magic), word(4) == 3007000,
              (512...65536).contains(page), page.nonzeroBitCount == 1,
              (bytes.count - 32) % (page + 24) == 0 else { throw LocalStorageReadError.invalid }
        var first: UInt32 = 0, second: UInt32 = 0
        func checksum(_ range: Range<Int>) {
            for at in stride(from: range.lowerBound, to: range.upperBound, by: 8) {
                first = first &+ word(at, little: magic == 0x377f0682) &+ second
                second = second &+ word(at + 4, little: magic == 0x377f0682) &+ first
            }
        }
        checksum(0..<24)
        guard first == word(24), second == word(28) else { throw LocalStorageReadError.invalid }
        let salt1 = word(16), salt2 = word(20), started = ContinuousClock.now
        var expectedFrames: Int?, expectedFirst: UInt32?, expectedSecond: UInt32?
        if let sharedMemory, !sharedMemory.isEmpty {
            let shared = [UInt8](sharedMemory)
            guard shared.count >= 96, shared[0..<48].elementsEqual(shared[48..<96]), shared[12] == 1 else { throw LocalStorageReadError.changed }
            func native(_ at: Int) -> UInt32 { (0..<4).reduce(UInt32(0)) { $0 | UInt32(shared[at + $1]) << ($1 * 8) } }
            var a: UInt32 = 0, b: UInt32 = 0
            for at in stride(from: 0, to: 40, by: 8) { a = a &+ native(at) &+ b; b = b &+ native(at + 4) &+ a }
            let encodedPage = Int(shared[14]) | Int(shared[15]) << 8
            guard native(0) == 3007000, a == native(40), b == native(44), shared[13] == UInt8(magic & 1),
                  (encodedPage == 1 ? 65536 : encodedPage) == page,
                  shared[32..<40].elementsEqual(bytes[16..<24]) else { throw LocalStorageReadError.changed }
            expectedFrames = Int(native(16)); expectedFirst = native(24); expectedSecond = native(28)
            guard expectedFrames! <= (bytes.count - 32) / (page + 24) else { throw LocalStorageReadError.changed }
        }
        let stop = expectedFrames.map { 32 + $0 * (page + 24) } ?? bytes.count
        var committed = 32
        for at in stride(from: 32, to: stop, by: page + 24) {
            try Task.checkCancellation()
            guard started.duration(to: .now) < .seconds(1) else { throw LocalStorageReadError.limit }
            let current = word(at + 8) == salt1 && word(at + 12) == salt2
            guard current, word(at) > 0, word(at) < UInt32.max else { throw LocalStorageReadError.invalid }
            checksum(at..<(at + 8)); checksum((at + 24)..<(at + 24 + page))
            guard first == word(at + 16), second == word(at + 20) else { throw LocalStorageReadError.invalid }
            if word(at + 4) != 0 { committed = at + 24 + page }
        }
        if let expectedFrames, expectedFrames > 0 {
            guard committed == stop, first == expectedFirst, second == expectedSecond else { throw LocalStorageReadError.changed }
        }
        return committed == 32 ? Data() : Data(bytes[..<committed])
    }
    private final class Budget {
        let start = ContinuousClock.now
        var calls = 0
        func interrupted() -> Bool { calls += 1; return calls > 1000 || start.duration(to: .now) >= .seconds(1) || Task.isCancelled }
    }
    private static func query(_ file: URL) throws -> [String: String] {
        var db: OpaquePointer?
        guard sqlite3_open_v2(file.path, &db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_NOMUTEX, nil) == SQLITE_OK, let db else {
            if let db { sqlite3_close(db) }; throw LocalStorageReadError.invalid
        }
        defer { sqlite3_close(db) }
        // READWRITE initializes missing WAL/SHM only inside our 0700 temporary directory.
        // SQLite READONLY cannot open a closed WAL-mode DB on macOS when both sidecars are absent.
        guard sqlite3_exec(db, "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;", nil, nil, nil) == SQLITE_OK else { throw LocalStorageReadError.invalid }
        sqlite3_set_authorizer(db, { _, action, name, _, _, _ in
            if action == SQLITE_SELECT || action == SQLITE_READ { return SQLITE_OK }
            if action == SQLITE_PRAGMA, let name, ["table_list", "table_xinfo"].contains(String(cString: name)) { return SQLITE_OK }
            return SQLITE_DENY
        }, nil)
        defer { sqlite3_set_authorizer(db, nil, nil) }
        sqlite3_busy_timeout(db, 0)
        sqlite3_limit(db, SQLITE_LIMIT_LENGTH, 1024 * 1024)
        sqlite3_limit(db, SQLITE_LIMIT_SQL_LENGTH, 65536)
        let budget = Budget()
        sqlite3_progress_handler(db, 1000, { pointer in
            guard let pointer else { return 1 }
            return Unmanaged<Budget>.fromOpaque(pointer).takeUnretainedValue().interrupted() ? 1 : 0
        }, Unmanaged.passUnretained(budget).toOpaque())
        defer { sqlite3_progress_handler(db, 0, nil, nil) }
        func rows(_ sql: String, _ body: (OpaquePointer) throws -> Void) throws {
            try Task.checkCancellation()
            var statement: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK, let statement else { throw LocalStorageReadError.invalid }
            defer { sqlite3_finalize(statement) }
            var count = 0
            while true {
                try Task.checkCancellation()
                let status = sqlite3_step(statement)
                if status == SQLITE_DONE { break }
                guard status == SQLITE_ROW else { throw status == SQLITE_INTERRUPT ? LocalStorageReadError.limit : LocalStorageReadError.invalid }
                count += 1; guard count <= 256 else { throw LocalStorageReadError.limit }; try body(statement)
            }
            try Task.checkCancellation()
        }
        func text(_ statement: OpaquePointer, _ column: Int32) throws -> String {
            let size = sqlite3_column_bytes(statement, column)
            guard size >= 0, size <= 65536, let pointer = sqlite3_column_text(statement, column),
                  let text = String(bytes: UnsafeBufferPointer(start: pointer, count: Int(size)), encoding: .utf8) else { throw LocalStorageReadError.invalid }
            return text
        }
        var table: String?
        try rows("PRAGMA main.table_list") { row in
            let name = try text(row, 1)
            if name == "ItemTable" || name == "localstorage" {
                guard try text(row, 0) == "main", try text(row, 2) == "table", table == nil else { throw LocalStorageReadError.unsupported }
                table = name
            }
        }
        guard let table else { throw LocalStorageReadError.unsupported }
        var columns = Set<String>()
        try rows("PRAGMA main.table_xinfo(\"\(table)\")") { row in
            let name = try text(row, 1)
            if name == "key" || name == "value" {
                guard sqlite3_column_int(row, 6) == 0, columns.insert(name).inserted else { throw LocalStorageReadError.unsupported }
            }
        }
        guard columns == ["key", "value"] else { throw LocalStorageReadError.unsupported }
        var result: [String: String] = [:]
        try rows("SELECT key,value FROM \"\(table)\" WHERE key COLLATE BINARY IN ('workos:access-token','workos:refresh-token') LIMIT 3") { row in
            guard sqlite3_column_type(row, 0) == SQLITE_TEXT else { throw LocalStorageReadError.invalid }
            let key = try text(row, 0)
            guard result[key] == nil else { throw LocalStorageReadError.invalid }
            let size = sqlite3_column_bytes(row, 1), type = sqlite3_column_type(row, 1)
            guard size > 0, size <= 65536, type == SQLITE_TEXT || type == SQLITE_BLOB else { throw LocalStorageReadError.invalid }
            if type == SQLITE_TEXT { result[key] = try text(row, 1) }
            else {
                guard let pointer = sqlite3_column_blob(row, 1) else { throw LocalStorageReadError.invalid }
                let data = Data(bytes: pointer, count: Int(size))
                if data.contains(0) {
                    guard data.count % 2 == 0, let value = String(data: data, encoding: .utf16LittleEndian), !value.contains("\0") else { throw LocalStorageReadError.invalid }
                    result[key] = value
                } else {
                    guard let value = String(data: data, encoding: .utf8) else { throw LocalStorageReadError.invalid }; result[key] = value
                }
            }
        }
        guard result.values.reduce(0, { $0 + $1.utf8.count }) <= 65536 else { throw LocalStorageReadError.limit }
        try Task.checkCancellation(); return result
    }
}
