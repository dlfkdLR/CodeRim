import Foundation

let clock = Date(timeIntervalSince1970: 1_730_611_799.75)
let states: [CompanionState] = [.ready, .partial, .stale, .loading, .disabled, .unavailable,
                                .needsAuth, .accessDenied, .unsupported]

func fixture(_ seed: Int, providerCount: Int, calendar: Calendar) -> CompanionSnapshot {
    let offsets: [Double?] = [nil, -600.000_001, -600, -300.000_001, -300, 0, 60, 60.000_001]
    let shifted: (Int) -> Date? = { place in
        offsets[(seed + place) % offsets.count].map(clock.addingTimeInterval)
    }
    let zone = seed.isMultiple(of: 7) ? "other-zone" : calendar.timeZone.identifier
    let state: (Int) -> CompanionState = { states[(seed / 8 + $0) % states.count] }
    let windows = (0..<(seed % 5)).map { n in
        CompanionLimitWindow(id: "w" + String(n), name: "주간", durationMinutes: 60,
                             usedPercent: 37, resetsAt: shifted(n))
    }
    let usage = CompanionLocalUsage(state: state(0), updatedAt: shifted(0),
        periodsAsOf: seed.isMultiple(of: 6) ? clock.addingTimeInterval(-86_400) : clock,
        timeZoneIdentifier: zone, totals: ["today": .init(inputTokens: 91, cachedInputTokens: 8, outputTokens: 5)])
    let history = CompanionHistory(state: state(2), updatedAt: shifted(3) ?? clock,
        timeZoneIdentifier: zone, days: [.init(date: clock, tokens: 96, estimatedCostUSD: 0.125, costIsPartial: false)],
        totalTokens: 96, estimatedCostUSD: 0.125, costIsPartial: false)
    let provider = CompanionProvider(id: "target", name: "첫 번째", localUsage: seed.isMultiple(of: 13) ? nil : usage,
        limits: .init(state: state(1), updatedAt: shifted(1), windows: windows,
                      staleAfterSeconds: seed.isMultiple(of: 3) ? 120 : 300),
        history: seed.isMultiple(of: 11) ? nil : history)
    var providers = (0..<providerCount).map { n in
        var copy = provider
        copy.id = "model-" + String(n)
        return copy
    }
    providers[providerCount / 2] = provider
    var duplicate = provider
    duplicate.name = "중복"
    providers.append(duplicate)
    return CompanionSnapshot(generatedAt: clock, providers: providers)
}

@inline(never)
func readings(_ snapshot: CompanionSnapshot, _ id: String, _ dates: [Date], _ calendar: Calendar) -> [CompanionProvider?] {
    #if WIDGET_ORIGINAL
    return dates.map { date in snapshot.evaluated(at: date, calendar: calendar).providers.first { $0.id == id } }
    #else
    return snapshot.widgetReadings(for: id, at: dates, calendar: calendar)
    #endif
}

var calendar = Calendar(identifier: .gregorian)
let dates = (0...15).map { clock.addingTimeInterval(Double($0) * 60) }
if CommandLine.arguments.count > 1, let count = Int(CommandLine.arguments[1]) {
    calendar.timeZone = TimeZone(identifier: "America/New_York")!
    let seed = Int(CommandLine.arguments[2])!
    var source = fixture(seed, providerCount: count, calendar: calendar)
    source.providers.removeLast()  // Benchmarks have exactly the requested number of providers.
    let start = DispatchTime.now().uptimeNanoseconds
    var checksum = 0
    for _ in 0..<100 {
        for item in readings(source, "target", dates, calendar) {
            if let item { checksum += item.limits.state.rawValue.utf8.count + (item.localUsage?.state.rawValue.utf8.count ?? 0) }
        }
    }
    print(checksum, DispatchTime.now().uptimeNanoseconds - start)
} else {
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    for zone in ["UTC", "America/New_York", "Asia/Seoul", "Pacific/Apia"] {
        calendar.timeZone = TimeZone(identifier: zone)!
        for seed in 0..<72 {
            let source = fixture(seed, providerCount: 3, calendar: calendar)
            let calendarDayEnd = calendar.dateInterval(of: .day, for: clock)!.end
            let boundaryDates = [-600.000_001, -600, -300.000_001, -300, -60.000_001, -60, 0, 0.000_001,
                                 300, 300.000_001, 600, 600.000_001].map(clock.addingTimeInterval)
            let queries = dates + boundaryDates + [calendarDayEnd.addingTimeInterval(-0.01), calendarDayEnd]
            for id in ["target", "absent"] {
                let received = readings(source, id, queries, calendar)
                // Compare the entire wire object once; every temporal sample retains
                // the selected identity and all three independently visible states.
                print(try! String(decoding: encoder.encode(received.first ?? nil), as: UTF8.self))
                for item in received {
                    print(item?.name ?? "-", item?.limits.state.rawValue ?? "-",
                          item?.localUsage?.state.rawValue ?? "-", item?.history?.state.rawValue ?? "-")
                }
            }
        }
    }
}
