import Foundation
import XCTest
@testable import CodeRim

final class EstimatedCostDisplayTests: XCTestCase {
    private let through = Date(timeIntervalSince1970: 1_789_344_000)

    func testPartialHistoryStillPricesRecordedAstraTokens() throws {
        let models = [model()]
        let expected = try XCTUnwrap(Decimal(string: "1.05"))

        XCTAssertEqual(estimatedCost(for: models, through: through, quality: .partial), expected)
        XCTAssertEqual(estimatedCost(for: models, through: through, quality: .exact), expected)
    }

    func testUnavailableHistoryDoesNotProduceAnEstimate() {
        XCTAssertNil(estimatedCost(for: [model()], through: through, quality: .unavailable))
        XCTAssertNil(CostDisplaySummary(models: [model()], through: through, quality: .unavailable).amountUSD)
    }

    func testCostChartRemainsAvailableForPriceablePartialHistoryAndEmptyIntervals() {
        for quality in [DataQuality.exact, .partial] {
            XCTAssertFalse(CostChartSummary(snapshot: snapshot(quality: quality, models: [model()])).isUnavailable)
        }
    }

    func testCostChartStillRejectsUnavailableUsageAndUnpriceableIntervals() {
        XCTAssertTrue(CostChartSummary(snapshot: snapshot(quality: .unavailable, models: [model()])).isUnavailable)
        for quality in [DataQuality.exact, .partial] {
            XCTAssertTrue(CostChartSummary(snapshot: snapshot(quality: quality, models: [model(id: "future-model")])).isUnavailable)
            XCTAssertTrue(CostChartSummary(snapshot: snapshot(quality: quality, models: [model(cacheWrite: nil)])).isUnavailable)
        }
    }

    func testMixedAstraSolAndSparkShowsAnExplicitSubtotal() {
        let spark = model(id: "gpt-5.3-codex-spark")
        let models = [model(), model(id: "gpt-5.6-sol"), spark]
        for quality in [DataQuality.exact, .partial] {
            // The strict estimator still cannot call the subtotal a full cost.
            XCTAssertNil(estimatedCost(for: models, through: through, quality: quality))
            let summary = CostDisplaySummary(models: models, through: through, quality: quality)
            XCTAssertEqual(summary.amountUSD, Decimal(string: "1.47"))
            XCTAssertTrue(summary.isPartial)
            XCTAssertEqual(summary.excludedModels, [spark])
            XCTAssertEqual(summary.excludedTokens, 110_000)
            XCTAssertTrue(summary.exclusionDescription.contains(spark.displayName))
            let chart = CostChartSummary(snapshot: snapshot(quality: quality, models: models))
            XCTAssertFalse(chart.isUnavailable)
            XCTAssertEqual(chart.buckets.compactMap(\.cost.amountUSD).reduce(0, +), summary.amountUSD)
        }
    }

    func testUnknownOnlyIntervalIsAGapAndDoesNotHideOtherBars() {
        let models = [model(), model(id: "future-model")]
        let chart = CostChartSummary(snapshot: snapshot(quality: .partial, groups: models.map { [$0] }))
        XCTAssertFalse(chart.isUnavailable)
        XCTAssertTrue(chart.hasUnavailableIntervals)
        XCTAssertEqual(chart.buckets[0].cost.amountUSD, Decimal(string: "1.05"))
        XCTAssertNil(chart.buckets[1].cost.amountUSD)
        XCTAssertEqual(chart.buckets.compactMap(\.cost.amountUSD).reduce(0, +), chart.total.amountUSD)
    }

    func testIncompleteMetadataExcludesTheSameModelFromEveryBar() {
        let astra = model()
        let incompleteAstra = model(cacheWrite: nil)
        let totalAstra = ModelUsageSummary(modelID: astra.modelID, usage: astra.usage.adding(incompleteAstra.usage))
        let sol = model(id: "gpt-5.6-sol")
        let chart = CostChartSummary(snapshot: snapshot(
            quality: .exact,
            groups: [[astra, sol], [incompleteAstra]],
            models: [totalAstra, sol]
        ))
        XCTAssertEqual(chart.total.amountUSD, Decimal(string: "0.42"))
        XCTAssertEqual(chart.total.excludedTokens, 220_000)
        XCTAssertEqual(chart.buckets[0].cost.amountUSD, Decimal(string: "0.42"))
        XCTAssertNil(chart.buckets[1].cost.amountUSD)
        XCTAssertEqual(chart.buckets.compactMap(\.cost.amountUSD).reduce(0, +), chart.total.amountUSD)
    }

    func testEmptyUsageIsZeroButUnknownUsageIsNotFree() {
        let empty = CostDisplaySummary(models: [], through: through, quality: .partial)
        XCTAssertEqual(empty.amountUSD, 0)
        XCTAssertFalse(empty.isPartial)
        let unknown = CostDisplaySummary(models: [model(id: "unknown")], through: through, quality: .partial)
        XCTAssertNil(unknown.amountUSD)
        XCTAssertTrue(unknown.isPartial)
        let zeroUnknown = ModelUsageSummary(modelID: nil, usage: .zero)
        let known = CostDisplaySummary(models: [model(), zeroUnknown], through: through, quality: .exact)
        XCTAssertEqual(known.amountUSD, Decimal(string: "1.05"))
        XCTAssertFalse(known.isPartial)
    }

    func testPartialHistoryStillRequiresCacheWriteAndContextMetadata() {
        let missingCache = model(cacheWrite: nil)
        let unknownContext = ModelUsageSummary(
            modelID: "gpt-6-astra",
            usage: TokenUsage(
                inputTokens: 300_000,
                cachedInputTokens: 0,
                cacheWriteInputTokens: 0,
                outputTokens: 1
            ),
            hasUnknownPricingContext: true
        )
        for summary in [missingCache, unknownContext] {
            XCTAssertNil(estimatedCost(for: [summary], through: through, quality: .partial))
            XCTAssertNil(CostDisplaySummary(models: [summary], through: through, quality: .partial).amountUSD)
        }
    }

    private func snapshot(quality: DataQuality, models: [ModelUsageSummary]) -> AnalyticsSnapshot {
        snapshot(quality: quality, groups: [[], models], models: models)
    }

    private func snapshot(quality: DataQuality, groups: [[ModelUsageSummary]], models: [ModelUsageSummary]? = nil) -> AnalyticsSnapshot {
        let models = models ?? groups.flatMap { $0 }
        let start = through.addingTimeInterval(-7_200)
        let middle = through.addingTimeInterval(-3_600)
        return AnalyticsSnapshot(
            range: .today,
            interval: DateInterval(start: start, end: through),
            through: through,
            usage: models.reduce(TokenUsage.zero) { $0.adding($1.usage) },
            quality: quality,
            buckets: [
                UsageBucket(start: start, end: middle, models: groups[0]),
                UsageBucket(start: middle, end: through, models: groups[1])
            ],
            models: models,
            projects: [],
            sessions: []
        )
    }

    private func model(id: String = "gpt-6-astra", cacheWrite: Int64? = 0) -> ModelUsageSummary {
        ModelUsageSummary(
            modelID: id,
            usage: TokenUsage(
                inputTokens: 100_000,
                cachedInputTokens: 50_000,
                cacheWriteInputTokens: cacheWrite,
                outputTokens: 10_000
            )
        )
    }
}
