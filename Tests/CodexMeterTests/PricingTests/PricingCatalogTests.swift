import Foundation
import XCTest
@testable import CodexMeter

final class PricingCatalogTests: XCTestCase {
    func testCurrentCatalogContainsOfficialPerMillionPrices() throws {
        let catalog = PricingCatalog.current

        try assertPrice(catalog, "gpt-6-astra", input: "10", cached: "1", output: "50")
        try assertPrice(catalog, "gpt-5.6-sol", input: "4", cached: "0.4", output: "20")
        try assertPrice(catalog, "gpt-5.6-terra", input: "2", cached: "0.2", output: "12")
        try assertPrice(catalog, "gpt-5.6-luna", input: "0.2", cached: "0.02", output: "1.2")
        try assertPrice(catalog, "gpt-5.5", input: "5", cached: "0.5", output: "30")
        try assertPrice(catalog, "gpt-5.4", input: "2.5", cached: "0.25", output: "15")
        try assertPrice(catalog, "gpt-5.4-mini", input: "0.75", cached: "0.075", output: "4.5")
        try assertPrice(catalog, "gpt-5.3-codex", input: "1.75", cached: "0.175", output: "14")

        let sol = try XCTUnwrap(catalog.pricing(for: "gpt-5.6-sol"))
        XCTAssertEqual(sol.cacheWriteInputMultiplier, try decimal("1.25"))
        XCTAssertEqual(sol.highContextInputMultiplier, try decimal("2"))
        XCTAssertEqual(sol.highContextOutputMultiplier, try decimal("1.5"))

        let astra = try XCTUnwrap(catalog.pricing(for: "gpt-6-astra"))
        XCTAssertEqual(astra.cacheWriteInputMultiplier, try decimal("1.25"))
        XCTAssertEqual(astra.highContextInputMultiplier, try decimal("2"))
        XCTAssertEqual(astra.highContextOutputMultiplier, try decimal("1.5"))
        XCTAssertEqual(astra.sourceURL?.absoluteString, "https://developers.openai.com/api/docs/models/gpt-6-astra")

        XCTAssertEqual(catalog.metadata.version, "2026-09-14")
        XCTAssertEqual(catalog.metadata.retrievedAt, Date(timeIntervalSince1970: 1_789_344_000))
    }

    func testLookupCanonicalizesOnlyCaseAndSurroundingWhitespace() throws {
        let price = try XCTUnwrap(PricingCatalog.current.pricing(for: "  GPT-5.6-SOL \n"))
        XCTAssertEqual(price.modelID, "gpt-5.6-sol")
        XCTAssertNil(PricingCatalog.current.pricing(for: "gpt-5.6-sol-unknown-suffix"))
    }

    func testCatalogRejectsDuplicateOrNegativePrices() throws {
        let metadata = PricingCatalogMetadata(version: "test", retrievedAt: Date())
        let valid = ModelPricing(
            modelID: "model",
            inputUSDPerMillionTokens: 1,
            cachedInputUSDPerMillionTokens: 1,
            outputUSDPerMillionTokens: 1
        )
        XCTAssertThrowsError(try PricingCatalog(metadata: metadata, prices: [valid, valid])) { error in
            XCTAssertEqual(error as? PricingCatalogError, .duplicateModelID("model"))
        }

        let invalid = ModelPricing(
            modelID: "negative",
            inputUSDPerMillionTokens: -1,
            cachedInputUSDPerMillionTokens: 0,
            outputUSDPerMillionTokens: 0
        )
        XCTAssertThrowsError(try PricingCatalog(metadata: metadata, prices: [invalid])) { error in
            XCTAssertEqual(error as? PricingCatalogError, .invalidPrice(modelID: "negative"))
        }

        let invalidMultiplier = ModelPricing(
            modelID: "negative-multiplier",
            inputUSDPerMillionTokens: 1,
            cachedInputUSDPerMillionTokens: 0,
            outputUSDPerMillionTokens: 1,
            cacheWriteInputMultiplier: -1
        )
        XCTAssertThrowsError(try PricingCatalog(metadata: metadata, prices: [invalidMultiplier])) { error in
            XCTAssertEqual(error as? PricingCatalogError, .invalidPrice(modelID: "negative-multiplier"))
        }
    }

    private func assertPrice(
        _ catalog: PricingCatalog,
        _ modelID: String,
        input: String,
        cached: String,
        output: String
    ) throws {
        let price = try XCTUnwrap(catalog.pricing(for: modelID))
        XCTAssertEqual(price.inputUSDPerMillionTokens, try decimal(input))
        XCTAssertEqual(price.cachedInputUSDPerMillionTokens, try decimal(cached))
        XCTAssertEqual(price.outputUSDPerMillionTokens, try decimal(output))
    }

    private func decimal(_ value: String) throws -> Decimal {
        try XCTUnwrap(Decimal(string: value, locale: Locale(identifier: "en_US_POSIX")))
    }
}
