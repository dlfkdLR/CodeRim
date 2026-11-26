import CodeRimUsageCore
import Foundation

let identifiers: [String?] = [nil, "unknown", "GPT-5.6", "gpt-5.6-sol", " gpt-6-astra ", "claude-opus-4-6",
    "gpt-5.2", "aliased 한글", "A\u{030A}", "Å", "", "gpt-5-mini", "gpt-6-astra", "gpt-6-astra\0"]
let through = Date(timeIntervalSince1970: 1_750_000_000)
let qualities: [DataQuality] = [.exact, .partial, .stale, .error, .unavailable]

func rows(_ count: Int, seed: Int) -> [ModelUsageSummary] {
    (0..<count).map { i in
        let key = seed + i * 11
        let n: Int64 = switch key % 13 { case 0: 0; case 1: -4; case 2: .max; default: 10_005 + Int64(key) }
        let usage = TokenUsage(inputTokens: n, cachedInputTokens: key.isMultiple(of: 9) ? -1 : max(0, n) / 4,
                               cacheWriteInputTokens: key.isMultiple(of: 3) ? nil : max(0, n) / 8,
                               outputTokens: key.isMultiple(of: 7) ? .max : max(0, n) / 2)
        let high = TokenUsage(inputTokens: key.isMultiple(of: 4) ? max(0, n) / 2 : 0, cachedInputTokens: 0,
                              cacheWriteInputTokens: key.isMultiple(of: 8) ? nil : 0,
                              outputTokens: key.isMultiple(of: 10) ? 12 : 0)
        return ModelUsageSummary(modelID: identifiers[key % identifiers.count], usage: usage,
                                 highContextUsage: high, hasUnknownPricingContext: key.isMultiple(of: 6))
    }
}

func emit(_ summary: CostDisplaySummary) {
    let amount = summary.amountUSD.map { NSDecimalNumber(decimal: $0).stringValue } ?? "nil"
    // Include a per-row token value so reordering two entries with the same ID is detectable.
    let excluded = summary.excludedModels.map { [String(reflecting: $0.modelID), String($0.usage.inputTokens)].joined(separator: "/") }
    print(amount, summary.isPartial, excluded)
}

for quality in qualities {
    for seed in 0..<20 {
        let models = rows(seed, seed: seed + 1)
        for forced in [Set<String>(), ["unknown", "GPT-5.6", "Å"]] {
            emit(CostDisplaySummary(models: models, through: through, quality: quality, excludingModelIDs: forced))
        }
        let buckets = (0..<(seed % 7)).map { day in
            UsageBucket(start: through.addingTimeInterval(Double(day) - 1), end: through.addingTimeInterval(Double(day)),
                        models: rows((day + seed) % 17, seed: 21 + day))
        }
        let snapshot = AnalyticsSnapshot(range: .thirtyDays, interval: .init(start: through, end: through),
                                        through: through, usage: .zero, quality: quality, buckets: buckets,
                                        models: models, projects: [], sessions: [])
        let chart = CostChartSummary(snapshot: snapshot)
        emit(chart.total)
        print(chart.isUnavailable, chart.hasUnavailableIntervals)
        for bucket in chart.buckets { emit(bucket.cost) }
    }
}

// Distinct accepted spellings exercise both sides of the bounded rate cache.
for count in [255, 256, 257, 300] {
    let aliases = (0..<count).map { i in
        ModelUsageSummary(modelID: String(repeating: " ", count: i) + "gpt-5.6",
                          usage: TokenUsage(inputTokens: Int64(i + 1), cachedInputTokens: 0,
                                            cacheWriteInputTokens: 0, outputTokens: 1))
    }
    let sample = AnalyticsSnapshot(range: .thirtyDays, interval: .init(start: through, end: through), through: through,
                                  usage: .zero, quality: .partial,
                                  buckets: [UsageBucket(start: through, end: through, models: Array(aliases.reversed()))],
                                  models: aliases, projects: [], sessions: [])
    let chart = CostChartSummary(snapshot: sample)
    emit(chart.total)
    emit(chart.buckets[0].cost)
}
