#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
import Foundation

enum RustUsageNormalizer {
    static func normalize(
        _ observation: CodexTokenObservation,
        state: UsageNormalizationState
    ) -> UsageNormalizationResult {
        let highWater = pack(state.cumulativeHighWaterMark)
        let last = pack(observation.lastUsage)
        let cumulative = pack(observation.cumulativeUsage)
        let priorDate: (at: Double, present: Int64)
        if let date = state.lastObservedAt {
            priorDate = (date.timeIntervalSinceReferenceDate, 1)
        } else {
            priorDate = (0, 0)
        }
        let previous = cr_state(
            high_water: highWater.raw,
            at: priorDate.at,
            has_high_water: highWater.present,
            has_at: priorDate.present,
            quality: qualityCode(state.quality)
        )
        let sample = cr_observation(
            cumulative: cumulative.raw,
            last: last.raw,
            at: observation.occurredAt.timeIntervalSinceReferenceDate,
            has_cumulative: cumulative.present,
            has_last: last.present
        )
        let result = cr_normalize_apple(previous, sample)
        guard let quality = decodeQuality(result.state.quality),
              diagnostics.indices.contains(Int(result.diagnostic)) else {
            var failed = state
            failed.quality = .error
            return UsageNormalizationResult(delta: nil, state: failed,
                                            diagnostic: "unknown accounting result")
        }
        return UsageNormalizationResult(
            delta: result.has_delta == 0 ? nil : unpack(result.delta),
            state: UsageNormalizationState(
                cumulativeHighWaterMark: result.state.has_high_water == 0 ? nil : unpack(result.state.high_water),
                lastObservedAt: result.state.has_at == 0 ? nil : Date(timeIntervalSinceReferenceDate: result.state.at),
                quality: quality
            ),
            diagnostic: diagnostics[Int(result.diagnostic)]
        )
    }

    private static func pack(_ usage: TokenUsage?) -> (raw: cr_usage, present: Int64) {
        guard let usage else { return (cr_usage(), 0) }
        var raw = cr_usage(input: usage.inputTokens, cached: usage.cachedInputTokens,
                           output: usage.outputTokens, written: 0, has_written: 0)
        if let written = usage.cacheWriteInputTokens {
            raw.written = written
            raw.has_written = 1
        }
        return (raw, 1)
    }

    private static func unpack(_ usage: cr_usage) -> TokenUsage {
        TokenUsage(inputTokens: usage.input, cachedInputTokens: usage.cached,
                   cacheWriteInputTokens: usage.has_written == 0 ? nil : usage.written,
                   outputTokens: usage.output)
    }

    private static func qualityCode(_ quality: DataQuality) -> Int64 {
        switch quality {
        case .exact: 0
        case .partial: 1
        case .stale: 2
        case .unavailable: 3
        case .error: 4
        }
    }

    private static func decodeQuality(_ value: Int64) -> DataQuality? {
        switch value {
        case 0: .exact
        case 1: .partial
        case 2: .stale
        case 3: .unavailable
        case 4: .error
        default: nil
        }
    }

    private static let diagnostics: [String?] = [
        nil,
        "out-of-order token snapshot ignored",
        "missing cumulative token usage",
        "initial cumulative baseline is unresolved",
        "cumulative counter restarted",
        "cumulative token usage decreased or interleaved",
        "inconsistent token delta clamped",
        "inconsistent token usage clamped",
        "cumulative token difference overflowed; baseline reset",
    ]
}
#endif
