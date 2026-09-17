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

            return UsageNormalizationResult(
                delta: cumulative.isZero ? nil : cumulative,
                state: UsageNormalizationState(
                    cumulativeHighWaterMark: cumulative,
                    lastObservedAt: observation.occurredAt,
                    quality: state.quality
                ),
                diagnostic: nil
            )
        }

        guard cumulative.isComponentWiseAtLeast(previous) else {
            if observation.lastUsage == cumulative {
                return UsageNormalizationResult(
                    delta: cumulative.isZero ? nil : cumulative,
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
}
