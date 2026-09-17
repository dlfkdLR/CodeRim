import Foundation

struct TokenUsage: Codable, Equatable, Sendable {
    let inputTokens: Int64
    let cachedInputTokens: Int64
    let cacheWriteInputTokens: Int64?
    let outputTokens: Int64

    init(
        inputTokens: Int64,
        cachedInputTokens: Int64,
        cacheWriteInputTokens: Int64? = nil,
        outputTokens: Int64
    ) {
        self.inputTokens = inputTokens
        self.cachedInputTokens = cachedInputTokens
        self.cacheWriteInputTokens = cacheWriteInputTokens
        self.outputTokens = outputTokens
    }

    static let zero = TokenUsage(
        inputTokens: 0,
        cachedInputTokens: 0,
        cacheWriteInputTokens: 0,
        outputTokens: 0
    )

    var totalTokens: Int64 {
        inputTokens
            .saturatedAdding(outputTokens)
    }

    var isZero: Bool {
        inputTokens == 0
            && cachedInputTokens == 0
            && (cacheWriteInputTokens == nil || cacheWriteInputTokens == 0)
            && outputTokens == 0
    }

    var isValid: Bool {
        let knownCacheWriteIsValid = cacheWriteInputTokens.map { cacheWrite in
            guard cacheWrite >= 0 else { return false }
            let combined = cachedInputTokens.addingReportingOverflow(cacheWrite)
            return !combined.overflow && combined.partialValue <= inputTokens
        } ?? true
        return inputTokens >= 0
            && cachedInputTokens >= 0
            && outputTokens >= 0
            && cachedInputTokens <= inputTokens
            && knownCacheWriteIsValid
    }

    func adding(_ other: TokenUsage) -> TokenUsage {
        TokenUsage(
            inputTokens: inputTokens.saturatedAdding(other.inputTokens),
            cachedInputTokens: cachedInputTokens.saturatedAdding(other.cachedInputTokens),
            cacheWriteInputTokens: optionalSaturatedAdd(cacheWriteInputTokens, other.cacheWriteInputTokens),
            outputTokens: outputTokens.saturatedAdding(other.outputTokens)
        )
    }

    func subtractingFloorAtZero(_ previous: TokenUsage) -> TokenUsage {
        TokenUsage(
            inputTokens: max(0, inputTokens - previous.inputTokens),
            cachedInputTokens: max(0, cachedInputTokens - previous.cachedInputTokens),
            cacheWriteInputTokens: optionalSubtract(cacheWriteInputTokens, previous.cacheWriteInputTokens),
            outputTokens: max(0, outputTokens - previous.outputTokens)
        )
    }

    func componentWiseMaximum(with other: TokenUsage) -> TokenUsage {
        TokenUsage(
            inputTokens: max(inputTokens, other.inputTokens),
            cachedInputTokens: max(cachedInputTokens, other.cachedInputTokens),
            cacheWriteInputTokens: optionalMaximum(cacheWriteInputTokens, other.cacheWriteInputTokens),
            outputTokens: max(outputTokens, other.outputTokens)
        )
    }

    func isComponentWiseAtLeast(_ other: TokenUsage) -> Bool {
        inputTokens >= other.inputTokens
            && cachedInputTokens >= other.cachedInputTokens
            && optionalIsAtLeast(cacheWriteInputTokens, other.cacheWriteInputTokens)
            && outputTokens >= other.outputTokens
    }

    private func optionalSaturatedAdd(_ lhs: Int64?, _ rhs: Int64?) -> Int64? {
        guard let lhs, let rhs else { return nil }
        return lhs.saturatedAdding(rhs)
    }

    private func optionalSubtract(_ lhs: Int64?, _ rhs: Int64?) -> Int64? {
        guard let lhs, let rhs else { return nil }
        return max(0, lhs - rhs)
    }

    private func optionalMaximum(_ lhs: Int64?, _ rhs: Int64?) -> Int64? {
        switch (lhs, rhs) {
        case let (.some(lhs), .some(rhs)): max(lhs, rhs)
        case let (.some(value), .none), let (.none, .some(value)): value
        case (.none, .none): nil
        }
    }

    private func optionalIsAtLeast(_ lhs: Int64?, _ rhs: Int64?) -> Bool {
        switch (lhs, rhs) {
        case let (.some(lhs), .some(rhs)): lhs >= rhs
        case (.some, .none), (.none, .none): true
        case (.none, .some): false
        }
    }
}

private extension Int64 {
    func saturatedAdding(_ other: Int64) -> Int64 {
        let result = addingReportingOverflow(other)
        return result.overflow ? Int64.max : result.partialValue
    }
}
