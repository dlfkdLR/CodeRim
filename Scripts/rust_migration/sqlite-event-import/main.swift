import CodeRimUsageCore
import Foundation

private let base = Date(timeIntervalSince1970: 1_793_530_800)
private let path = "synthetic/데이터/é"
private let session = "fixture-session"

private func event(_ i: Int, stage: Int, mode: String) -> UsageEvent {
    let text: [String?] = [nil, "", "ASCII", "é", "e\u{301}", "한글🌙"]
    var model = text[(i + stage) % text.count]
    var key = "key-\(i)"
    var position = Int64(i)
    var parent: String? = stage > 0 ? session : nil
    if mode == "duplicate" && i > 0 {
        if i % 2 == 0 { key = "key-0" } else { position = 0 }
    }
    if mode == "oversize" && i == 0 { model = String(repeating: "x", count: 8 * 1024 * 1024 + 1) }
    if mode == "bad-text" && i == 256 { model = "bad\0value" }
    if mode == "bad-parent" && i == 256 { parent = "bad\0parent" }
    if mode == "wide-error" && i == 1 { model = String(repeating: "w", count: 8 * 1024 * 1024 + 1) + "\0" }
    let usage = TokenUsage(inputTokens: i % 17 == 0 ? -4 : Int64(50 + stage + i % 7),
                           cachedInputTokens: i % 13 == 0 ? 100 : Int64(4 + stage),
                           cacheWriteInputTokens: i % 3 == 0 ? nil : Int64(1 + stage),
                           outputTokens: i % 11 == 0 ? -2 : Int64(i % 5 + stage))
    return UsageEvent(eventKey: key, occurredAt: base.addingTimeInterval(Double(i) / 4 - Double(stage)),
                      sessionID: stage == 0 ? session : (i % 3 == 0 ? nil : "child"),
                      model: model, projectPath: text[i % text.count], usage: usage, sourcePath: path,
                      sourcePosition: position, pricingContext: [nil, .standard, .highContext][i % 3],
                      importParentSessionID: parent)
}

private func run() async throws {
    let args = CommandLine.arguments
    let db = try SQLiteDatabase(url: URL(fileURLWithPath: args[1]))
    if args[2] == "init" { return }
    if args[2] == "read" {
        let cp = try await db.checkpoint(for: path)
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(secondsFromGMT: 0)!
        let snap = try await db.usageSnapshot(now: base.addingTimeInterval(86400), calendar: cal, weekStart: .monday)
        print(cp?.committedOffset ?? -1, snap.allTime.totalTokens, snap.quality.rawValue, snap.historyIncomplete)
        return
    }
    let mode = args[2]
    let count = Int(args[3])!
    let stage = Int(args[4])!
    let merge = args[5] == "claude"
    let events = (0..<count).map { event($0, stage: stage, mode: mode) }
    var checkpoint = SourceCheckpoint.fresh(sourcePath: path, fileIdentity: "fixture", generation: 3)
    checkpoint.sessionID = session
    checkpoint.committedOffset = Int64(count + stage * 1000)
    checkpoint.observedSize = checkpoint.committedOffset
    checkpoint.projectName = "프로젝트"
    checkpoint.imageAttachmentCount = Int64(stage)
    checkpoint.model = mode == "bad-checkpoint" ? "invalid\0checkpoint" : "fixture-model"
    let counter = UsageNormalizationState(cumulativeHighWaterMark: TokenUsage(inputTokens: Int64(1000 + count + stage),
        cachedInputTokens: 5, outputTokens: 7), lastObservedAt: base.addingTimeInterval(Double(stage)), quality: .exact)
    let excluded = mode == "exclude" ? Set(events.enumerated().compactMap { $0.offset % 2 == 0 ? $0.element.eventKey : nil }) : []
    let clock = DispatchTime.now().uptimeNanoseconds
    do {
        let state = try await db.commit(events: events, checkpoint: checkpoint, normalizationState: counter,
            expectedEpoch: mode == "stale" ? -1 : nil, mergesMessageUsage: merge, excludedEventKeys: excluded,
            partialEvidence: [PartialEvidence(source: "fixture", position: Int64(stage), observedAt: base)])
        let elapsed = DispatchTime.now().uptimeNanoseconds - clock
        let usage = state?.cumulativeHighWaterMark
        print("OK", usage?.inputTokens ?? -1, usage?.cachedInputTokens ?? -1, usage?.cacheWriteInputTokens ?? -1,
              usage?.outputTokens ?? -1, state?.lastObservedAt?.timeIntervalSince1970.bitPattern ?? 0,
              state?.quality.rawValue ?? "nil")
        if args.count > 6 && args[6] == "time" { print(elapsed) }
    } catch {
        print("ERROR", error.localizedDescription)
    }
}

try await run()
