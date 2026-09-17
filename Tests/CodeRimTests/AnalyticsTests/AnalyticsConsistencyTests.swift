import Foundation
import XCTest
@testable import CodeRim

final class AnalyticsConsistencyTests: XCTestCase {
    func testAstraAndSolCostsAgreeAcrossTotalsAndChartBuckets() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let database = try SQLiteDatabase(url: directory.appendingPathComponent("db.sqlite"))
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "Asia/Seoul"))
        let through = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 9, day: 14, hour: 15))
        )

        for (index, model) in ["gpt-6-astra", "gpt-5.6-sol"].enumerated() {
            try await insert(
                database,
                sessionID: "session-\(index)",
                parentID: nil,
                source: "/source-\(index).jsonl",
                projectID: "project",
                projectName: "Project",
                model: model,
                usage: usage(input: 100_000, cached: 50_000, output: 10_000),
                at: through.addingTimeInterval(TimeInterval(-3_600 * (index + 1))),
                images: 0
            )
        }

        let expected = try XCTUnwrap(Decimal(string: "1.47"))
        for range in [AnalyticsRange.today, .sevenDays, .thirtyDays] {
            let snapshot = try await database.analyticsSnapshot(
                range: range,
                through: through,
                calendar: calendar
            )
            XCTAssertEqual(snapshot.quality, .exact)
            XCTAssertEqual(cost(snapshot.models, at: through), expected)
            let bucketCosts = try snapshot.buckets.map {
                try XCTUnwrap(cost($0.models, at: $0.end))
            }
            XCTAssertEqual(bucketCosts.reduce(0, +), expected)
            XCTAssertEqual(cost(try XCTUnwrap(snapshot.projects.first).models, at: through), expected)
        }

        try await insert(
            database,
            sessionID: "spark",
            parentID: nil,
            source: "/spark.jsonl",
            projectID: "project",
            projectName: "Project",
            model: "gpt-5.3-codex-spark",
            usage: usage(input: 100_000, cached: 50_000, output: 10_000),
            at: through.addingTimeInterval(-10_800),
            images: 0
        )
        for range in [AnalyticsRange.today, .sevenDays, .thirtyDays] {
            let snapshot = try await database.analyticsSnapshot(range: range, through: through, calendar: calendar)
            let chart = CostChartSummary(snapshot: snapshot)
            XCTAssertEqual(snapshot.usage.totalTokens, 330_000)
            XCTAssertEqual(chart.total.amountUSD, expected)
            XCTAssertEqual(chart.total.excludedModelIDs, ["gpt-5.3-codex-spark"])
            XCTAssertEqual(chart.total.excludedTokens, 110_000)
            XCTAssertFalse(chart.isUnavailable)
            XCTAssertEqual(chart.buckets.compactMap(\.cost.amountUSD).reduce(0, +), expected)
        }
    }

    func testUsageProjectSessionAndAgentTotalsShareOneDataset() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let database = try SQLiteDatabase(url: directory.appendingPathComponent("db.sqlite"))
        let through = Date(timeIntervalSince1970: 1_800_000_000)

        try await insert(
            database,
            sessionID: "parent",
            parentID: nil,
            source: "/parent.jsonl",
            projectID: "project-hash",
            projectName: "CodeRim",
            usage: usage(input: 1_000_000, cached: 500_000, output: 100_000),
            at: through.addingTimeInterval(-300),
            images: 2
        )
        try await insert(
            database,
            sessionID: "child",
            parentID: "parent",
            source: "/child.jsonl",
            projectID: "project-hash",
            projectName: "CodeRim",
            usage: usage(input: 500_000, cached: 250_000, output: 50_000),
            at: through.addingTimeInterval(-200),
            images: 1
        )
        try await insert(
            database,
            sessionID: "orphan",
            parentID: "missing-parent",
            source: "/orphan.jsonl",
            projectID: "other-hash",
            projectName: "Other",
            usage: usage(input: 100_000, cached: 0, output: 10_000),
            at: through.addingTimeInterval(-100),
            images: 0
        )
        try await insert(
            database,
            sessionID: "second-child",
            parentID: "parent",
            source: "/second-child.jsonl",
            projectID: "project-hash",
            projectName: "CodeRim",
            usage: usage(input: 200_000, cached: 100_000, output: 20_000),
            at: through.addingTimeInterval(-80),
            images: 0
        )
        try await insert(
            database,
            sessionID: "grandchild",
            parentID: "child",
            source: "/grandchild.jsonl",
            projectID: "project-hash",
            projectName: "CodeRim",
            usage: usage(input: 50_000, cached: 25_000, output: 5_000),
            at: through.addingTimeInterval(-60),
            images: 0
        )

        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let snapshot = try await database.analyticsSnapshot(
            range: .thirtyDays,
            through: through,
            calendar: calendar
        )

        let projectTotal = snapshot.projects.reduce(TokenUsage.zero) { $0.adding($1.usage) }
        let sessionTotal = snapshot.sessions.reduce(TokenUsage.zero) { $0.adding($1.usage) }
        let bucketTotal = snapshot.buckets.reduce(TokenUsage.zero) { $0.adding($1.usage) }
        XCTAssertEqual(snapshot.usage, projectTotal)
        XCTAssertEqual(snapshot.usage, sessionTotal)
        XCTAssertEqual(snapshot.usage, bucketTotal)
        XCTAssertEqual(snapshot.usage.totalTokens, 2_035_000)
        XCTAssertEqual(snapshot.quality, .exact)

        let parent = try XCTUnwrap(snapshot.sessions.first(where: { $0.id == "parent" }))
        let child = try XCTUnwrap(snapshot.sessions.first(where: { $0.id == "child" }))
        let orphan = try XCTUnwrap(snapshot.sessions.first(where: { $0.id == "orphan" }))
        let grandchild = try XCTUnwrap(snapshot.sessions.first(where: { $0.id == "grandchild" }))
        XCTAssertEqual(parent.directSubagentCount, 2)
        XCTAssertEqual(child.directSubagentCount, 1)
        XCTAssertEqual(parent.imageAttachmentCount, 2)
        XCTAssertEqual(child.parentSessionID, "parent")
        XCTAssertEqual(grandchild.parentSessionID, "child")
        XCTAssertEqual(orphan.directSubagentCount, 0)

        let globalCost = try XCTUnwrap(cost(snapshot.models, at: through))
        let projectCost = snapshot.projects.compactMap { cost($0.models, at: through) }.reduce(0, +)
        let sessionCost = snapshot.sessions.compactMap { cost($0.models, at: through) }.reduce(0, +)
        XCTAssertEqual(globalCost, projectCost)
        XCTAssertEqual(globalCost, sessionCost)
    }

    func testAnalyticsRangesUseCalendarBoundariesAcrossDST() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "America/Los_Angeles"))
        let through = try XCTUnwrap(
            calendar.date(from: DateComponents(year: 2026, month: 3, day: 8, hour: 23, minute: 30))
        )

        let today = AnalyticsRange.today
        let sevenDays = AnalyticsRange.sevenDays
        let thirtyDays = AnalyticsRange.thirtyDays
        XCTAssertEqual(today.bucketIntervals(through: through, calendar: calendar).count, 23)
        XCTAssertEqual(sevenDays.bucketIntervals(through: through, calendar: calendar).count, 7)
        XCTAssertEqual(thirtyDays.bucketIntervals(through: through, calendar: calendar).count, 30)
        XCTAssertEqual(today.interval(through: through, calendar: calendar).start, calendar.startOfDay(for: through))
        XCTAssertEqual(
            sevenDays.interval(through: through, calendar: calendar).start,
            calendar.date(byAdding: .day, value: -6, to: calendar.startOfDay(for: through))
        )
        XCTAssertEqual(
            thirtyDays.interval(through: through, calendar: calendar).start,
            calendar.date(byAdding: .day, value: -29, to: calendar.startOfDay(for: through))
        )
    }

    func testLegacyStandardRowsStayPriceableWhenTheirAggregateExceedsHighContextThreshold() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let database = try SQLiteDatabase(url: directory.appendingPathComponent("db.sqlite"))
        let through = Date(timeIntervalSince1970: 1_800_000_000)

        for index in 0..<2 {
            try await insert(
                database,
                sessionID: "legacy-\(index)",
                parentID: nil,
                source: "/legacy-\(index).jsonl",
                projectID: "legacy-project",
                projectName: "Legacy",
                model: "gpt-5.5",
                usage: usage(input: 150_000, cached: 100_000, output: 1_000),
                at: through.addingTimeInterval(TimeInterval(-index * 60)),
                images: 0,
                pricingContext: nil
            )
        }

        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let snapshot = try await database.analyticsSnapshot(
            range: .today,
            through: through,
            calendar: calendar
        )

        let model = try XCTUnwrap(snapshot.models.first)
        XCTAssertEqual(model.usage.inputTokens, 300_000)
        XCTAssertFalse(model.hasUnknownPricingContext)
        XCTAssertNotNil(cost(snapshot.models, at: through))
        XCTAssertFalse(try XCTUnwrap(snapshot.projects.first?.models.first).hasUnknownPricingContext)
        XCTAssertTrue(snapshot.sessions.allSatisfy { $0.models.allSatisfy { !$0.hasUnknownPricingContext } })
        XCTAssertTrue(
            snapshot.buckets
                .flatMap(\.models)
                .allSatisfy { !$0.hasUnknownPricingContext }
        )
    }

    private func insert(
        _ database: SQLiteDatabase,
        sessionID: String,
        parentID: String?,
        source: String,
        projectID: String,
        projectName: String,
        model: String = "gpt-5.6-sol",
        usage: TokenUsage,
        at date: Date,
        images: Int64,
        pricingContext: PricingContext? = .standard
    ) async throws {
        let checkpoint = SourceCheckpoint(
            sourcePath: source,
            fileIdentity: sessionID,
            generation: 0,
            committedOffset: 1,
            sessionID: sessionID,
            inheritsHistory: parentID != nil,
            sessionStartedAt: date.addingTimeInterval(-60),
            historyReplayComplete: true,
            model: model,
            projectPath: projectID,
            parentSessionID: parentID,
            projectName: projectName,
            imageAttachmentCount: images
        )
        let event = UsageEvent(
            eventKey: "event-\(sessionID)",
            occurredAt: date,
            sessionID: sessionID,
            model: model,
            projectPath: projectID,
            usage: usage,
            sourcePath: source,
            sourcePosition: 1,
            pricingContext: pricingContext
        )
        try await database.commit(
            events: [event],
            checkpoint: checkpoint,
            normalizationState: UsageNormalizationState(
                cumulativeHighWaterMark: usage,
                lastObservedAt: date,
                quality: .exact
            )
        )
    }

    private func usage(input: Int64, cached: Int64, output: Int64) -> TokenUsage {
        TokenUsage(
            inputTokens: input,
            cachedInputTokens: cached,
            cacheWriteInputTokens: 0,
            outputTokens: output
        )
    }

    private func cost(_ models: [ModelUsageSummary], at date: Date) -> Decimal? {
        CostEstimator().estimate(
            models.map {
                ModelTokenUsageSample(
                    modelID: $0.modelID ?? "unknown",
                    usage: $0.usage,
                    highContextUsage: $0.highContextUsage,
                    hasUnknownPricingContext: $0.hasUnknownPricingContext,
                    occurredAt: date
                )
            }
        ).amountUSD
    }
}
