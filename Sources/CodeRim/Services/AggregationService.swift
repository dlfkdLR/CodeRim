import Foundation

struct AggregationService: Sendable {
    func snapshot(
        from events: [UsageEvent],
        now: Date,
        calendar baseCalendar: Calendar,
        weekStart: WeekStart
    ) -> UsageSnapshot {
        let bounds = UsagePeriodBounds(now: now, calendar: baseCalendar, weekStart: weekStart)

        var today = TokenUsage.zero
        var week = TokenUsage.zero
        var month = TokenUsage.zero
        var allTime = TokenUsage.zero

        for event in events where event.occurredAt <= now {
            allTime = allTime.adding(event.usage)
            if event.occurredAt >= bounds.month { month = month.adding(event.usage) }
            if event.occurredAt >= bounds.week { week = week.adding(event.usage) }
            if event.occurredAt >= bounds.today { today = today.adding(event.usage) }
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
