#if CODERIM_RUST_ACCOUNTING
import CCodeRimApplePricing
import CodeRimUsageCore
import Foundation

enum RustCostEstimator {
    static func estimate(_ samples: [ModelTokenUsageSample], catalog: PricingCatalog,
                         basis: CostPricingBasis, normalizeMissing: (String) -> String) -> CostEstimate {
        func unavailable(_ coverage: CostCoverage) -> CostEstimate {
            CostEstimate(amountUSD: nil, breakdown: nil, coverage: coverage, pricingBasis: basis)
        }

        var total = cr_price_breakdown(uncached_input: 0, cached_input: 0, written_input: 0, output: 0)
        var missing = Set<String>()
        var incomplete = Set<String>()
        var preparedByModel = [String: cr_prepared_rates]()
        for sample in samples {
            let rates: cr_prepared_rates
            if let price = catalog.pricing(for: sample.modelID) {
                if let cached = preparedByModel[price.modelID] {
                    rates = cached
                } else {
                    rates = prepare(price)
                    preparedByModel[price.modelID] = rates
                }
            } else {
                rates = cr_prepared_rates()
            }
            let result = cr_apple_price_sample(pack(sample.usage), pack(sample.highContextUsage),
                                              sample.hasUnknownPricingContext ? 1 : 0, rates, total) {
                Decimal($0)
            }
            switch result.status {
            case 0: total = result.accumulated
            case 1: return unavailable(.invalidTokenUsage)
            case 2: missing.insert(normalizeMissing(sample.modelID))
            case 3: incomplete.insert(normalizeMissing(sample.modelID))
            default: preconditionFailure("Unexpected Rust pricing status")
            }
        }
        if !missing.isEmpty { return unavailable(.unavailable(missingModelIDs: missing.sorted())) }
        if !incomplete.isEmpty { return unavailable(.incompleteMetadata(modelIDs: incomplete.sorted())) }

        let breakdown = CostBreakdown(uncachedInputUSD: total.uncached_input, cachedInputUSD: total.cached_input,
                                      cacheWriteInputUSD: total.written_input, outputUSD: total.output)
        return CostEstimate(amountUSD: cr_apple_price_total(total), breakdown: breakdown, coverage: .complete, pricingBasis: basis)
    }

    private static func prepare(_ price: ModelPricing) -> cr_prepared_rates {
        let rates = cr_pricing_rates(
            input: price.inputUSDPerMillionTokens, cached: price.cachedInputUSDPerMillionTokens,
            output: price.outputUSDPerMillionTokens, write_multiplier: price.cacheWriteInputMultiplier ?? 1,
            high_input_multiplier: price.highContextInputMultiplier ?? 1,
            high_output_multiplier: price.highContextOutputMultiplier ?? 1,
            has_write_multiplier: price.cacheWriteInputMultiplier == nil ? 0 : 1,
            has_high_input_multiplier: price.highContextInputMultiplier == nil ? 0 : 1
        )
        return cr_apple_prepare_rates(rates) { Decimal($0) }
    }

    private static func pack(_ usage: TokenUsage) -> cr_usage {
        cr_usage(input: usage.inputTokens, cached: usage.cachedInputTokens, output: usage.outputTokens,
                 written: usage.cacheWriteInputTokens ?? 0, has_written: usage.cacheWriteInputTokens == nil ? 0 : 1)
    }
}
#endif
