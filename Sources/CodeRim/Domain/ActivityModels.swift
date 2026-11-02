import Foundation

/// What a session's turns ran on, as counts. The shares a person sees are
/// worked out from these when they are shown, so two sessions can be added.
struct SessionTraits: Equatable, Sendable {
    var models: [String: Int] = [:]
    var efforts: [String: Int] = [:]
    var speeds: [String: Int] = [:]

    var isEmpty: Bool { models.isEmpty && efforts.isEmpty && speeds.isEmpty }

    /// Largest first, then by name so a tie never reorders between refreshes.
    static func shares(_ counts: [String: Int]) -> [(name: String, share: Double)] {
        let total = counts.values.reduce(0, +)
        guard total > 0 else { return [] }
        return counts.sorted { $0.value != $1.value ? $0.value > $1.value : $0.key < $1.key }
            .map { ($0.key, Double($0.value) / Double(total)) }
    }
}

/// One log file's activity on one day.
struct DayActivity: Equatable, Sendable {
    /// Messages the person sent, by the model that answered.
    var messagesByModel: [String: Int] = [:]
    /// Tool calls, by tool (an MCP server's tools are one tool, its server).
    var tools: [String: Int] = [:]
    var skills: [String: Int] = [:]
}

/// Everything read from one log file.
struct FileActivity: Equatable, Sendable {
    /// The analytics' own identifier for the session, so a row there finds its
    /// traits here.
    var sessionID: String?
    var days: [Date: DayActivity] = [:]
    var traits = SessionTraits()

    mutating func edit(_ day: Date, _ change: (inout DayActivity) -> Void) {
        var value = days[day] ?? DayActivity()
        change(&value)
        days[day] = value
    }
}

struct ActivitySeries: Equatable, Sendable, Identifiable {
    let id: String
    let title: String
    let total: Int
    /// One value per day of the chart, zero where nothing happened.
    let counts: [Int]
}

/// A chart's worth of counts: a handful of named series and the rest together.
struct ActivityChart: Equatable, Sendable {
    let days: [Date]
    let series: [ActivitySeries]
    let total: Int

    static let otherID = "other"

    static func empty(days: [Date]) -> ActivityChart {
        ActivityChart(days: days, series: [], total: 0)
    }

    /// The busiest `limit` names, then everything else as one series. Ties are
    /// broken by name so the colours do not swap between refreshes.
    static func build(days: [Date], counts: [Date: [String: Int]], limit: Int = 5,
                      otherTitle: String = "Other") -> ActivityChart {
        var totals: [String: Int] = [:]
        for byName in counts.values { for (name, count) in byName { totals[name, default: 0] += count } }
        let ranked = totals.sorted { $0.value != $1.value ? $0.value > $1.value : $0.key < $1.key }
        let named = Set(ranked.prefix(limit).map(\.key))
        func perDay(where names: (String) -> Bool) -> [Int] {
            days.map { day in (counts[day] ?? [:]).reduce(0) { names($1.key) ? $0 + $1.value : $0 } }
        }
        var series = ranked.prefix(limit).map { name, total in
            ActivitySeries(id: name, title: name, total: total, counts: perDay { $0 == name })
        }
        let rest = ranked.dropFirst(limit).reduce(0) { $0 + $1.value }
        if rest > 0 {
            series.append(ActivitySeries(id: otherID, title: otherTitle, total: rest,
                                         counts: perDay { !named.contains($0) }))
        }
        return ActivityChart(days: days, series: series, total: totals.values.reduce(0, +))
    }
}

struct ActivitySnapshot: Equatable, Sendable {
    let range: AnalyticsRange
    let messages: ActivityChart
    let tools: ActivityChart
    let skills: ActivityChart
    /// By the analytics' session identifier.
    let traits: [String: SessionTraits]

    static func empty(range: AnalyticsRange, days: [Date] = []) -> ActivitySnapshot {
        ActivitySnapshot(range: range, messages: .empty(days: days), tools: .empty(days: days),
                         skills: .empty(days: days), traits: [:])
    }
}
