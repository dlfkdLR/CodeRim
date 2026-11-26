import Foundation

let date = Date(timeIntervalSince1970: 1_795_000_100)
let catalog = CompanionProviderID.allCases
func reading(_ id: CompanionProviderID, phase: Int) -> CompanionProvider {
    let index = catalog.firstIndex(of: id)! + phase
    var cost: Double? = 0.71
    var percentage: Double? = 112
    if index % 13 == 0 { percentage = .infinity }
    if index % 17 == 0 { cost = .nan }
    if index % 5 == 0 { percentage = nil }
    let day = CompanionHistoryDay(date: date, tokens: 148, estimatedCostUSD: cost, costIsPartial: index % 3 == 0)
    let history = CompanionHistory(state: .ready, updatedAt: date, timeZoneIdentifier: "Asia/Seoul",
        days: [day], totalTokens: 148, estimatedCostUSD: 2, costIsPartial: false)
    let window = CompanionLimitWindow(id: "한도", name: "요금 é · e\u{301}", durationMinutes: 3,
        usedPercent: percentage, resetsAt: date, displayValue: "0%")
    return CompanionProvider(id: id.rawValue, name: id.name, localUsage: nil,
        limits: CompanionLimits(state: .ready, updatedAt: date, windows: [window], message: "합성"),
        history: history, plan: "fixture")
}
let samples = catalog.enumerated().map { reading($0.element, phase: $0.offset % 4) }
let lookup = Dictionary(uniqueKeysWithValues: zip(catalog, samples))
if CommandLine.arguments.count > 1 {
    let selected = Set(catalog.prefix(Int(CommandLine.arguments[1])!).map(\.rawValue))
    let loops = Int(CommandLine.arguments[2])!
    let clock = DispatchTime.now().uptimeNanoseconds
    var checksum = 0
    for _ in 0..<loops {
        let rows = Publisher.projectedProviders(selected: selected) { lookup[$0]! }
        checksum += rows.reduce(0) { $0 + $1.limits.state.rawValue.utf8.count }
    }
    print(checksum, Double(DispatchTime.now().uptimeNanoseconds-clock) / Double(loops))
} else {
    let encoder = JSONEncoder()
    encoder.dateEncodingStrategy = .iso8601
    encoder.outputFormatting = [.sortedKeys]
    for phase in 0..<12 {
        for count in [0, 1, 2, 8, catalog.count/2, catalog.count-1, catalog.count] {
            var selected = Set((0..<count).map { catalog[($0 + phase*7) % catalog.count].rawValue })
            selected.insert("unlisted-fixture")
            var visited = [CompanionProviderID]()
            let rows = Publisher.projectedProviders(selected: selected) { id in
                visited.append(id)
                return reading(id, phase: phase)
            }
            precondition(visited == catalog.filter { selected.contains($0.rawValue) })
            precondition(rows.map(\.id) == catalog.map(\.rawValue))
            precondition(rows.filter { !$0.enabled }.count == catalog.count - count)
            let value = CompanionSnapshot(generatedAt: date, providers: rows)
            print(try String(decoding: encoder.encode(value), as: UTF8.self))
        }
    }
}
