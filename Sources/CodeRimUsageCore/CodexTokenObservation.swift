import Foundation

package struct CodexTokenObservation: Equatable, Sendable {
    package let occurredAt: Date
    package let ordinal: Int64?
    package let lastUsage: TokenUsage?
    package let cumulativeUsage: TokenUsage?

    package init(occurredAt: Date, ordinal: Int64?, lastUsage: TokenUsage?, cumulativeUsage: TokenUsage?) {
        self.occurredAt = occurredAt; self.ordinal = ordinal
        self.lastUsage = lastUsage; self.cumulativeUsage = cumulativeUsage
    }
}
