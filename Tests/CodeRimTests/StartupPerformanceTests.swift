import Foundation
import XCTest
@testable import CodeRim

final class StartupPerformanceTests: XCTestCase {
    func testRepeatedUnchangedAggregationBenchmark() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let db = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let now = Date()
        let usage = TokenUsage(inputTokens: 1, cachedInputTokens: 0, outputTokens: 0)
        let events = (0..<40_000).map { index in
            UsageEvent(eventKey: "fixture-\(index)", occurredAt: now.addingTimeInterval(-Double(index + 1)),
                sessionID: "fixture", model: nil, projectPath: nil, usage: usage,
                sourcePath: "fixture", sourcePosition: Int64(index))
        }
        _ = try await db.commit(events: events, checkpoint: .fresh(sourcePath: "fixture", fileIdentity: "1:2"),
            normalizationState: UsageNormalizationState(cumulativeHighWaterMark: usage, quality: .exact))
        let expected = try await db.usageSnapshot(now: now, calendar: .current, weekStart: .monday)
        _ = try await db.dataStatistics()
        let clock = ContinuousClock(); let start = clock.now
        for index in 0..<20 {
            let result = try await db.usageSnapshot(now: now.addingTimeInterval(Double(index) * 0.01),
                                                  calendar: .current, weekStart: .monday)
            let bounds = try await db.dataStatistics()
            XCTAssertEqual(result, expected)
            XCTAssertNotNil(bounds.newestRecord)
        }
        let seconds = Self.seconds(start.duration(to: clock.now))
        print("STARTUP_BENCH aggregation_20_reads_seconds=\(seconds)")
        XCTAssertEqual(expected.allTime.totalTokens, 40_000)
    }

    func testLargeChangedPrefixContinuationBenchmark() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let sessions = root.appendingPathComponent("sessions")
        try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
        let file = sessions.appendingPathComponent("source.jsonl")
        let now = Date(); let stamp = now.formatted(.iso8601)
        func token(_ count: Int) -> Data {
            Data(("{\"timestamp\":\"\(stamp)\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":\(count),\"cached_input_tokens\":0,\"output_tokens\":0},\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":0,\"output_tokens\":0}}}}\n").utf8)
        }
        var data = Data("{\"type\":\"session_meta\",\"payload\":{\"id\":\"fixture\"}}\n".utf8)
        data.append(token(100))
        let padding = Data(("{\"type\":\"ignored\",\"padding\":\"" + String(repeating: "x", count: 256 * 1024) + "\"}\n").utf8)
        for _ in 0..<384 { data.append(padding) }
        try data.write(to: file); data.removeAll(keepingCapacity: false)
        let db = try SQLiteDatabase(url: root.appendingPathComponent("usage.sqlite"))
        let collector = CodexUsageCollector(database: db, roots: [sessions], maximumRefreshDuration: .seconds(30))
        var result = try await collector.refresh(now: now.addingTimeInterval(1), weekStart: .monday)
        for _ in 0..<30 where result.hasMoreWork {
            result = try await collector.refresh(now: now.addingTimeInterval(1), weekStart: .monday)
        }
        XCTAssertFalse(result.hasMoreWork)
        XCTAssertEqual(result.snapshot.allTime.totalTokens, 100)
        let writer = try FileHandle(forWritingTo: file)
        try writer.seekToEnd(); try writer.write(contentsOf: token(200)); try writer.close()
        let clock = ContinuousClock(); let start = clock.now; var passes = 1
        result = try await collector.refresh(now: now.addingTimeInterval(1), weekStart: .monday)
        for _ in 0..<30 where result.hasMoreWork {
            try await Task.sleep(for: .milliseconds(100))
            result = try await collector.refresh(now: now.addingTimeInterval(1), weekStart: .monday)
            passes += 1
        }
        let seconds = Self.seconds(start.duration(to: clock.now))
        XCTAssertFalse(result.hasMoreWork)
        XCTAssertEqual(result.snapshot.allTime.totalTokens, 200)
        print("STARTUP_BENCH changed_96MiB_seconds=\(seconds) passes=\(passes)")
    }

    func testInstalledSignatureValidationBenchmark() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["CODERIM_RUN_TRUSTED_EXECUTABLE_BENCHMARK"] == "1",
                          "Opt in to local signed executable validation; no RPC or provider request.")
        let clock = ContinuousClock(); var times: [Double] = []
        for _ in 0..<2 {
            let start = clock.now
            let executable = try TrustedCodexExecutable.resolve()
            XCTAssertTrue(FileManager.default.isExecutableFile(atPath: executable.path))
            times.append(Self.seconds(start.duration(to: clock.now)))
        }
        print("STARTUP_BENCH signature_first_seconds=\(times[0]) second_seconds=\(times[1])")
    }

    private static func seconds(_ duration: Duration) -> Double {
        Double(duration.components.seconds) + Double(duration.components.attoseconds) / 1e18
    }
}
