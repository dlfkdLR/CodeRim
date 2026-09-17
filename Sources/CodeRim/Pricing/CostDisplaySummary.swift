import Foundation

/// A displayable subtotal with explicit omissions. The billing estimator stays
/// all-or-nothing; callers must not present this subtotal as a complete cost.
struct CostDisplaySummary: Equatable, Sendable {
    let amountUSD: Decimal?
    let excludedModels: [ModelUsageSummary]

    var isPartial: Bool { !excludedModels.isEmpty }
    var excludedModelIDs: Set<String> { Set(excludedModels.map(\.id)) }
    var excludedTokens: Int64 {
        excludedModels.reduce(TokenUsage.zero) { $0.adding($1.usage) }.totalTokens
    }
    var exclusionDescription: String {
        "Excludes \(excludedTokens.formatted()) tokens · \(excludedModels.map(\.displayName).sorted().joined(separator: ", "))"
    }

    init(
        models: [ModelUsageSummary],
        through: Date,
        quality: DataQuality,
        excludingModelIDs: Set<String> = []
    ) {
        guard quality != .unavailable else {
            amountUSD = nil
            excludedModels = []
            return
        }

        var subtotal = Decimal.zero
        var pricedCount = 0
        var excluded: [ModelUsageSummary] = []
        for model in models where !model.usage.isZero {
            if !excludingModelIDs.contains(model.id),
               let amount = estimatedCost(for: [model], through: through, quality: quality) {
                subtotal += amount
                pricedCount += 1
            } else {
                excluded.append(model)
            }
        }
        amountUSD = pricedCount > 0 || excluded.isEmpty ? subtotal : nil
        excludedModels = excluded
    }
}

struct CostChartBucket: Identifiable, Sendable {
    let bucket: UsageBucket
    let cost: CostDisplaySummary
    var id: Date { bucket.start }
}

struct CostChartSummary: Sendable {
    let total: CostDisplaySummary
    let buckets: [CostChartBucket]

    var isUnavailable: Bool { total.amountUSD == nil }
    var hasUnavailableIntervals: Bool { buckets.contains { $0.cost.amountUSD == nil } }

    init(snapshot: AnalyticsSnapshot) {
        let total = CostDisplaySummary(models: snapshot.models, through: snapshot.through, quality: snapshot.quality)
        self.total = total
        // Use the same model coverage in the chart and the range subtotal.
        // Otherwise a model with incomplete metadata in one interval could
        // appear in other bars while being excluded from the displayed subtotal.
        buckets = snapshot.buckets.map { bucket in
            CostChartBucket(
                bucket: bucket,
                cost: CostDisplaySummary(
                    models: bucket.models,
                    through: bucket.end,
                    quality: snapshot.quality,
                    excludingModelIDs: total.excludedModelIDs
                )
            )
        }
    }
}
