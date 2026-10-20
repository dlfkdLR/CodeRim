import Darwin
import Foundation
import XCTest
@testable import CodeRim

final class StartupExecutableCacheTests: XCTestCase {
    private func fixture() throws -> (URL, URL) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let file = root.appendingPathComponent("executable")
        try Data("original".utf8).write(to: file)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: file.path)
        return (root, file)
    }

    func testUnchangedVerifiedFileAvoidsRepeatedValidation() throws {
        let (root, file) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let cache = TrustedExecutableValidationCache()
        var calls = 0
        for _ in 0..<3 {
            XCTAssertTrue(cache.validate(file) { _ in calls += 1; return true })
        }
        XCTAssertEqual(calls, 1)
    }

    func testFailedValidationIsNeverReused() throws {
        let (root, file) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let cache = TrustedExecutableValidationCache()
        var calls = 0
        for _ in 0..<2 {
            XCTAssertFalse(cache.validate(file) { _ in calls += 1; return false })
        }
        XCTAssertEqual(calls, 2)
    }

    func testSameSizeRewriteWithRestoredModificationTimeRequiresValidation() throws {
        let (root, file) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let cache = TrustedExecutableValidationCache()
        XCTAssertTrue(cache.validate(file) { _ in true })
        var original = stat()
        XCTAssertEqual(Darwin.lstat(file.path, &original), 0)
        usleep(2_000)
        let writer = try FileHandle(forWritingTo: file)
        try writer.write(contentsOf: Data("modified".utf8)); try writer.close()
        let timestamps = [original.st_atimespec, original.st_mtimespec]
        let restored = timestamps.withUnsafeBufferPointer { pointer in
            Darwin.utimensat(AT_FDCWD, file.path, pointer.baseAddress, 0)
        }
        XCTAssertEqual(restored, 0)
        var rewritten = stat()
        XCTAssertEqual(Darwin.lstat(file.path, &rewritten), 0)
        XCTAssertEqual(rewritten.st_mtimespec.tv_sec, original.st_mtimespec.tv_sec)
        XCTAssertEqual(rewritten.st_mtimespec.tv_nsec, original.st_mtimespec.tv_nsec)
        var called = false
        XCTAssertFalse(cache.validate(file) { _ in called = true; return false })
        XCTAssertTrue(called)
    }

    func testReplacementAndChangeDuringValidationAreRejected() throws {
        let (root, file) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let cache = TrustedExecutableValidationCache()
        XCTAssertTrue(cache.validate(file) { _ in true })
        let replacement = root.appendingPathComponent("replacement")
        try Data("original".utf8).write(to: replacement)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: replacement.path)
        XCTAssertEqual(rename(replacement.path, file.path), 0)
        XCTAssertFalse(cache.validate(file) { _ in false })
        XCTAssertFalse(cache.validate(file) { url in
            try? Data("modified".utf8).write(to: url)
            return true
        })
    }

    func testConcurrentCallersShareOneValidation() throws {
        let (root, file) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let cache = TrustedExecutableValidationCache()
        let calls = LockedCalls()
        DispatchQueue.concurrentPerform(iterations: 8) { _ in
            XCTAssertTrue(cache.validate(file) { _ in
                calls.increment(); usleep(5_000); return true
            })
        }
        XCTAssertEqual(calls.count, 1)
    }

    func testSymlinkSwapDuringValidationCannotCacheAnotherFile() throws {
        let (root, untrusted) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let trusted = root.appendingPathComponent("trusted")
        try Data("trusted".utf8).write(to: trusted)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: trusted.path)
        let alias = root.appendingPathComponent("alias")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: untrusted)
        let cache = TrustedExecutableValidationCache()
        XCTAssertFalse(cache.validate(alias) { url in
            try! FileManager.default.removeItem(at: alias)
            try! FileManager.default.createSymbolicLink(at: alias, withDestinationURL: trusted)
            let accepted = (try! Data(contentsOf: url)) == Data("trusted".utf8)
            try! FileManager.default.removeItem(at: alias)
            try! FileManager.default.createSymbolicLink(at: alias, withDestinationURL: untrusted)
            return accepted
        })
        var called = false
        XCTAssertFalse(cache.validate(alias) { _ in called = true; return false })
        XCTAssertTrue(called)
    }

    func testReplacedSymlinkRequiresValidation() throws {
        let (root, file) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let alias = root.appendingPathComponent("alias")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: file)
        let cache = TrustedExecutableValidationCache()
        XCTAssertTrue(cache.validate(alias) { _ in true })
        try FileManager.default.removeItem(at: alias)
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: file)
        var called = false
        XCTAssertFalse(cache.validate(alias) { _ in called = true; return false })
        XCTAssertTrue(called)
    }

    func testAncestorDirectorySwapAndRestorationCannotSeedCache() throws {
        let (root, file) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let original = root.appendingPathComponent("original")
        let replacement = root.appendingPathComponent("replacement")
        let saved = root.appendingPathComponent("saved")
        for directory in [original, replacement] {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
        }
        let executable = original.appendingPathComponent("executable")
        try FileManager.default.moveItem(at: file, to: executable)
        let trusted = replacement.appendingPathComponent("executable")
        try Data("trusted".utf8).write(to: trusted)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: trusted.path)
        let cache = TrustedExecutableValidationCache()
        XCTAssertFalse(cache.validate(executable) { url in
            XCTAssertEqual(rename(original.path, saved.path), 0)
            XCTAssertEqual(rename(replacement.path, original.path), 0)
            let accepted = (try! Data(contentsOf: url)) == Data("trusted".utf8)
            XCTAssertEqual(rename(original.path, replacement.path), 0)
            XCTAssertEqual(rename(saved.path, original.path), 0)
            return accepted
        })
        var called = false
        XCTAssertFalse(cache.validate(executable) { _ in called = true; return false })
        XCTAssertTrue(called)
    }
}

private final class LockedCalls: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    var count: Int { lock.withLock { value } }
    func increment() { lock.withLock { value += 1 } }
}

final class StartupAggregationCacheTests: XCTestCase {
    private func fixture() throws -> (URL, URL, SQLiteDatabase) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let url = root.appendingPathComponent("usage.sqlite")
        return (root, url, try SQLiteDatabase(url: url))
    }

    private func insert(_ count: Int64, at date: Date, key: String, into db: SQLiteDatabase) async throws {
        let checkpoint = SourceCheckpoint.fresh(sourcePath: key, fileIdentity: "1:2")
        let usage = TokenUsage(inputTokens: count, cachedInputTokens: 0, outputTokens: 0)
        let event = UsageEvent(eventKey: key, occurredAt: date, sessionID: key, model: nil,
                              projectPath: nil, usage: usage, sourcePath: key, sourcePosition: 0)
        _ = try await db.commit(events: [event], checkpoint: checkpoint,
            normalizationState: UsageNormalizationState(cumulativeHighWaterMark: usage, quality: .exact))
    }

    func testOwnAndExternalConnectionWritesInvalidateAggregatesAndBounds() async throws {
        let (root, url, db) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let now = Date()
        try await insert(100, at: now.addingTimeInterval(-10), key: "first", into: db)
        let first = try await db.usageSnapshot(now: now, calendar: .current, weekStart: .monday)
        XCTAssertEqual(first.allTime.totalTokens, 100)
        _ = try await db.dataStatistics()
        try await insert(200, at: now.addingTimeInterval(-5), key: "second", into: db)
        let second = try await db.usageSnapshot(now: now, calendar: .current, weekStart: .monday)
        XCTAssertEqual(second.allTime.totalTokens, 300)
        _ = try await db.dataStatistics()
        let external = try SQLiteDatabase(url: url)
        try await insert(300, at: now, key: "external", into: external)
        let third = try await db.usageSnapshot(now: now, calendar: .current, weekStart: .monday)
        let bounds = try await db.dataStatistics()
        XCTAssertEqual(third.allTime.totalTokens, 600)
        XCTAssertEqual(try XCTUnwrap(bounds.newestRecord).timeIntervalSince1970,
                       now.timeIntervalSince1970, accuracy: 0.000001)
    }

    func testFutureEventBecomesEligibleWithoutDatabaseWrite() async throws {
        let (root, _, db) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let now = Date()
        try await insert(100, at: now, key: "present", into: db)
        try await insert(200, at: now.addingTimeInterval(0.25), key: "future", into: db)
        let first = try await db.usageSnapshot(now: now, calendar: .current, weekStart: .monday)
        let before = try await db.usageSnapshot(now: now.addingTimeInterval(0.1), calendar: .current, weekStart: .monday)
        let after = try await db.usageSnapshot(now: now.addingTimeInterval(0.25), calendar: .current, weekStart: .monday)
        XCTAssertEqual(first.allTime.totalTokens, 100)
        XCTAssertEqual(before, first)
        XCTAssertEqual(after.allTime.totalTokens, 300)
    }

    func testMidnightTimezoneWeekStartAndClockRollbackRecomputePeriods() async throws {
        let (root, _, db) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let midnight = calendar.date(from: DateComponents(year: 2026, month: 9, day: 28))!
        try await insert(100, at: midnight.addingTimeInterval(-60), key: "sunday", into: db)
        let before = try await db.usageSnapshot(now: midnight.addingTimeInterval(-1), calendar: calendar, weekStart: .monday)
        let after = try await db.usageSnapshot(now: midnight, calendar: calendar, weekStart: .monday)
        let sundayWeek = try await db.usageSnapshot(now: midnight, calendar: calendar, weekStart: .sunday)
        let rollback = try await db.usageSnapshot(now: midnight.addingTimeInterval(-120), calendar: calendar, weekStart: .sunday)
        XCTAssertEqual(before.today.totalTokens, 100)
        XCTAssertEqual(after.today.totalTokens, 0)
        XCTAssertEqual(after.week.totalTokens, 0)
        XCTAssertEqual(sundayWeek.week.totalTokens, 100)
        XCTAssertEqual(rollback.allTime.totalTokens, 0)
        calendar.timeZone = TimeZone(secondsFromGMT: -3600)!
        let otherZone = try await db.usageSnapshot(now: midnight, calendar: calendar, weekStart: .monday)
        XCTAssertEqual(otherZone.today.totalTokens, 100)
    }
}
