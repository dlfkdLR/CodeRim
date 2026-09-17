import Foundation

struct ModelPricing: Equatable, Sendable {
    let modelID: String
    let inputUSDPerMillionTokens: Decimal
    let cachedInputUSDPerMillionTokens: Decimal
    let outputUSDPerMillionTokens: Decimal
    let cacheWriteInputMultiplier: Decimal?
    let highContextInputMultiplier: Decimal?
    let highContextOutputMultiplier: Decimal?
    let effectiveFrom: Date?
    let sourceURL: URL?

    init(
        modelID: String,
        inputUSDPerMillionTokens: Decimal,
        cachedInputUSDPerMillionTokens: Decimal,
        outputUSDPerMillionTokens: Decimal,
        cacheWriteInputMultiplier: Decimal? = nil,
        highContextInputMultiplier: Decimal? = nil,
        highContextOutputMultiplier: Decimal? = nil,
        effectiveFrom: Date? = nil,
        sourceURL: URL? = nil
    ) {
        self.modelID = modelID
        self.inputUSDPerMillionTokens = inputUSDPerMillionTokens
        self.cachedInputUSDPerMillionTokens = cachedInputUSDPerMillionTokens
        self.outputUSDPerMillionTokens = outputUSDPerMillionTokens
        self.cacheWriteInputMultiplier = cacheWriteInputMultiplier
        self.highContextInputMultiplier = highContextInputMultiplier
        self.highContextOutputMultiplier = highContextOutputMultiplier
        self.effectiveFrom = effectiveFrom
        self.sourceURL = sourceURL
    }
}

struct PricingCatalogMetadata: Equatable, Sendable {
    let version: String
    let retrievedAt: Date
}

enum PricingCatalogError: Error, Equatable {
    case invalidMetadata
    case invalidModelID
    case invalidPrice(modelID: String)
    case duplicateModelID(String)
}

struct PricingCatalog: Equatable, Sendable {
    static let current = makeCurrentCatalog()

    let metadata: PricingCatalogMetadata
    private let pricingByModelID: [String: ModelPricing]

    init(
        metadata: PricingCatalogMetadata,
        prices: [ModelPricing]
    ) throws {
        guard !metadata.version.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw PricingCatalogError.invalidMetadata
        }

        var indexed: [String: ModelPricing] = [:]
        indexed.reserveCapacity(prices.count)

        for price in prices {
            guard let key = Self.canonicalModelID(price.modelID) else {
                throw PricingCatalogError.invalidModelID
            }
            guard price.inputUSDPerMillionTokens >= 0,
                  price.cachedInputUSDPerMillionTokens >= 0,
                  price.outputUSDPerMillionTokens >= 0,
                  price.cacheWriteInputMultiplier.map({ $0 >= 0 }) ?? true,
                  price.highContextInputMultiplier.map({ $0 >= 0 }) ?? true,
                  price.highContextOutputMultiplier.map({ $0 >= 0 }) ?? true else {
                throw PricingCatalogError.invalidPrice(modelID: price.modelID)
            }
            guard indexed[key] == nil else {
                throw PricingCatalogError.duplicateModelID(key)
            }
            indexed[key] = ModelPricing(
                modelID: key,
                inputUSDPerMillionTokens: price.inputUSDPerMillionTokens,
                cachedInputUSDPerMillionTokens: price.cachedInputUSDPerMillionTokens,
                outputUSDPerMillionTokens: price.outputUSDPerMillionTokens,
                cacheWriteInputMultiplier: price.cacheWriteInputMultiplier,
                highContextInputMultiplier: price.highContextInputMultiplier,
                highContextOutputMultiplier: price.highContextOutputMultiplier,
                effectiveFrom: price.effectiveFrom,
                sourceURL: price.sourceURL
            )
        }

        self.metadata = metadata
        pricingByModelID = indexed
    }

    func pricing(for modelID: String) -> ModelPricing? {
        guard let key = Self.canonicalModelID(modelID) else { return nil }
        return pricingByModelID[key == "gpt-5.6" ? "gpt-5.6-sol" : key]
    }

    var supportedModelIDs: [String] {
        pricingByModelID.keys.sorted()
    }

    private static func canonicalModelID(_ value: String) -> String? {
        let canonical = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !canonical.isEmpty,
              canonical.utf8.count <= 256,
              !canonical.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains)
        else { return nil }
        return canonical
    }

    private static func makeCurrentCatalog() -> PricingCatalog {
        let metadata = PricingCatalogMetadata(
            version: "2026-09-14",
            retrievedAt: Date(timeIntervalSince1970: 1_789_344_000)
        )
        let prices = [
            ModelPricing(
                modelID: "gpt-6-astra",
                inputUSDPerMillionTokens: decimal(10),
                cachedInputUSDPerMillionTokens: decimal(1),
                outputUSDPerMillionTokens: decimal(50),
                cacheWriteInputMultiplier: decimal(125, scale: 2),
                highContextInputMultiplier: decimal(2),
                highContextOutputMultiplier: decimal(15, scale: 1),
                sourceURL: modelURL("gpt-6-astra")
            ),
            ModelPricing(
                modelID: "gpt-5.6-sol",
                inputUSDPerMillionTokens: decimal(4),
                cachedInputUSDPerMillionTokens: decimal(4, scale: 1),
                outputUSDPerMillionTokens: decimal(20),
                cacheWriteInputMultiplier: decimal(125, scale: 2),
                highContextInputMultiplier: decimal(2),
                highContextOutputMultiplier: decimal(15, scale: 1),
                sourceURL: modelURL("gpt-5.6-sol")
            ),
            ModelPricing(
                modelID: "gpt-5.6-terra",
                inputUSDPerMillionTokens: decimal(2),
                cachedInputUSDPerMillionTokens: decimal(2, scale: 1),
                outputUSDPerMillionTokens: decimal(12),
                cacheWriteInputMultiplier: decimal(125, scale: 2),
                highContextInputMultiplier: decimal(2),
                highContextOutputMultiplier: decimal(15, scale: 1),
                sourceURL: modelURL("gpt-5.6-terra")
            ),
            ModelPricing(
                modelID: "gpt-5.6-luna",
                inputUSDPerMillionTokens: decimal(2, scale: 1),
                cachedInputUSDPerMillionTokens: decimal(2, scale: 2),
                outputUSDPerMillionTokens: decimal(12, scale: 1),
                cacheWriteInputMultiplier: decimal(125, scale: 2),
                highContextInputMultiplier: decimal(2),
                highContextOutputMultiplier: decimal(15, scale: 1),
                sourceURL: modelURL("gpt-5.6-luna")
            ),
            ModelPricing(
                modelID: "gpt-5.5",
                inputUSDPerMillionTokens: decimal(5),
                cachedInputUSDPerMillionTokens: decimal(5, scale: 1),
                outputUSDPerMillionTokens: decimal(30),
                highContextInputMultiplier: decimal(2),
                highContextOutputMultiplier: decimal(15, scale: 1),
                sourceURL: modelURL("gpt-5.5")
            ),
            ModelPricing(
                modelID: "gpt-5.4",
                inputUSDPerMillionTokens: decimal(25, scale: 1),
                cachedInputUSDPerMillionTokens: decimal(25, scale: 2),
                outputUSDPerMillionTokens: decimal(15),
                highContextInputMultiplier: decimal(2),
                highContextOutputMultiplier: decimal(15, scale: 1),
                sourceURL: modelURL("gpt-5.4")
            ),
            ModelPricing(
                modelID: "gpt-5.4-mini",
                inputUSDPerMillionTokens: decimal(75, scale: 2),
                cachedInputUSDPerMillionTokens: decimal(75, scale: 3),
                outputUSDPerMillionTokens: decimal(45, scale: 1),
                sourceURL: modelURL("gpt-5.4-mini")
            ),
            ModelPricing(
                modelID: "gpt-5.3-codex",
                inputUSDPerMillionTokens: decimal(175, scale: 2),
                cachedInputUSDPerMillionTokens: decimal(175, scale: 3),
                outputUSDPerMillionTokens: decimal(14),
                sourceURL: modelURL("gpt-5.3-codex")
            )
        ]

        do {
            return try PricingCatalog(metadata: metadata, prices: prices)
        } catch {
            preconditionFailure("The bundled pricing catalog is invalid: \(error)")
        }
    }

    private static func decimal(_ significand: Int, scale: Int = 0) -> Decimal {
        Decimal(
            sign: .plus,
            exponent: -scale,
            significand: Decimal(significand)
        )
    }

    private static func modelURL(_ modelID: String) -> URL? {
        URL(string: "https://developers.openai.com/api/docs/models/\(modelID)")
    }
}
