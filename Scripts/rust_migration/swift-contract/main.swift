import Foundation

// Both binaries use the application's normal entry point. The compile definition
// selects the implementation, so the driver never calls a Rust symbol itself.
struct Scenario: Decodable {
    let name: String
    let steps: [Step]
}

struct Step: Decodable {
    let occurredAt: Double
    let lastUsage: TokenUsage?
    let cumulativeUsage: TokenUsage?
    let expected: Expected
}

struct Expected: Decodable {
    let delta: TokenUsage?
    let cumulativeHighWaterMark: TokenUsage?
    let lastObservedAt: Double?
    let quality: DataQuality
    let diagnostic: String?
}

struct Row: Encodable {
    let scenario: String
    let step: Int
    let highWater: TokenUsage?
    let dateBits: UInt64?
    let quality: DataQuality
    let delta: TokenUsage?
    let diagnostic: String?
}

let fixtures = try JSONDecoder().decode([Scenario].self, from: Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1])))
let normalizer = UsageNormalizer()
var rows: [Row] = []

func record(_ name: String, _ step: Int, _ result: UsageNormalizationResult) {
    rows.append(Row(scenario: name, step: step,
                    highWater: result.state.cumulativeHighWaterMark,
                    dateBits: result.state.lastObservedAt?.timeIntervalSinceReferenceDate.bitPattern,
                    quality: result.state.quality, delta: result.delta, diagnostic: result.diagnostic))
}

for fixture in fixtures {
    var state = UsageNormalizationState.empty
    for (index, step) in fixture.steps.enumerated() {
        let result = normalizer.normalize(
            CodexTokenObservation(occurredAt: Date(timeIntervalSince1970: step.occurredAt), ordinal: Int64(index),
                                  lastUsage: step.lastUsage, cumulativeUsage: step.cumulativeUsage),
            state: state)
        let expected = UsageNormalizationResult(delta: step.expected.delta, state: UsageNormalizationState(
            cumulativeHighWaterMark: step.expected.cumulativeHighWaterMark,
            lastObservedAt: step.expected.lastObservedAt.map { Date(timeIntervalSince1970: $0) },
            quality: step.expected.quality), diagnostic: step.expected.diagnostic)
        precondition(result == expected, "\(fixture.name), transition \(index)")
        record(fixture.name, index, result)
        state = result.state
    }
}

// Check the host boundary for nullable counts, all persisted quality values,
// dates that lose precision in a Unix epoch conversion, and 64-bit counters.
let qualities: [DataQuality] = [.exact, .partial, .stale, .unavailable, .error]
for quality in qualities {
    let values: [Int64?] = [nil, -1, 0, 7, .max]
    for (index, value) in values.enumerated() {
        let timestamp = Date(timeIntervalSinceReferenceDate: Double(index + 1) / 1_000_000_000)
        let cumulative = TokenUsage(inputTokens: Int64.max, cachedInputTokens: 0,
                                    cacheWriteInputTokens: value, outputTokens: Int64.max)
        let stable = UsageNormalizationState(cumulativeHighWaterMark: cumulative, lastObservedAt: timestamp, quality: quality)
        let old = CodexTokenObservation(occurredAt: timestamp.addingTimeInterval(-0.000_000_000_1),
                                         ordinal: nil, lastUsage: .zero, cumulativeUsage: nil)
        let ignored = normalizer.normalize(old, state: stable)
        precondition(ignored == UsageNormalizationResult(delta: nil, state: stable, diagnostic: "out-of-order token snapshot ignored"))
        record("older-\(quality.rawValue)", index, ignored)
        let initState = UsageNormalizationState(cumulativeHighWaterMark: nil, lastObservedAt: nil, quality: quality)
        let first = normalizer.normalize(CodexTokenObservation(occurredAt: timestamp, ordinal: nil, lastUsage: cumulative,
                                                               cumulativeUsage: cumulative), state: initState)
        let repaired = value == -1
        let expectedState = UsageNormalizationState(cumulativeHighWaterMark: cumulative, lastObservedAt: timestamp,
                                                    quality: repaired ? .partial : quality)
        precondition(first == UsageNormalizationResult(delta: repaired ? cumulative.clampedToValid : cumulative,
                                                       state: expectedState, diagnostic: repaired ? "inconsistent token usage clamped" : nil))
        record("initial-\(quality.rawValue)", index, first)
    }
}

#if CODERIM_RUST_ACCOUNTING
// The legacy unchecked subtraction traps on this damaged persisted counter.
// The candidate must recover a new baseline without inventing billable tokens.
let badWatermark = TokenUsage(inputTokens: .min, cachedInputTokens: 0, outputTokens: 0)
let corruptState = UsageNormalizationState(cumulativeHighWaterMark: badWatermark,
                                           lastObservedAt: Date(timeIntervalSinceReferenceDate: 0),
                                           quality: .exact)
let anchorDate = Date(timeIntervalSinceReferenceDate: 1.25)
let anchor = TokenUsage(inputTokens: 0, cachedInputTokens: 0, outputTokens: 0)
let reset = normalizer.normalize(CodexTokenObservation(occurredAt: anchorDate, ordinal: nil,
                                                       lastUsage: nil, cumulativeUsage: anchor), state: corruptState)
let resetState = UsageNormalizationState(cumulativeHighWaterMark: anchor, lastObservedAt: anchorDate, quality: .partial)
precondition(reset == UsageNormalizationResult(delta: nil, state: resetState,
                                               diagnostic: "cumulative token difference overflowed; baseline reset"))
let nextDate = Date(timeIntervalSinceReferenceDate: 2.5)
let nextUsage = TokenUsage(inputTokens: 20, cachedInputTokens: 7, outputTokens: 3)
let resumed = normalizer.normalize(CodexTokenObservation(occurredAt: nextDate, ordinal: nil,
                                                         lastUsage: nil, cumulativeUsage: nextUsage), state: reset.state)
let resumedState = UsageNormalizationState(cumulativeHighWaterMark: nextUsage,
                                           lastObservedAt: nextDate, quality: .partial)
precondition(resumed == UsageNormalizationResult(delta: nextUsage, state: resumedState, diagnostic: nil))
#endif

let encoder = JSONEncoder()
encoder.outputFormatting = [.sortedKeys]
FileHandle.standardOutput.write(try encoder.encode(rows))
