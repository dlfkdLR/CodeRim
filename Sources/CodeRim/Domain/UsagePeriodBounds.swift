import Foundation

enum WeekStart: Int, CaseIterable, Codable, Sendable {
    case sunday = 1
    case monday = 2
}

struct UsagePeriodBounds: Sendable {
    let today: Date
    let week: Date
    let month: Date

    init(now: Date, calendar baseCalendar: Calendar, weekStart: WeekStart) {
        var calendar = baseCalendar
        calendar.firstWeekday = weekStart.rawValue
        today = calendar.startOfDay(for: now)
        week = calendar.dateInterval(of: .weekOfYear, for: now)?.start ?? today
        month = calendar.dateInterval(of: .month, for: now)?.start ?? today
    }
}
