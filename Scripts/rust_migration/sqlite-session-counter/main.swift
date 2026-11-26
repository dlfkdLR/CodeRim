import CodeRimUsageCore
import CSQLite
import Foundation

private func fields(_ state: UsageNormalizationState?) -> String {
    guard let state else { return "nil" }
    let usage = state.cumulativeHighWaterMark
    return [usage?.inputTokens, usage?.cachedInputTokens, usage?.cacheWriteInputTokens, usage?.outputTokens]
        .map { $0.map(String.init) ?? "-" }.joined(separator: "|") + "|"
        + (state.lastObservedAt.map { String($0.timeIntervalSinceReferenceDate.bitPattern) } ?? "-")
        + "|" + state.quality.rawValue
}

private func commit(_ db: SQLiteDatabase, key: String, n: Int64,
                    expected: UsageNormalizationState?) async throws -> String {
    var point = SourceCheckpoint.fresh(sourcePath: "fixture/\(n)", fileIdentity: "file")
    point.sessionID = key
    point.committedOffset = n
    point.observedSize = n
    let date = Date(timeIntervalSince1970: Double(n))
    let usage = TokenUsage(inputTokens: n, cachedInputTokens: n / 2, cacheWriteInputTokens: 0, outputTokens: n / 3)
    let event = UsageEvent(eventKey: "event-\(n)", occurredAt: date, sessionID: key, model: nil, projectPath: nil,
                           usage: usage, sourcePath: point.sourcePath, sourcePosition: 0)
    let state = UsageNormalizationState(cumulativeHighWaterMark: usage, lastObservedAt: date, quality: .exact)
    return fields(try await db.commit(events: [event], checkpoint: point, normalizationState: state,
                                      expectedNormalizationState: expected))
}

private func run() async throws {
    let args = CommandLine.arguments
    let url = URL(fileURLWithPath: args[1])
    let db = try SQLiteDatabase(url: url)
    if args[2] == "init" { return }
    if args[2] == "bench" {
        let keys = try JSONDecoder().decode([String].self, from: FileHandle.standardInput.readDataToEndOfFile())
        let repeats = Int(args[3])!
        var checksum: Int64 = 0
        let start = DispatchTime.now().uptimeNanoseconds
        let cpu = clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID)
        for _ in 0..<repeats {
            for key in keys {
                let tokens = try await db.normalizationState(for: key).cumulativeHighWaterMark
                checksum &+= (tokens?.inputTokens ?? 1) &+ (tokens?.cacheWriteInputTokens ?? 2)
            }
        }
        print(checksum, DispatchTime.now().uptimeNanoseconds - start, clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID) - cpu)
        return
    }
    var external: OpaquePointer?
    guard sqlite3_open_v2(url.path, &external, SQLITE_OPEN_READWRITE, nil) == SQLITE_OK else { fatalError("fixture") }
    defer { sqlite3_close(external) }
    var saved: UsageNormalizationState?
    while let line = readLine() {
        let op = try JSONDecoder().decode([String].self, from: Data(line.utf8))
        do {
            switch op[0] {
            case "get":
                print(fields(try await db.normalizationState(for: op[1])))
            case "save":
                saved = try await db.normalizationState(for: op[1])
                print(fields(saved))
            case "commit":
                print(try await commit(db, key: op[1], n: Int64(op[2])!, expected: saved))
            case "count":
                print("events", try await db.eventCount())
            case "point":
                print(try await db.checkpoint(for: op[1])?.committedOffset ?? -1)
            case "sql":
                print("SQL", sqlite3_exec(external, op[1], nil, nil, nil))
            default: fatalError("unknown fixture command")
            }
        } catch { print(error.localizedDescription) }
    }
}

do { try await run() } catch { print("open", error.localizedDescription) }
