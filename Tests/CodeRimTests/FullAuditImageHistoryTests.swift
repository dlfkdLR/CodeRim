import CSQLite
import Foundation
import XCTest
@testable import CodeRim

final class FullAuditImageHistoryTests: XCTestCase {
    func testOlderSessionCopyCannotEraseImageMetadata() async throws {
        let fixture = try Fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        try fixture.write(fixture.original, image: true)
        let database = try SQLiteDatabase(url: fixture.database)
        let collector = CodexUsageCollector(database: database, roots: [fixture.sources])
        _ = try await collector.refresh(now: fixture.now, weekStart: .monday)
        try fixture.write(fixture.copy, image: false)
        _ = try await collector.refresh(now: fixture.now, weekStart: .monday)
        _ = try await collector.refresh(now: fixture.now, weekStart: .monday)
        let after = try await database.analyticsSnapshot(range: .today, through: fixture.now, calendar: .current)
        XCTAssertEqual(after.usage.totalTokens, 120)
        XCTAssertEqual(after.sessions.first?.imageAttachmentCount, 1)
        let reopened = try SQLiteDatabase(url: fixture.database)
        let persisted = try await reopened.analyticsSnapshot(range: .today, through: fixture.now, calendar: .current)
        XCTAssertEqual(persisted.sessions.first?.imageAttachmentCount, 1)
    }

    func testSingleSourceRewriteCanRemoveImagesAndClearDoesNotRestoreThem() async throws {
        let fixture = try Fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        try fixture.write(fixture.original, image: true)
        let database = try SQLiteDatabase(url: fixture.database)
        let collector = CodexUsageCollector(database: database, roots: [fixture.sources])
        _ = try await collector.refresh(now: fixture.now, weekStart: .monday)
        try fixture.write(fixture.original, image: false)
        _ = try await collector.refresh(now: fixture.now, weekStart: .monday)
        let rewritten = try await database.analyticsSnapshot(range: .today, through: fixture.now, calendar: .current)
        XCTAssertEqual(rewritten.sessions.first?.imageAttachmentCount, 0)
        _ = try await collector.clearLocalHistory(weekStart: .monday)
        _ = try await collector.refresh(now: fixture.now, weekStart: .monday)
        let cleared = try await database.analyticsSnapshot(range: .today, through: fixture.now, calendar: .current)
        XCTAssertTrue(cleared.sessions.isEmpty)
        XCTAssertEqual(cleared.usage.totalTokens, 0)
    }

    func testVersion16RepairsCountsFromSourceCheckpointsWithoutReimportingTokens() async throws {
        let fixture = try Fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        try fixture.write(fixture.original, image: true)
        let database = try SQLiteDatabase(url: fixture.database)
        let collector = CodexUsageCollector(database: database, roots: [fixture.sources])
        _ = try await collector.refresh(now: fixture.now, weekStart: .monday)
        var connection: OpaquePointer?
        XCTAssertEqual(sqlite3_open(fixture.database.path, &connection), SQLITE_OK)
        defer { sqlite3_close(connection) }
        XCTAssertEqual(sqlite3_exec(connection, "UPDATE session_metadata SET image_attachment_count = 0; DROP INDEX IF EXISTS parsing_state_session_idx; PRAGMA user_version = 16;", nil, nil, nil), SQLITE_OK)
        let migrated = try SQLiteDatabase(url: fixture.database)
        let repaired = try await migrated.analyticsSnapshot(range: .today, through: fixture.now, calendar: .current)
        XCTAssertEqual(repaired.usage.totalTokens, 120)
        XCTAssertEqual(repaired.sessions.first?.imageAttachmentCount, 1)
    }

    private struct Fixture {
        let root: URL
        let sources: URL
        let database: URL
        let now = Date(timeIntervalSince1970: 1_788_230_400)
        var original: URL { sources.appendingPathComponent("full.jsonl") }
        var copy: URL { sources.appendingPathComponent("archive.jsonl") }
        init() throws {
            root = FileManager.default.temporaryDirectory.appendingPathComponent("image-audit-\(UUID())")
            sources = root.appendingPathComponent("sessions")
            database = root.appendingPathComponent("usage.sqlite")
            try FileManager.default.createDirectory(at: sources, withIntermediateDirectories: true)
        }
        func write(_ path: URL, image: Bool) throws {
            let timestamp = now.addingTimeInterval(-60).ISO8601Format()
            var lines = [
                "{\"timestamp\":\"\(timestamp)\",\"type\":\"session_meta\",\"payload\":{\"id\":\"synthetic-session\"}}",
                "{\"timestamp\":\"\(timestamp)\",\"type\":\"event_msg\",\"ordinal\":2,\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":0,\"output_tokens\":20},\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":0,\"output_tokens\":20}}}}"
            ]
            if image {
                lines.append("{\"timestamp\":\"\(timestamp)\",\"type\":\"response_item\",\"ordinal\":3,\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_image\",\"image_url\":\"synthetic-image\"}]}}")
            }
            try Data((lines.joined(separator: "\n") + "\n").utf8).write(to: path)
        }
    }
}
