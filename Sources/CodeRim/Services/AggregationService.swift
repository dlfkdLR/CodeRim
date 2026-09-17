import Foundation

enum WeekStart: Int, CaseIterable, Codable, Sendable {
    case sunday = 1
    case monday = 2
}

struct AggregationService: Sendable {
    func snapshot(
        from events: [UsageEvent],
        now: Date,
        calendar baseCalendar: Calendar,
        weekStart: WeekStart
    ) -> UsageSnapshot {
        var calendar = baseCalendar
        calendar.firstWeekday = weekStart.rawValue

        let todayStart = calendar.startOfDay(for: now)
        let weekStartDate = calendar.dateInterval(of: .weekOfYear, for: now)?.start ?? todayStart
        let monthStart = calendar.dateInterval(of: .month, for: now)?.start ?? todayStart

        var today = TokenUsage.zero
        var week = TokenUsage.zero
        var month = TokenUsage.zero
        var allTime = TokenUsage.zero

        for event in events where event.occurredAt <= now {
            allTime = allTime.adding(event.usage)
            if event.occurredAt >= monthStart { month = month.adding(event.usage) }
            if event.occurredAt >= weekStartDate { week = week.adding(event.usage) }
            if event.occurredAt >= todayStart { today = today.adding(event.usage) }
        }

        let visibleEvents = events.filter { $0.occurredAt <= now }
        return UsageSnapshot(
            today: today,
            week: week,
            month: month,
            allTime: allTime,
            quality: visibleEvents.isEmpty ? .unavailable : .exact,
            updatedAt: visibleEvents.map(\.occurredAt).max()
        )
    }
}
