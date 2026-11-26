import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

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
        #if CODERIM_RUST_ACCOUNTING
        if let chart = projected(days: days, counts: counts, limit: limit, otherTitle: otherTitle) { return chart }
        #endif
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

#if CODERIM_RUST_ACCOUNTING
private extension ActivityChart {
    static func projected(days: [Date], counts: [Date: [String: Int]], limit: Int, otherTitle: String) -> Self? {
        guard limit >= 0, days.count < Int(UInt32.max) else { return nil }
        // With no overflow category, each day's named value is already present
        // in the source dictionary. Avoid building a foreign sparse matrix for
        // a projection which requires only keyed reads. Stop this check as soon
        // as one extra name proves that aggregation is needed.
        var directTotals: [String: Int] = [:]
        var needsGrouping = false
        outer: for byName in counts.values {
            for (name, count) in byName {
                directTotals[name, default: 0] += count
                if directTotals.count > limit {
                    needsGrouping = true
                    break outer
                }
            }
        }
        if !needsGrouping {
            let ranked = directTotals.sorted { $0.value != $1.value ? $0.value > $1.value : $0.key < $1.key }
            let visibleDays = days.map { counts[$0] }
            let series = ranked.map { name, total in
                ActivitySeries(id: name, title: name, total: total,
                               counts: visibleDays.map { $0?[name] ?? 0 })
            }
            return Self(days: days, series: series, total: directTotals.values.reduce(0, +))
        }
        var dates: [Date: UInt32] = [:]
        var displaySlots: [UInt32] = []
        displaySlots.reserveCapacity(days.count)
        for day in days {
            if let slot = dates[day] { displaySlots.append(slot) }
            else {
                let slot = UInt32(dates.count)
                dates[day] = slot
                displaySlots.append(slot)
            }
        }
        var ids: [String: UInt32] = [:]
        var names: [String] = []
        var rows: [cr_activity_count] = []
        rows.reserveCapacity(counts.values.reduce(0) { $0 + $1.count })
        for (day, byName) in counts {
            let slot = dates[day] ?? .max
            for (name, value) in byName {
                let id: UInt32
                if let existing = ids[name] { id = existing }
                else {
                    guard names.count < Int(UInt32.max) - 1 else { return nil }
                    id = UInt32(names.count)
                    names.append(name)
                    ids[name] = id
                }
                rows.append(cr_activity_count(count: Int64(value), name: id, day: slot))
            }
        }
        guard !rows.isEmpty else { return .empty(days: days) }
        let ordered = names.indices.sorted { names[$0] < names[$1] }
        var ranks = [UInt32](repeating: 0, count: names.count)
        for (rank, id) in ordered.enumerated() { ranks[id] = UInt32(rank) }
        let slots = min(limit, names.count) + 1
        let (capacity, overflow) = slots.multipliedReportingOverflow(by: dates.count)
        guard !overflow else { return nil }
        var labels = [cr_activity_series](repeating: .init(), count: slots)
        var cells = [Int64](repeating: 0, count: capacity)
        let projected = cr_activity_distribution(rows, rows.count, ranks, ranks.count,
                                                 dates.count, limit, &labels, &cells)
        guard projected.series_count >= 0 && projected.series_count <= slots else { return nil }
        var result: [ActivitySeries] = []
        result.reserveCapacity(projected.series_count)
        for i in 0..<projected.series_count {
            let label = labels[i]
            let id = label.name == .max ? otherID : names[Int(label.name)]
            let title = label.name == .max ? otherTitle : id
            let start = i * dates.count
            result.append(ActivitySeries(id: id, title: title, total: Int(label.total),
                                         counts: displaySlots.map { Int(cells[start + Int($0)]) }))
        }
        return ActivityChart(days: days, series: result, total: Int(projected.total))
    }
}
#endif

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
