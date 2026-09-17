import Foundation

struct ModelTokenUsageSample: Equatable, Sendable {
    let modelID: String
    let usage: TokenUsage
    let highContextUsage: TokenUsage
    let hasUnknownPricingContext: Bool
    let occurredAt: Date

    init(
        modelID: String,
        usage: TokenUsage,
        highContextUsage: TokenUsage = .zero,
        hasUnknownPricingContext: Bool = false,
        occurredAt: Date
    ) {
        self.modelID = modelID
        self.usage = usage
        self.highContextUsage = highContextUsage
        self.hasUnknownPricingContext = hasUnknownPricingContext
        self.occurredAt = occurredAt
    }
}

struct CostBreakdown: Equatable, Sendable {
    let uncachedInputUSD: Decimal
    let cachedInputUSD: Decimal
    let cacheWriteInputUSD: Decimal
    let outputUSD: Decimal

    var totalUSD: Decimal {
        uncachedInputUSD + cachedInputUSD + cacheWriteInputUSD + outputUSD
    }

    static let zero = CostBreakdown(
        uncachedInputUSD: 0,
        cachedInputUSD: 0,
        cacheWriteInputUSD: 0,
        outputUSD: 0
    )

    func adding(_ other: CostBreakdown) -> CostBreakdown {
        CostBreakdown(
            uncachedInputUSD: uncachedInputUSD + other.uncachedInputUSD,
            cachedInputUSD: cachedInputUSD + other.cachedInputUSD,
            cacheWriteInputUSD: cacheWriteInputUSD + other.cacheWriteInputUSD,
            outputUSD: outputUSD + other.outputUSD
        )
    }
}

enum CostCoverage: Equatable, Sendable {
    case complete
    case unavailable(missingModelIDs: [String])
    case incompleteMetadata(modelIDs: [String])
    case invalidTokenUsage
}

struct CostPricingBasis: Equatable, Sendable {
    let catalogVersion: String
    let catalogRetrievedAt: Date

    var isCurrentPricingEstimate: Bool { true }
}

struct CostEstimate: Equatable, Sendable {
    let amountUSD: Decimal?
    let breakdown: CostBreakdown?
    let coverage: CostCoverage
    let pricingBasis: CostPricingBasis

    var isAvailable: Bool {
        coverage == .complete && amountUSD != nil && breakdown != nil
    }
}

struct CostEstimator: Sendable {
    private static let tokensPerMillion = Decimal(1_000_000)

    let catalog: PricingCatalog

    init(catalog: PricingCatalog = .current) {
        self.catalog = catalog
    }

    func estimate(modelID: String, usage: TokenUsage, occurredAt: Date) -> CostEstimate {
        estimate([
            ModelTokenUsageSample(
                modelID: modelID,
                usage: usage,
                occurredAt: occurredAt
            )
        ])
    }

    func estimate(_ samples: [ModelTokenUsageSample]) -> CostEstimate {
        let basis = CostPricingBasis(
            catalogVersion: catalog.metadata.version,
            catalogRetrievedAt: catalog.metadata.retrievedAt
        )

        guard samples.allSatisfy({
            $0.usage.isValid
                && $0.highContextUsage.isValid
                && ($0.highContextUsage.isZero || $0.usage.isComponentWiseAtLeast($0.highContextUsage))
        }) else {
            return CostEstimate(
                amountUSD: nil,
                breakdown: nil,
                coverage: .invalidTokenUsage,
                pricingBasis: basis
            )
        }

        let missingModelIDs = Set(
            samples.compactMap { sample in
                catalog.pricing(for: sample.modelID) == nil
                    ? normalizedMissingModelID(sample.modelID)
                    : nil
            }
        ).sorted()

        guard missingModelIDs.isEmpty else {
            return CostEstimate(
                amountUSD: nil,
                breakdown: nil,
                coverage: .unavailable(missingModelIDs: missingModelIDs),
                pricingBasis: basis
            )
        }


        let incompleteModelIDs = Set(
            samples.compactMap { sample -> String? in
                guard let price = catalog.pricing(for: sample.modelID) else { return nil }
                if price.cacheWriteInputMultiplier != nil,
                   sample.usage.cacheWriteInputTokens == nil,
                   sample.usage.inputTokens > 0 {
                    return normalizedMissingModelID(sample.modelID)
                }
                if sample.hasUnknownPricingContext,
                   price.highContextInputMultiplier != nil {
                    return normalizedMissingModelID(sample.modelID)
                }
                return nil
            }
        ).sorted()
        guard incompleteModelIDs.isEmpty else {
            return CostEstimate(
                amountUSD: nil,
                breakdown: nil,
                coverage: .incompleteMetadata(modelIDs: incompleteModelIDs),
                pricingBasis: basis
            )
        }

        let breakdown = samples.reduce(into: CostBreakdown.zero) { total, sample in
            guard let price = catalog.pricing(for: sample.modelID) else { return }
            let standardUsage = sample.usage.subtractingFloorAtZero(sample.highContextUsage)
            total = total.adding(cost(for: standardUsage, price: price))
            total = total.adding(
                cost(
                    for: sample.highContextUsage,
                    price: price,
                    inputMultiplier: price.highContextInputMultiplier ?? 1,
                    outputMultiplier: price.highContextOutputMultiplier ?? 1
                )
            )
        }
        return CostEstimate(
            amountUSD: breakdown.totalUSD,
            breakdown: breakdown,
            coverage: .complete,
            pricingBasis: basis
        )
    }

    private func cost(
        for usage: TokenUsage,
        price: ModelPricing,
        inputMultiplier: Decimal = 1,
        outputMultiplier: Decimal = 1
    ) -> CostBreakdown {
        let cacheWriteTokens = usage.cacheWriteInputTokens ?? 0
        let uncachedInputTokens = usage.inputTokens - usage.cachedInputTokens - cacheWriteTokens
        return CostBreakdown(
            uncachedInputUSD: scaledCost(
                tokens: uncachedInputTokens,
                usdPerMillionTokens: price.inputUSDPerMillionTokens * inputMultiplier
            ),
            cachedInputUSD: scaledCost(
                tokens: usage.cachedInputTokens,
                usdPerMillionTokens: price.cachedInputUSDPerMillionTokens * inputMultiplier
            ),
            cacheWriteInputUSD: scaledCost(
                tokens: cacheWriteTokens,
                usdPerMillionTokens: price.inputUSDPerMillionTokens
                    * (price.cacheWriteInputMultiplier ?? 1)
                    * inputMultiplier
            ),
            outputUSD: scaledCost(
                tokens: usage.outputTokens,
                usdPerMillionTokens: price.outputUSDPerMillionTokens * outputMultiplier
            )
        )
    }

    private func scaledCost(tokens: Int64, usdPerMillionTokens: Decimal) -> Decimal {
        Decimal(tokens) * usdPerMillionTokens / Self.tokensPerMillion
    }

    private func normalizedMissingModelID(_ value: String) -> String {
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return normalized.isEmpty ? "unknown" : normalized
    }
}
