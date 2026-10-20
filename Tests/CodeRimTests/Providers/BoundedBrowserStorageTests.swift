import Foundation
import XCTest
import CSQLite
@testable import CodeRim

final class BoundedBrowserStorageTests: XCTestCase {
    private let token = "fixture-current-token-12345"
    private func directory() throws -> URL {
        let root = URL(fileURLWithPath: "/private/tmp", isDirectory: true).appendingPathComponent("coderim-storage-test-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }
    private func varint(_ value: UInt64) -> [UInt8] {
        var value = value, result: [UInt8] = []
        repeat { let byte = UInt8(value & 127); value >>= 7; result.append(byte | (value == 0 ? 0 : 128)) } while value != 0
        return result
    }
    private func little(_ value: UInt64, _ size: Int) -> [UInt8] { (0..<size).map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) } }
    private func physical(_ body: [UInt8]) -> Data {
        var crc: UInt32 = ~0
        for byte in [UInt8(1)] + body { crc ^= UInt32(byte); for _ in 0..<8 { crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82f63b78 : 0) } }
        crc = ~crc; crc = ((crc >> 15) | (crc << 17)) &+ 0xa282ead8
        return Data(little(UInt64(crc), 4) + little(UInt64(body.count), 2) + [1] + body)
    }
    private func setup(_ root: URL, deleted: Bool = false, alias: Bool = false) throws {
        try Data("MANIFEST-000001\n".utf8).write(to: root.appendingPathComponent("CURRENT"))
        // log=3, nextFile=4, lastSequence=3.
        try physical([1] + varint(UInt64("leveldb.BytewiseComparator".utf8.count)) + Array("leveldb.BytewiseComparator".utf8) + [2,3,3,4,4,3]).write(to: root.appendingPathComponent("MANIFEST-000001"))
        func slice(_ value: [UInt8]) -> [UInt8] { varint(UInt64(value.count)) + value }
        let key = Array("_https://platform.deepseek.com\0".utf8) + [UInt8(alias ? 0 : 1)] +
            (alias ? "userToken".utf16.flatMap { little(UInt64($0), 2) } : Array("userToken".utf8))
        var items = [UInt8(1)] + slice(Array("VERSION".utf8)) + slice(Array("1".utf8))
        items += [1] + slice(key) + slice([1] + Array(token.utf8))
        if deleted { items += [0] + slice(key) }
        try physical(little(1, 8) + little(deleted ? 3 : 2, 4) + items).write(to: root.appendingPathComponent("000003.log"))
    }
    func testCurrentManifestWALAndDeletion() throws {
        let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
        try setup(root)
        XCTAssertEqual(try BoundedLocalStorageReader.read(directory: root, origin: "https://platform.deepseek.com", keys: ["userToken"])["userToken"], token)
        try setup(root, deleted: true)
        XCTAssertTrue(try BoundedLocalStorageReader.read(directory: root, origin: "https://platform.deepseek.com", keys: ["userToken"]).isEmpty)
    }
    func testMixedEncodingCRCPartialAndSymlinkAreRejected() throws {
        let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
        try setup(root, alias: true)
        XCTAssertThrowsError(try BoundedLocalStorageReader.read(directory: root, origin: "https://platform.deepseek.com", keys: ["userToken"]))
        try setup(root); let log = root.appendingPathComponent("000003.log"); var bytes = try Data(contentsOf: log); bytes[0] ^= 1; try bytes.write(to: log)
        XCTAssertThrowsError(try BoundedLocalStorageReader.read(directory: root, origin: "https://platform.deepseek.com", keys: ["userToken"]))
        try setup(root); bytes = try Data(contentsOf: log); try bytes.dropLast().write(to: log)
        XCTAssertThrowsError(try BoundedLocalStorageReader.read(directory: root, origin: "https://platform.deepseek.com", keys: ["userToken"]))
        try setup(root); let link = root.appendingPathComponent("linked"); try FileManager.default.createSymbolicLink(at: link, withDestinationURL: root)
        XCTAssertThrowsError(try BoundedLocalStorageReader.read(directory: link, origin: "https://platform.deepseek.com", keys: ["userToken"]))
    }
    func testRewriteDuringSnapshotInvalidatesResult() throws {
        let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }; try setup(root)
        XCTAssertThrowsError(try BoundedLocalStorageReader.read(directory: root, origin: "https://platform.deepseek.com", keys: ["userToken"], afterCapture: { try self.setup(root, deleted: true) }))
        for origin in ["http://platform.deepseek.com", "https://platform.deepseek.com:443", "https://platform.deepseek.com.evil", "https://x@platform.deepseek.com"] {
            if origin.hasSuffix(".evil") { continue } // Different HTTPS origin is a valid empty namespace, never an alias.
            XCTAssertThrowsError(try BoundedLocalStorageReader.read(directory: root, origin: origin, keys: ["userToken"]))
        }
    }
    func testTypedConflictsAndEscapedDuplicateKeysAreRejected() throws {
        for raw in ["{\"value\":\"\(token)\",\"value\":\"\(token)\"}", "{\"value\":\"\(token)\",\"v\\u0061lue\":\"\(token)\"}",
                    "{\"value\":\"\(token)\",\"token\":\"other-current-token-12345\"}", "{\"value\":null}", "[]", "NaN", " short ", "bad\nheader-token-1234567890"] {
            XCTAssertThrowsError(try SafeBrowserSessionCredentials.deepSeekToken(raw), raw)
        }
        for raw in [token, "\"\(token)\"", "'\(token)'", "{\"value\":\"\(token)\"}"] {
            XCTAssertEqual(try SafeBrowserSessionCredentials.deepSeekToken(raw), token)
        }
        XCTAssertThrowsError(try SafeBrowserSessionCredentials.miniMaxFields(["access_token": token, "group_id": "1", "user_detail": #"{"group_id":"2"}"#]))
        XCTAssertThrowsError(try SafeBrowserSessionCredentials.miniMaxFields(["user_detail": #"{"group_id":9007199254740993,"groupId":"9007199254740992"}"#]))
        XCTAssertEqual(try SafeBrowserSessionCredentials.miniMaxFields(["user_detail": #"{"group_id":9007199254740993}"#]).group, "9007199254740993")
        XCTAssertThrowsError(try SafeBrowserSessionCredentials.credential(provider: "factory", profileID: "x", profileURL: URL(fileURLWithPath: "/"), origin: "http://app.factory.ai", values: [:]))
    }
    private func db(_ path: URL, _ sql: String) throws -> OpaquePointer {
        var db: OpaquePointer?; XCTAssertEqual(sqlite3_open(path.path, &db), SQLITE_OK)
        let pointer = try XCTUnwrap(db)
        guard sqlite3_exec(pointer, sql, nil, nil, nil) == SQLITE_OK else { sqlite3_close(pointer); throw LocalStorageReadError.invalid }
        return pointer
    }
    func testSafariExactTopAndClientHashTEXTAndBlobEncoding() throws {
        for encoding in ["TEXT", "UTF8", "UTF16LE"] {
            let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
            let salt = Data(0..<8); try salt.write(to: root.appendingPathComponent("salt"))
            let name = SafeSafariLocalStorageReader.directoryName(origin: "https://app.factory.ai", salt: salt)
            let path = root.appendingPathComponent(name + "/" + name + "/LocalStorage")
            try FileManager.default.createDirectory(at: path, withIntermediateDirectories: true)
            let data = encoding == "UTF16LE" ? token.data(using: .utf16LittleEndian)! : Data(token.utf8)
            let value = encoding == "TEXT" ? "'\(token)'" : "X'" + data.map { String(format: "%02x", $0) }.joined() + "'"
            let pointer = try db(path.appendingPathComponent("localstorage.sqlite3"), "CREATE TABLE ItemTable (key TEXT UNIQUE, value BLOB); INSERT INTO ItemTable VALUES ('workos:access-token',\(value));")
            sqlite3_close(pointer)
            let result = try SafeSafariLocalStorageReader.factoryCredentials(root: root)
            XCTAssertEqual(result.count, 1); XCTAssertEqual(result.first?.token, token)
            let partitioned = root.appendingPathComponent("unrelated-top")
            try FileManager.default.moveItem(at: root.appendingPathComponent(name), to: partitioned)
            XCTAssertTrue(try SafeSafariLocalStorageReader.factoryCredentials(root: root).isEmpty)
        }
    }
    func testSafariWALCommittedTokenAndOriginalBytesPreserved() throws {
        let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
        let path = root.appendingPathComponent("localstorage.sqlite3")
        let pointer = try db(path, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE ItemTable(key TEXT UNIQUE,value BLOB); INSERT INTO ItemTable VALUES('workos:access-token','old-token-123456789012345');")
        defer { sqlite3_close(pointer) }
        XCTAssertEqual(sqlite3_exec(pointer, "UPDATE ItemTable SET value='\(token)';", nil, nil, nil), SQLITE_OK)
        let before = try ["", "-wal", "-shm"].map { try Data(contentsOf: URL(fileURLWithPath: path.path + $0)) }
        XCTAssertEqual(try SafeSafariLocalStorageReader.values(directory: root)["workos:access-token"], token)
        let after = try ["", "-wal", "-shm"].map { try Data(contentsOf: URL(fileURLWithPath: path.path + $0)) }
        XCTAssertEqual(before, after)
    }
    func testSafariClosedWALDatabaseWithoutSidecarsIsReadableWithoutSourceWrites() throws {
        let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
        let path = root.appendingPathComponent("localstorage.sqlite3")
        let pointer = try db(path, "PRAGMA journal_mode=WAL; CREATE TABLE ItemTable(key TEXT,value TEXT); INSERT INTO ItemTable VALUES('workos:access-token','\(token)');")
        XCTAssertEqual(sqlite3_wal_checkpoint_v2(pointer, nil, SQLITE_CHECKPOINT_TRUNCATE, nil, nil), SQLITE_OK)
        XCTAssertEqual(sqlite3_close(pointer), SQLITE_OK)
        // Apple SQLite may keep zero-length WAL/SHM after close. Construct the already-checkpointed
        // database-only state explicitly, without opening it again before the product reader.
        for suffix in ["-wal", "-shm"] {
            let sidecar = URL(fileURLWithPath: path.path + suffix)
            if FileManager.default.fileExists(atPath: sidecar.path) {
                if suffix == "-wal" { XCTAssertEqual(try Data(contentsOf: sidecar).count, 0) }
                try FileManager.default.removeItem(at: sidecar)
            }
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: path.path + "-wal"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: path.path + "-shm"))
        let before = try Data(contentsOf: path)
        XCTAssertEqual(try SafeSafariLocalStorageReader.values(directory: root)["workos:access-token"], token)
        XCTAssertEqual(try Data(contentsOf: path), before)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: root.path), ["localstorage.sqlite3"])
    }
    func testSafariMalformedWALCannotFallBackToOldDatabaseToken() throws {
        let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
        let path = root.appendingPathComponent("localstorage.sqlite3")
        let pointer = try db(path, "PRAGMA journal_mode=WAL; CREATE TABLE ItemTable(key TEXT UNIQUE,value TEXT); INSERT INTO ItemTable VALUES('workos:access-token','old-token-123456789012345'); PRAGMA wal_checkpoint(TRUNCATE); UPDATE ItemTable SET value='\(token)';")
        defer { sqlite3_close(pointer) }
        let wal = try Data(contentsOf: URL(fileURLWithPath: path.path + "-wal"))
        XCTAssertFalse(try SafeSafariLocalStorageReader.validatedWAL(wal).isEmpty)
        var corrupt = wal; corrupt[corrupt.count - 1] ^= 1
        XCTAssertThrowsError(try SafeSafariLocalStorageReader.validatedWAL(corrupt))
        XCTAssertThrowsError(try SafeSafariLocalStorageReader.validatedWAL(wal.dropLast()))
        XCTAssertThrowsError(try SafeSafariLocalStorageReader.validatedWAL(Data(repeating: 0, count: 32)))
    }
    func testSafariHostileSQLAndDuplicateRowsFailClosed() throws {
        for sql in [
            "CREATE VIEW ItemTable AS WITH RECURSIVE r(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM r) SELECT 'workos:access-token' key,x value FROM r;",
            "CREATE TABLE ItemTable(key TEXT,value TEXT GENERATED ALWAYS AS ('\(token)') VIRTUAL); INSERT INTO ItemTable(key) VALUES('workos:access-token');",
            "CREATE TABLE ItemTable(key TEXT,value TEXT); INSERT INTO ItemTable VALUES('workos:access-token','\(token)'),('workos:access-token','\(token)');",
            "CREATE VIRTUAL TABLE ItemTable USING fts5(key,value);"
        ] {
            let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
            let pointer = try db(root.appendingPathComponent("localstorage.sqlite3"), sql); sqlite3_close(pointer)
            XCTAssertThrowsError(try SafeSafariLocalStorageReader.values(directory: root), sql)
        }
    }
    func testSafariAbsentOrCaseVariantKeyCannotRecoverHistoricalValue() throws {
        let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
        let pointer = try db(root.appendingPathComponent("localstorage.sqlite3"), "CREATE TABLE ItemTable(key TEXT COLLATE NOCASE,value TEXT); INSERT INTO ItemTable VALUES('Workos:access-token','\(token)');")
        sqlite3_close(pointer)
        XCTAssertTrue(try SafeSafariLocalStorageReader.values(directory: root).isEmpty)
    }
}
