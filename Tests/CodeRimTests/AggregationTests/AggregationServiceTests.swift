import Foundation
import XCTest
@testable import CodeRim

final class AggregationServiceTests: XCTestCase {
    func testTotalCountsCachedInputOnlyAsPartOfInput() {
        let usage = TokenUsage(inputTokens: 1_200, cachedInputTokens: 800, outputTokens: 300)

        XCTAssertEqual(usage.totalTokens, 1_500)
    }

    func testMondayWeekAndPeriodBoundaries() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "Asia/Seoul"))
        let now = try XCTUnwrap(calendar.date(from: DateComponents(year: 2026, month: 8, day: 27, hour: 12)))

        let events = [
            event(at: calendar.date(byAdding: .hour, value: -1, to: now)!, tokens: 10),
            event(at: calendar.date(from: DateComponents(year: 2026, month: 8, day: 24, hour: 9))!, tokens: 20),
            event(at: calendar.date(from: DateComponents(year: 2026, month: 8, day: 23, hour: 9))!, tokens: 30),
            event(at: calendar.date(from: DateComponents(year: 2026, month: 7, day: 31, hour: 23))!, tokens: 40)
        ]

        let snapshot = AggregationService().snapshot(
            from: events,
            now: now,
            calendar: calendar,
            weekStart: .monday
        )

        XCTAssertEqual(snapshot.today.totalTokens, 10)
        XCTAssertEqual(snapshot.week.totalTokens, 30)
        XCTAssertEqual(snapshot.month.totalTokens, 60)
        XCTAssertEqual(snapshot.allTime.totalTokens, 100)
    }

    func testSundayWeekStartAndFutureEvents() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "Asia/Seoul"))
        let now = try XCTUnwrap(calendar.date(from: DateComponents(year: 2026, month: 8, day: 26, hour: 12)))
        let sunday = try XCTUnwrap(calendar.date(from: DateComponents(year: 2026, month: 8, day: 23, hour: 9)))
        let saturday = try XCTUnwrap(calendar.date(from: DateComponents(year: 2026, month: 8, day: 22, hour: 23)))
        let future = try XCTUnwrap(calendar.date(byAdding: .minute, value: 1, to: now))

        let snapshot = AggregationService().snapshot(
            from: [event(at: sunday, tokens: 20), event(at: saturday, tokens: 30), event(at: future, tokens: 40)],
            now: now,
            calendar: calendar,
            weekStart: .sunday
        )

        XCTAssertEqual(snapshot.week.totalTokens, 20)
        XCTAssertEqual(snapshot.allTime.totalTokens, 50)
        XCTAssertEqual(snapshot.updatedAt, sunday)

        let futureOnly = AggregationService().snapshot(
            from: [event(at: future, tokens: 40)],
            now: now,
            calendar: calendar,
            weekStart: .sunday
        )
        XCTAssertEqual(futureOnly.quality, .unavailable)
        XCTAssertNil(futureOnly.updatedAt)
    }

    func testYearLeapDayAndMidnightBoundaries() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "UTC"))

        let newYear = try XCTUnwrap(calendar.date(from: DateComponents(year: 2027, month: 1, day: 1, hour: 0, minute: 1)))
        let previousYear = try XCTUnwrap(calendar.date(from: DateComponents(year: 2026, month: 12, day: 31, hour: 23, minute: 59)))
        let afterMidnight = try XCTUnwrap(calendar.date(from: DateComponents(year: 2027, month: 1, day: 1, hour: 0)))
        let yearSnapshot = AggregationService().snapshot(
            from: [event(at: previousYear, tokens: 10), event(at: afterMidnight, tokens: 20)],
            now: newYear,
            calendar: calendar,
            weekStart: .monday
        )
        XCTAssertEqual(yearSnapshot.today.totalTokens, 20)
        XCTAssertEqual(yearSnapshot.month.totalTokens, 20)
        XCTAssertEqual(yearSnapshot.allTime.totalTokens, 30)

        let leapNow = try XCTUnwrap(calendar.date(from: DateComponents(year: 2028, month: 3, day: 1, hour: 0, minute: 1)))
        let leapDay = try XCTUnwrap(calendar.date(from: DateComponents(year: 2028, month: 2, day: 29, hour: 23, minute: 59)))
        let leapSnapshot = AggregationService().snapshot(
            from: [event(at: leapDay, tokens: 25)],
            now: leapNow,
            calendar: calendar,
            weekStart: .monday
        )
        XCTAssertEqual(leapSnapshot.today.totalTokens, 0)
        XCTAssertEqual(leapSnapshot.month.totalTokens, 0)
        XCTAssertEqual(leapSnapshot.allTime.totalTokens, 25)
    }

    func testDSTStartUsesLocalCalendarDay() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "America/Los_Angeles"))
        let now = try XCTUnwrap(calendar.date(from: DateComponents(year: 2026, month: 3, day: 8, hour: 12)))
        let previousNight = try XCTUnwrap(calendar.date(from: DateComponents(year: 2026, month: 3, day: 7, hour: 23, minute: 59)))
        let sameDay = try XCTUnwrap(calendar.date(from: DateComponents(year: 2026, month: 3, day: 8, hour: 3, minute: 1)))

        let snapshot = AggregationService().snapshot(
            from: [event(at: previousNight, tokens: 10), event(at: sameDay, tokens: 20)],
            now: now,
            calendar: calendar,
            weekStart: .sunday
        )
        XCTAssertEqual(snapshot.today.totalTokens, 20)
        XCTAssertEqual(snapshot.week.totalTokens, 20)
    }

    private func event(at date: Date, tokens: Int64) -> UsageEvent {
        UsageEvent(
            eventKey: UUID().uuidString,
            occurredAt: date,
            sessionID: nil,
            model: nil,
            projectPath: nil,
            usage: TokenUsage(inputTokens: tokens, cachedInputTokens: 0, outputTokens: 0),
            sourcePath: "/fixture",
            sourcePosition: 0
        )
    }
}
