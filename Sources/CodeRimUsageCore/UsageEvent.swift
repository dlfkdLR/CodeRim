import Foundation

package enum PricingContext: Int, Equatable, Sendable {
    package static let highContextInputThreshold: Int64 = 272_000

    case standard = 0
    case highContext = 1
}

package struct UsageEvent: Equatable, Sendable {
    package let eventKey: String
    package let occurredAt: Date
    package let sessionID: String?
    package let model: String?
    package let projectPath: String?
    package let usage: TokenUsage
    package let sourcePath: String
    package let sourcePosition: Int64
    package let pricingContext: PricingContext?
    /// Import provenance only; no new persisted column or external format.
    package let importParentSessionID: String?

    package init(
        eventKey: String,
        occurredAt: Date,
        sessionID: String?,
        model: String?,
        projectPath: String?,
        usage: TokenUsage,
        sourcePath: String,
        sourcePosition: Int64,
        pricingContext: PricingContext? = nil,
        importParentSessionID: String? = nil
    ) {
        self.eventKey = eventKey
        self.occurredAt = occurredAt
        self.sessionID = sessionID
        self.model = model
        self.projectPath = projectPath
        self.usage = usage
        self.sourcePath = sourcePath
        self.sourcePosition = sourcePosition
        self.pricingContext = pricingContext
        self.importParentSessionID = importParentSessionID
    }
}

package struct SessionMetadata: Equatable, Sendable {
    package let id: String?
    package let model: String?
    package let workingDirectory: String?
    package let forkedFromID: String?
    package let parentThreadID: String?
    package let subagentHistoryStartOrdinal: Int64?
    package let occurredAt: Date?

    package init(
        id: String?,
        model: String?,
        workingDirectory: String?,
        forkedFromID: String? = nil,
        parentThreadID: String? = nil,
        subagentHistoryStartOrdinal: Int64? = nil,
        occurredAt: Date? = nil
    ) {
        self.id = id
        self.model = model
        self.workingDirectory = workingDirectory
        self.forkedFromID = forkedFromID
        self.parentThreadID = parentThreadID
        self.subagentHistoryStartOrdinal = subagentHistoryStartOrdinal
        self.occurredAt = occurredAt
    }

    package var inheritsHistory: Bool {
        forkedFromID != nil || parentThreadID != nil || subagentHistoryStartOrdinal != nil
    }
}
