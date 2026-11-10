import Foundation

struct CodexTokenObservation: Equatable, Sendable {
    let occurredAt: Date
    let ordinal: Int64?
    let lastUsage: TokenUsage?
    let cumulativeUsage: TokenUsage?
}
