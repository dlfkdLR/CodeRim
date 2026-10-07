import Foundation

struct UsageNormalizationState: Equatable, Sendable {
    var cumulativeHighWaterMark: TokenUsage?
    var lastObservedAt: Date?
    var quality: DataQuality

    init(
        cumulativeHighWaterMark: TokenUsage?,
        lastObservedAt: Date? = nil,
        quality: DataQuality
    ) {
        self.cumulativeHighWaterMark = cumulativeHighWaterMark
        self.lastObservedAt = lastObservedAt
        self.quality = quality
    }

    static let empty = UsageNormalizationState(
        cumulativeHighWaterMark: nil,
        lastObservedAt: nil,
        quality: .exact
    )
}

struct UsageNormalizationResult: Equatable, Sendable {
    let delta: TokenUsage?
    let state: UsageNormalizationState
    let diagnostic: String?
}

struct UsageNormalizer: Sendable {
    func normalize(
        _ observation: CodexTokenObservation,
        metadata: SessionMetadata?,
        state: UsageNormalizationState
    ) -> UsageNormalizationResult {
        if let lastObservedAt = state.lastObservedAt,
           observation.occurredAt < lastObservedAt {
            return UsageNormalizationResult(
                delta: nil,
                state: state,
                diagnostic: "out-of-order token snapshot ignored"
            )
        }

        guard let cumulative = observation.cumulativeUsage else {
            return UsageNormalizationResult(
                delta: nil,
                state: UsageNormalizationState(
                    cumulativeHighWaterMark: state.cumulativeHighWaterMark,
                    lastObservedAt: state.lastObservedAt,
                    quality: .partial
                ),
                diagnostic: "missing cumulative token usage"
            )
        }

        guard let previous = state.cumulativeHighWaterMark else {
            guard observation.lastUsage == cumulative else {
                return UsageNormalizationResult(
                    delta: nil,
                    state: UsageNormalizationState(
                        cumulativeHighWaterMark: cumulative,
                        lastObservedAt: observation.occurredAt,
                        quality: .partial
                    ),
                    diagnostic: "initial cumulative baseline is unresolved"
                )
            }

            let initial = storable(cumulative)
            return UsageNormalizationResult(
                delta: initial.usage,
                state: UsageNormalizationState(
                    cumulativeHighWaterMark: cumulative,
                    lastObservedAt: observation.occurredAt,
                    quality: initial.repaired ? .partial : state.quality
                ),
                diagnostic: initial.repaired ? "inconsistent token usage clamped" : nil
            )
        }

        guard !cumulative.hasCounterDecrease(comparedTo: previous) else {
            if observation.lastUsage == cumulative {
                return UsageNormalizationResult(
                    delta: storable(cumulative).usage,
                    state: UsageNormalizationState(
                        cumulativeHighWaterMark: cumulative,
                        lastObservedAt: observation.occurredAt,
                        quality: .partial
                    ),
                    diagnostic: "cumulative counter restarted"
                )
            }

            return UsageNormalizationResult(
                delta: nil,
                state: UsageNormalizationState(
                    cumulativeHighWaterMark: previous.componentWiseMaximum(with: cumulative),
                    lastObservedAt: observation.occurredAt,
                    quality: .partial
                ),
                diagnostic: "cumulative token usage decreased or interleaved"
            )
        }

        let delta = cumulative.subtractingFloorAtZero(previous)
        guard delta.isValid else {
            // Each counter grew, but not consistently (e.g. cached input grew more than input).
            // Keep the tokens that are certain instead of a row storage rejects, and say the period is partial.
            let repaired = delta.clampedToValid
            return UsageNormalizationResult(
                delta: repaired.isZero ? nil : repaired,
                state: UsageNormalizationState(
                    cumulativeHighWaterMark: cumulative,
                    lastObservedAt: observation.occurredAt,
                    quality: .partial
                ),
                diagnostic: "inconsistent token delta clamped"
            )
        }
        return UsageNormalizationResult(
            delta: delta.isZero ? nil : delta,
            state: UsageNormalizationState(
                cumulativeHighWaterMark: cumulative,
                lastObservedAt: observation.occurredAt,
                quality: state.quality
            ),
            diagnostic: nil
        )
    }

    /// A cumulative value used as a delta (first snapshot, counter restart) must be storable as well.
    private func storable(_ usage: TokenUsage) -> (usage: TokenUsage?, repaired: Bool) {
        let value = usage.isValid ? usage : usage.clampedToValid
        return (value.isZero ? nil : value, !usage.isValid)
    }
}
