import CodeRimUsageCore
import Darwin
import Foundation

private struct Generator {
    var state: UInt64
    mutating func next(_ n: Int) -> Int {
        state = state &* 6364136223846793005 &+ 1442695040888963407
        return Int((state >> 32) % UInt64(n))
    }
}

private func row(_ name: String?, _ input: Int64, _ cached: Int64, _ output: Int64) -> ModelUsageSummary {
    ModelUsageSummary(modelID: name, usage: TokenUsage(inputTokens: input, cachedInputTokens: cached,
                                                        cacheWriteInputTokens: name == nil ? nil : 0, outputTokens: output))
}

private func snapshot(_ models: [ModelUsageSummary], _ groups: [[ModelUsageSummary]],
                      usage: TokenUsage? = nil, range: AnalyticsRange = .thirtyDays) -> AnalyticsSnapshot {
    let start = Date(timeIntervalSince1970: 1_740_000_000.125)
    let buckets = groups.enumerated().map { index, models in
        let t = start.addingTimeInterval(Double(index) * 3600)
        return UsageBucket(start: t, end: t.addingTimeInterval(3600), models: models)
    }
    let end = buckets.last?.end ?? start
    return AnalyticsSnapshot(range: range, interval: DateInterval(start: start, end: end), through: end,
                             usage: usage ?? models.reduce(.zero) { $0.adding($1.usage) }, quality: .exact,
                             buckets: buckets, models: models, projects: [], sessions: [])
}

private func generated(_ seed: Int, days: Int, count: Int) -> AnalyticsSnapshot {
    var rng = Generator(state: UInt64(seed + 991))
    let models = (0..<count).map { index in
        row("model-\(index)", Int64(rng.next(10000)), 5, Int64(rng.next(1000)))
    }
    let groups = (0..<days).map { _ in
        models.compactMap { m -> ModelUsageSummary? in
            if rng.next(6) == 0 { return nil }
            return row(m.modelID, Int64(rng.next(500)), Int64(rng.next(40)), Int64(rng.next(120)))
        }
    }
    return snapshot(models, groups, range: AnalyticsRange.allCases[seed % 4])
}

private struct Series: Encodable {
    let id, title: String
    let count: Int64
    init(_ x: SettingsUsageSeries) { (id, title, count) = (x.id, x.title, x.tokens) }
}
private struct Point: Encodable {
    let id, title, seriesID: String
    let bits: UInt64
    let count: Int64
    init(_ x: SettingsUsagePoint) {
        (id, title, seriesID, bits, count) = (x.id, x.title, x.seriesID, x.date.timeIntervalSinceReferenceDate.bitPattern, x.tokens)
    }
}
private struct Record: Encodable {
    let number: Int
    let grouping: String
    let cached: Bool
    let series: [Series]
    let points: [Point]
    let selected: [[Series]]
}
private func validate() throws {
    let special = [row(nil, 8, 3, 5), row("unknown-model", 17, 2, 0), row("Café", 9, 3, 3),
                   row("Cafe\u{301}", 10, 0, 2), row("", 12, 15, 0), row("Other models", 6, 0, 6),
                   row("other-models", 6, 1, 6), row("👨‍👩‍👧", 4, 1, 8), row("가", 12, 0, 0)]
    let extremes = [row("m", .max, 0, -5), row("n", 3, 0, 4), row("p", -19, 0, 3),
                    row("q", 8, 0, 8), row("r", 9, 0, 5),row("s", .min, 0, 9),row("t", -1, 0, 0)]
    var fixtures = [snapshot([], []),snapshot([], [[],special]), snapshot([special[0]], [[]]),
                    snapshot(special, [special.reversed(), [], [row("not-in-legend", 5, 10, 2)],special]),
                    snapshot(extremes, [extremes,extremes.reversed(),[row("x",.min,0,0),row("y",-1,0,.max)]]),
                    snapshot(special, [special], usage: TokenUsage(inputTokens: 985, cachedInputTokens: 126, outputTokens: 0))]
    fixtures += (0..<45).map { generated($0, days: $0 % 9, count: $0 % 14) }
    var records: [Record] = []
    for (i, s) in fixtures.enumerated() {
        for grouping in SettingsUsageGrouping.allCases {
            for cached in [false, true] {
                let data = UsageAnalyticsPresentation(snapshot: s, grouping: grouping, showsCachedInput: cached)
                let dates: [Date?] = [nil,s.interval.start.addingTimeInterval(-1),s.interval.start,
                                      s.interval.start.addingTimeInterval(3600),s.interval.end,s.interval.end.addingTimeInterval(1)]
                let selections = dates.map { data.series(for: data.bucket(at: $0)).map(Series.init) }
                records.append(Record(number:i,grouping:grouping.rawValue,cached:cached,series:data.series.map(Series.init),
                                      points:data.points.map(Point.init),selected:selections))
            }
        }
    }
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    FileHandle.standardOutput.write(try encoder.encode(records))
}

private func cpu() -> Double {
    var u = rusage()
    getrusage(RUSAGE_SELF, &u)
    return Double(u.ru_utime.tv_sec + u.ru_stime.tv_sec) + Double(u.ru_utime.tv_usec + u.ru_stime.tv_usec) / 1e6
}
@inline(never)
private func checksum(_ s: AnalyticsSnapshot, _ grouping: SettingsUsageGrouping, _ cached: Bool) -> UInt64 {
    let data = UsageAnalyticsPresentation(snapshot: s, grouping: grouping, showsCachedInput: cached)
    var n: UInt64 = 0
    for a in data.series + data.series(for: data.bucket(at: s.interval.start)) {
        n = n &* 31 &+ UInt64(bitPattern: a.tokens) &+ UInt64(a.id.utf8.count)
    }
    for p in data.points {
        n = n &* 31 &+ UInt64(bitPattern: p.tokens) &+ UInt64(p.title.utf8.count)
    }
    return n
}
if CommandLine.arguments.count == 5 {
    let grouping = SettingsUsageGrouping(rawValue: CommandLine.arguments[1])!
    let count = Int(CommandLine.arguments[2])!, days = Int(CommandLine.arguments[3])!, loops = Int(CommandLine.arguments[4])!
    let snaps = (0..<5).map { generated($0, days: days, count: count) }
    var sum: UInt64 = 0
    let start = DispatchTime.now().uptimeNanoseconds, before = cpu()
    for i in 0..<loops { sum &+= checksum(snaps[i % snaps.count], grouping, i % 2 == 0) }
    let wall = Double(DispatchTime.now().uptimeNanoseconds - start) / Double(loops) / 1000
    let user = (cpu()-before)*1e6/Double(loops)
    var usage = rusage()
    getrusage(RUSAGE_SELF, &usage)
    print("{\"checksum\":\"\(sum)\",\"wall_us\":\(wall),\"cpu_us\":\(user),\"peak_rss_bytes\":\(usage.ru_maxrss)}")
} else { try validate() }
