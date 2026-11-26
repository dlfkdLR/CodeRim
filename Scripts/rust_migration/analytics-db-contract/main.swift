import CodeRimUsageCore
import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
import CSQLite

private func probe(_ path: String, interrupted: Bool) throws {
    var connection: OpaquePointer?
    // A newly copied WAL database can lack its shared-memory sidecar. The product
    // uses a writable connection. query_only prevents database changes during the probe.
    let opened = sqlite3_open_v2(path, &connection, SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX, nil)
    guard opened == SQLITE_OK else { throw NSError(domain: "Open existing fixture", code: Int(opened)) }
    defer { sqlite3_close(connection) }
    let restricted = sqlite3_exec(connection, "PRAGMA query_only=ON", nil, nil, nil)
    guard restricted == SQLITE_OK else { throw NSError(domain: "Set query-only mode", code: Int(restricted)) }
    let query = """
        SELECT occurred_at, model, high_context_pricing, input_tokens, cached_input_tokens,
               output_tokens, cache_write_input_tokens FROM usage_events ORDER BY occurred_at
        """
    var statement: OpaquePointer?
    let prepared = sqlite3_prepare_v2(connection, query, -1, &statement, nil)
    guard prepared == SQLITE_OK else { throw NSError(domain: String(cString: sqlite3_errmsg(connection)), code: Int(prepared)) }
    defer { sqlite3_finalize(statement) }
    if interrupted { sqlite3_progress_handler(connection, 1, { _ in 1 }, nil) }
    var end = Double.greatestFiniteMagnitude
    var callbacks = 0
    let status = cr_read_apple_analytics(UnsafeMutableRawPointer(statement), &end, 1,
                                        PricingContext.highContextInputThreshold, &callbacks) { context, _ in
        context!.assumingMemoryBound(to: Int.self).pointee += 1
    }
    print(status, callbacks)
}
#endif

private func usage(_ value: TokenUsage) -> [Int64?] {
    [value.inputTokens, value.cachedInputTokens, value.outputTokens, value.cacheWriteInputTokens]
}
private func models(_ rows: [ModelUsageSummary]) -> [[String: Any]] {
    rows.map { ["model": $0.modelID as Any, "usage": usage($0.usage), "high": usage($0.highContextUsage),
                "unknown": $0.hasUnknownPricingContext] }
}
private func buckets(_ rows: [UsageBucket]) -> [[String: Any]] {
    rows.map { ["from": String($0.start.timeIntervalSince1970.bitPattern), "until": String($0.end.timeIntervalSince1970.bitPattern),
                "models": models($0.models)] }
}
private func run() async throws {
    let args = CommandLine.arguments
    let source = URL(fileURLWithPath: args[1])
    #if CODERIM_RUST_ACCOUNTING
    if args[2] == "probe" || args[2] == "interrupt" {
        try probe(source.path, interrupted: args[2] == "interrupt")
        return
    }
    #endif
    let db = try SQLiteDatabase(url: source)
    if args[2] == "init" { return }
    var calendar = Calendar(identifier: .gregorian)
    calendar.timeZone = TimeZone(identifier: args[4])!
    let end = Date(timeIntervalSince1970: Double(args[3])!)
    let range = AnalyticsRange(rawValue: args[5])!
    if args[2] == "bench" {
        let loops = Int(args[6])!
        let clock = DispatchTime.now().uptimeNanoseconds
        var sum: Int64 = 0
        for _ in 0..<loops {
            let snapshot = try await db.analyticsSnapshot(range: range, through: end, calendar: calendar)
            sum &+= snapshot.usage.totalTokens
        }
        print(sum, (DispatchTime.now().uptimeNanoseconds - clock) / UInt64(loops))
        return
    }
    let s = try await db.analyticsSnapshot(range: range, through: end, calendar: calendar)
    let results: [String: Any] = [
        "usage": usage(s.usage), "quality": s.quality.rawValue, "models": models(s.models), "buckets": buckets(s.buckets),
        "projects": s.projects.map { ["id": $0.id, "name": $0.name, "usage": usage($0.usage), "models": models($0.models),
                                      "count": $0.sessionCount] as [String: Any] },
        "sessions": s.sessions.map { ["id": $0.id, "usage": usage($0.usage), "models": models($0.models)] as [String: Any] }]
    FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: results, options: [.sortedKeys]))
}
do { try await run() } catch { print("error", error.localizedDescription) }
