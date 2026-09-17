import Foundation
import XCTest
@testable import CodexMeter

final class CostEstimatorTests: XCTestCase {
    private let estimator = CostEstimator()
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    func testAstraAndSolUsageProducesACompleteEstimate() throws {
        let estimate = estimator.estimate([
            ModelTokenUsageSample(
                modelID: "gpt-6-astra",
                usage: TokenUsage(
                    inputTokens: 1_000_000,
                    cachedInputTokens: 750_000,
                    cacheWriteInputTokens: 50_000,
                    outputTokens: 100_000
                ),
                occurredAt: now
            ),
            sample(modelID: "gpt-5.6-sol", input: 100_000)
        ])

        XCTAssertTrue(estimate.isAvailable)
        XCTAssertEqual(estimate.coverage, .complete)
        XCTAssertEqual(estimate.breakdown?.uncachedInputUSD, try decimal("2.4"))
        XCTAssertEqual(estimate.breakdown?.cachedInputUSD, try decimal("0.75"))
        XCTAssertEqual(estimate.breakdown?.cacheWriteInputUSD, try decimal("0.625"))
        XCTAssertEqual(estimate.breakdown?.outputUSD, try decimal("5"))
        XCTAssertEqual(estimate.amountUSD, try decimal("8.775"))
    }

    func testAstraHighContextMultipliersApplyOnlyToQualifyingUsage() throws {
        let highContext = TokenUsage(
            inputTokens: 400_000,
            cachedInputTokens: 200_000,
            cacheWriteInputTokens: 100_000,
            outputTokens: 20_000
        )
        let standard = TokenUsage(
            inputTokens: 100_000,
            cachedInputTokens: 50_000,
            cacheWriteInputTokens: 0,
            outputTokens: 10_000
        )
        let estimate = estimator.estimate([
            ModelTokenUsageSample(
                modelID: "gpt-6-astra",
                usage: highContext.adding(standard),
                highContextUsage: highContext,
                occurredAt: now
            )
        ])

        XCTAssertEqual(estimate.coverage, .complete)
        XCTAssertEqual(estimate.breakdown?.uncachedInputUSD, try decimal("2.5"))
        XCTAssertEqual(estimate.breakdown?.cachedInputUSD, try decimal("0.45"))
        XCTAssertEqual(estimate.breakdown?.cacheWriteInputUSD, try decimal("2.5"))
        XCTAssertEqual(estimate.breakdown?.outputUSD, try decimal("2"))
        XCTAssertEqual(estimate.amountUSD, try decimal("7.45"))
    }

    func testAstraStillRequiresMetadataThatChangesItsPrice() {
        let missingCacheWrite = estimator.estimate(
            modelID: "gpt-6-astra",
            usage: TokenUsage(inputTokens: 10, cachedInputTokens: 2, outputTokens: 1),
            occurredAt: now
        )
        let unknownContext = estimator.estimate([
            ModelTokenUsageSample(
                modelID: "gpt-6-astra",
                usage: TokenUsage(
                    inputTokens: 300_000,
                    cachedInputTokens: 0,
                    cacheWriteInputTokens: 0,
                    outputTokens: 1
                ),
                hasUnknownPricingContext: true,
                occurredAt: now
            )
        ])

        for estimate in [missingCacheWrite, unknownContext] {
            XCTAssertEqual(estimate.coverage, .incompleteMetadata(modelIDs: ["gpt-6-astra"]))
            XCTAssertNil(estimate.amountUSD)
        }
    }

    func testZeroTokensProduceAnAvailableZeroEstimate() throws {
        let estimate = estimator.estimate(modelID: "gpt-5.6-sol", usage: .zero, occurredAt: now)

        XCTAssertTrue(estimate.isAvailable)
        XCTAssertEqual(estimate.coverage, .complete)
        XCTAssertEqual(estimate.amountUSD, 0)
        XCTAssertEqual(estimate.breakdown, .zero)
    }

    func testCachedInputIsSubtractedBeforeApplyingInputPrice() throws {
        let usage = TokenUsage(
            inputTokens: 1_000_000,
            cachedInputTokens: 750_000,
            cacheWriteInputTokens: 0,
            outputTokens: 100_000
        )
        let estimate = estimator.estimate(modelID: "gpt-5.6-sol", usage: usage, occurredAt: now)
        let breakdown = try XCTUnwrap(estimate.breakdown)

        XCTAssertEqual(breakdown.uncachedInputUSD, try decimal("1"))
        XCTAssertEqual(breakdown.cachedInputUSD, try decimal("0.3"))
        XCTAssertEqual(breakdown.outputUSD, try decimal("2"))
        XCTAssertEqual(estimate.amountUSD, try decimal("3.3"))
    }

    func testMixedModelsUseTheirOwnPricesAndShareOneCompleteTotal() throws {
        let samples = [
            ModelTokenUsageSample(
                modelID: "gpt-5.6-sol",
                usage: TokenUsage(
                    inputTokens: 1_000_000,
                    cachedInputTokens: 500_000,
                    cacheWriteInputTokens: 0,
                    outputTokens: 100_000
                ),
                occurredAt: now
            ),
            ModelTokenUsageSample(
                modelID: "gpt-5.6-terra",
                usage: TokenUsage(
                    inputTokens: 2_000_000,
                    cachedInputTokens: 1_000_000,
                    cacheWriteInputTokens: 0,
                    outputTokens: 500_000
                ),
                occurredAt: now
            )
        ]

        let estimate = estimator.estimate(samples)
        let breakdown = try XCTUnwrap(estimate.breakdown)
        XCTAssertEqual(estimate.coverage, .complete)
        XCTAssertEqual(breakdown.uncachedInputUSD, try decimal("4"))
        XCTAssertEqual(breakdown.cachedInputUSD, try decimal("0.4"))
        XCTAssertEqual(breakdown.outputUSD, try decimal("8"))
        XCTAssertEqual(estimate.amountUSD, try decimal("12.4"))
    }

    func testCacheWriteUsesTheDocumentedOnePointTwoFiveInputMultiplier() throws {
        let estimate = estimator.estimate(
            modelID: "gpt-5.6-sol",
            usage: TokenUsage(
                inputTokens: 1_000_000,
                cachedInputTokens: 0,
                cacheWriteInputTokens: 1_000_000,
                outputTokens: 0
            ),
            occurredAt: now
        )
        let breakdown = try XCTUnwrap(estimate.breakdown)
        XCTAssertEqual(breakdown.uncachedInputUSD, 0)
        XCTAssertEqual(breakdown.cacheWriteInputUSD, try decimal("5"))
        XCTAssertEqual(estimate.amountUSD, try decimal("5"))
    }

    func testMissingCacheWriteMetadataDoesNotSilentlyBecomeZero() {
        let estimate = estimator.estimate(
            modelID: "gpt-5.6-sol",
            usage: TokenUsage(inputTokens: 10, cachedInputTokens: 2, outputTokens: 1),
            occurredAt: now
        )

        XCTAssertEqual(estimate.coverage, .incompleteMetadata(modelIDs: ["gpt-5.6-sol"]))
        XCTAssertNil(estimate.amountUSD)
    }

    func testMissingCacheWriteMetadataIsSafeWhenModelHasNoSeparateWritePrice() throws {
        let estimate = estimator.estimate(
            modelID: "gpt-5.5",
            usage: TokenUsage(inputTokens: 10, cachedInputTokens: 2, outputTokens: 1),
            occurredAt: now
        )

        XCTAssertEqual(estimate.coverage, .complete)
        XCTAssertEqual(estimate.amountUSD, try decimal("0.000071"))
    }

    func testHighContextRequestUsesOfficialInputAndOutputMultipliers() throws {
        let usage = TokenUsage(
            inputTokens: 300_000,
            cachedInputTokens: 0,
            cacheWriteInputTokens: 0,
            outputTokens: 100_000
        )
        let estimate = estimator.estimate([
            ModelTokenUsageSample(
                modelID: "gpt-5.6-sol",
                usage: usage,
                highContextUsage: usage,
                occurredAt: now
            )
        ])

        XCTAssertEqual(estimate.amountUSD, try decimal("5.4"))
        XCTAssertEqual(estimate.breakdown?.uncachedInputUSD, try decimal("2.4"))
        XCTAssertEqual(estimate.breakdown?.outputUSD, try decimal("3"))
    }

    func testUnknownRequestBoundaryMakesHighContextCapableModelIncomplete() {
        let usage = TokenUsage(
            inputTokens: 300_000,
            cachedInputTokens: 0,
            cacheWriteInputTokens: 0,
            outputTokens: 1
        )
        let estimate = estimator.estimate([
            ModelTokenUsageSample(
                modelID: "gpt-5.6-sol",
                usage: usage,
                hasUnknownPricingContext: true,
                occurredAt: now
            )
        ])

        XCTAssertEqual(estimate.coverage, .incompleteMetadata(modelIDs: ["gpt-5.6-sol"]))
        XCTAssertNil(estimate.amountUSD)
    }

    func testUnknownModelMakesTheWholeEstimateUnavailable() {
        let estimate = estimator.estimate(
            [
                ModelTokenUsageSample(
                    modelID: "gpt-5.6-sol",
                    usage: TokenUsage(
                        inputTokens: 1_000_000,
                        cachedInputTokens: 0,
                        cacheWriteInputTokens: 0,
                        outputTokens: 0
                    ),
                    occurredAt: now
                ),
                ModelTokenUsageSample(
                    modelID: "future-model",
                    usage: TokenUsage(
                        inputTokens: 1_000_000,
                        cachedInputTokens: 0,
                        cacheWriteInputTokens: 0,
                        outputTokens: 0
                    ),
                    occurredAt: now
                )
            ]
        )

        XCTAssertFalse(estimate.isAvailable)
        XCTAssertNil(estimate.amountUSD)
        XCTAssertNil(estimate.breakdown)
        XCTAssertEqual(estimate.coverage, .unavailable(missingModelIDs: ["future-model"]))
    }

    func testUnknownModelsAreDeduplicatedAndSortedWithoutAPartialDollarAmount() {
        let estimate = estimator.estimate(
            [
                sample(modelID: "Z-model", input: 1),
                sample(modelID: "a-model", input: 1),
                sample(modelID: " z-MODEL ", input: 1)
            ]
        )

        XCTAssertEqual(
            estimate.coverage,
            .unavailable(missingModelIDs: ["a-model", "z-model"])
        )
        XCTAssertNil(estimate.amountUSD)
        XCTAssertNil(estimate.breakdown)
    }

    func testHugeTokenCountsRemainDecimalAndDoNotOverflow() throws {
        let usage = TokenUsage(
            inputTokens: Int64.max,
            cachedInputTokens: Int64.max - 1,
            cacheWriteInputTokens: 0,
            outputTokens: Int64.max
        )
        let estimate = estimator.estimate(modelID: "gpt-5.6-sol", usage: usage, occurredAt: now)
        let breakdown = try XCTUnwrap(estimate.breakdown)
        let million = Decimal(1_000_000)

        XCTAssertEqual(breakdown.uncachedInputUSD, Decimal(4) / million)
        XCTAssertEqual(
            breakdown.cachedInputUSD,
            Decimal(Int64.max - 1) * (try decimal("0.4")) / million
        )
        XCTAssertEqual(
            breakdown.outputUSD,
            Decimal(Int64.max) * Decimal(20) / million
        )
        XCTAssertEqual(estimate.amountUSD, breakdown.totalUSD)
    }

    func testHistoricalUsageIsExplicitlyEstimatedWithTheCurrentCatalogSnapshot() throws {
        let historicalDate = try XCTUnwrap(
            Calendar(identifier: .gregorian).date(
                from: DateComponents(
                    timeZone: TimeZone(secondsFromGMT: 0),
                    year: 2024,
                    month: 1,
                    day: 1
                )
            )
        )
        let estimate = estimator.estimate(
            modelID: "gpt-5.4",
            usage: TokenUsage(
                inputTokens: 1_000_000,
                cachedInputTokens: 0,
                cacheWriteInputTokens: 0,
                outputTokens: 0
            ),
            occurredAt: historicalDate
        )

        XCTAssertEqual(estimate.amountUSD, try decimal("2.5"))
        XCTAssertTrue(estimate.pricingBasis.isCurrentPricingEstimate)
        XCTAssertEqual(estimate.pricingBasis.catalogVersion, "2026-09-14")
        XCTAssertEqual(
            estimate.pricingBasis.catalogRetrievedAt,
            Date(timeIntervalSince1970: 1_789_344_000)
        )
    }

    func testInvalidTokenUsageDoesNotProduceAnAmount() {
        let invalid = TokenUsage(
            inputTokens: 1,
            cachedInputTokens: 2,
            cacheWriteInputTokens: 0,
            outputTokens: 0
        )
        let estimate = estimator.estimate(modelID: "gpt-5.6-sol", usage: invalid, occurredAt: now)

        XCTAssertEqual(estimate.coverage, .invalidTokenUsage)
        XCTAssertNil(estimate.amountUSD)
        XCTAssertNil(estimate.breakdown)
    }

    private func sample(modelID: String, input: Int64) -> ModelTokenUsageSample {
        ModelTokenUsageSample(
            modelID: modelID,
            usage: TokenUsage(
                inputTokens: input,
                cachedInputTokens: 0,
                cacheWriteInputTokens: 0,
                outputTokens: 0
            ),
            occurredAt: now
        )
    }

    private func decimal(_ value: String) throws -> Decimal {
        try XCTUnwrap(Decimal(string: value, locale: Locale(identifier: "en_US_POSIX")))
    }
}
