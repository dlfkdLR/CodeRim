import Foundation

private struct Series: Encodable {
    let id, title: String
    let total: Int
    let values: [Int]
}
private struct Capture: Encodable {
    let days: [UInt64]
    let series: [Series]
    let total: Int
    init(_ value: ActivityChart) {
        days = value.days.map { $0.timeIntervalSinceReferenceDate.bitPattern }
        series = value.series.map { Series(id: $0.id, title: $0.title, total: $0.total, values: $0.counts) }
        total = value.total
    }
}
private typealias Counts = [Date: [String: Int]]
private func fixture(_ names: Int, _ days: Int, _ seed: Int) -> ([Date], Counts) {
    let dates = (0..<days).map { Date(timeIntervalSinceReferenceDate: 501_000_000.625 + Double($0) * 86400) }
    var counts: Counts = [:]
    for (day, date) in dates.enumerated() {
        for name in 0..<names where (day + name + seed) % 3 != 1 {
            counts[date, default: [:]]["service-\(name)"] = (day * 19 + name * 7 + seed) % 29
        }
    }
    return (dates, counts)
}
private func validate(_ directory: URL) async throws -> [Capture] {
    var result: [Capture] = []
    let a = Date(timeIntervalSinceReferenceDate: 5), b = a.addingTimeInterval(2), c = a.addingTimeInterval(3)
    let special: Counts = [a: ["other": 9, "é": 4, "α": -2, "": 1], b: ["e\u{301}": 5, "🌀": 3, "가": 1]]
    let extreme: Counts = [a: ["largest": .max], b: ["zero": 1], c: ["zero": -1]]
    for spec in [([a,b,a,c],special), ([c],special), ([],special), ([a,b],extreme), ([a,b],[:])] {
        for limit in [0,1,2,5,20,Int.max] {
            result.append(Capture(.build(days: spec.0, counts: spec.1, limit: limit, otherTitle: "기타")))
        }
    }
    for i in 0..<40 {
        let (days, counts) = fixture(i % 16, i % 9, i)
        for limit in [0,1,5,10] { result.append(Capture(.build(days: days, counts: counts, limit: limit))) }
    }
    // Actual scanners read only these two temporary directories.
    let claude = directory.appendingPathComponent("claude"), codex = directory.appendingPathComponent("codex")
    for dir in [claude,codex] { try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true) }
    let fixed = Date(timeIntervalSince1970: 1_793_275_200)
    let text = #"{"timestamp":"2026-10-29T01:00:00Z","type":"turn_context","payload":{"model":"fixture-model"}}"# + "\n"
        + #"{"timestamp":"2026-10-29T01:00:02Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"fixture"}]}}"# + "\n"
        + #"{"timestamp":"2026-10-29T01:00:03Z","type":"response_item","payload":{"type":"function_call","name":"fixture_tool","arguments":"{}","call_id":"c"}}"# + "\n"
    try text.write(to: codex.appendingPathComponent("fixture.jsonl"), atomically: true, encoding: .utf8)
    let event = #"{"timestamp":"2026-10-29T01:00:00Z","type":"assistant","message":{"id":"fixture","role":"assistant","model":"fixture-model","content":[{"type":"tool_use","id":"call","name":"Skill","input":{"skill":"fixture-skill"}}]}}"#
    try (event + "\n").write(to: claude.appendingPathComponent("fixture.jsonl"), atomically: true, encoding: .utf8)
    var calendar = Calendar(identifier: .gregorian)
    calendar.timeZone = TimeZone(secondsFromGMT: 0)!
    let scanner = UsageActivityScanner(roots: .init(claudeProjects: claude, codexSessions: [codex]), calendar: calendar)
    for provider in [UsageProvider.codex, .claude] {
        let value = await scanner.snapshot(provider: provider, range: .sevenDays, now: fixed, calendar: calendar)
        precondition(value.tools.total > 0)
        for chart in [value.messages, value.tools, value.skills] { result.append(Capture(chart)) }
    }
    return result
}
@inline(never) private func sum(_ value: ActivityChart) -> Int {
    var n = value.total
    for s in value.series { n &+= s.total &+ s.counts.reduce(0, &+) &+ s.id.utf8.count }
    return n
}
private func measureScanner(directory: URL, names: Int, days: Int) async throws {
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    let end = Date(timeIntervalSince1970: 1_793_275_200)
    var calendar = Calendar(identifier: .gregorian)
    calendar.timeZone = TimeZone(secondsFromGMT: 0)!
    let formatter = ISO8601DateFormatter()
    for file in 0..<4 {
        var rows = ""
        for day in 0..<days {
            let time = formatter.string(from: end.addingTimeInterval(-Double(day * 86400)))
            for name in 0..<names where name % 4 == file {
                rows += "{\"timestamp\":\"\(time)\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"tool-\(name)\",\"call_id\":\"\(day)-\(name)\",\"arguments\":\"{}\"}}\n"
            }
        }
        try rows.write(to: directory.appendingPathComponent("fixture-\(file).jsonl"), atomically: true, encoding: .utf8)
    }
    let scanner = UsageActivityScanner(roots: .init(claudeProjects: directory.appendingPathComponent("unused"),
                                                  codexSessions: [directory]), calendar: calendar)
    let range: AnalyticsRange = days == 7 ? .sevenDays : .thirtyDays
    func run(_ count: Int) async -> Int {
        var checksum = 0
        for _ in 0..<count {
            let snapshot = await scanner.snapshot(provider: .codex, range: range, now: end, calendar: calendar)
            precondition(snapshot.tools.total == names * days)
            checksum &+= sum(snapshot.tools) &+ sum(snapshot.skills) &+ sum(snapshot.messages)
        }
        return checksum
    }
    _ = await run(3)
    let start = DispatchTime.now().uptimeNanoseconds, answer = await run(60)
    print(answer, DispatchTime.now().uptimeNanoseconds - start)
}

if CommandLine.arguments.count == 5 && CommandLine.arguments[1] == "scan" {
    try await measureScanner(directory: URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true),
                             names: Int(CommandLine.arguments[3])!, days: Int(CommandLine.arguments[4])!)
} else if CommandLine.arguments.count == 4 {
    let names = Int(CommandLine.arguments[1])!, days = Int(CommandLine.arguments[2])!, loops = Int(CommandLine.arguments[3])!
    let data = (0..<5).map { fixture(names, days, $0) }
    func run(_ loops: Int) -> Int {
        var checksum = 0
        for i in 0..<loops {
            let (dates, counts) = data[i % data.count]
            checksum &+= sum(.build(days: dates, counts: counts))
        }
        return checksum
    }
    _ = run(20)
    let start = DispatchTime.now().uptimeNanoseconds, answer = run(loops)
    print(answer, DispatchTime.now().uptimeNanoseconds - start)
} else {
    let directory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
    let records = try await validate(directory)
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    FileHandle.standardOutput.write(try encoder.encode(records))
}
