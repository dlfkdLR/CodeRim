import Foundation

/// A credential-free interchange format. Local totals never include account-wide history.
public struct CompanionSnapshot: Codable, Equatable, Sendable {
    public static let currentSchemaVersion = 1
    public var schemaVersion = currentSchemaVersion
    public var generatedAt: Date
    public var providers: [CompanionProvider]

    public init(generatedAt: Date = Date(), providers: [CompanionProvider]) {
        self.generatedAt = generatedAt
        self.providers = providers
    }

    public func evaluated(at now: Date = Date(), calendar: Calendar = .current) -> Self {
        var result = self
        result.providers = providers.map { $0.evaluated(at: now, calendar: calendar) }
        return result
    }
}

public enum CompanionState: String, Codable, Sendable {
    case ready, partial, stale, loading, disabled, unavailable, needsAuth, accessDenied, unsupported
}

public struct CompanionProvider: Codable, Equatable, Sendable, Identifiable {
    public var id: String
    public var name: String
    public var enabled: Bool
    public var fidelity: String
    public var localUsage: CompanionLocalUsage?
    public var history: CompanionHistory?
    public var plan: String?
    public var limits: CompanionLimits

    public init(id: String, name: String, localUsage: CompanionLocalUsage?, limits: CompanionLimits,
                enabled: Bool = true, fidelity: String = "official", history: CompanionHistory? = nil, plan: String? = nil) {
        self.id = id
        self.name = name
        self.enabled = enabled
        self.fidelity = fidelity
        self.localUsage = localUsage
        self.history = history
        self.plan = plan
        self.limits = limits
    }

    public func evaluated(at now: Date, calendar: Calendar = .current) -> Self {
        var result = self
        if let localUsage { result.localUsage = localUsage.evaluated(at: now, calendar: calendar) }
        result.limits = limits.evaluated(at: now)
        result.history = history?.evaluated(at: now, calendar: calendar)
        return result
    }
}

public struct CompanionTokens: Codable, Equatable, Sendable {
    public var inputTokens: Int64
    public var cachedInputTokens: Int64
    public var outputTokens: Int64
    public var totalTokens: Int64 {
        let sum = inputTokens.addingReportingOverflow(outputTokens)
        return sum.overflow ? Int64.max : sum.partialValue
    }

    public init(inputTokens: Int64, cachedInputTokens: Int64, outputTokens: Int64) {
        self.inputTokens = inputTokens
        self.cachedInputTokens = cachedInputTokens
        self.outputTokens = outputTokens
    }
    private enum CodingKeys: String, CodingKey {
        case inputTokens, cachedInputTokens, outputTokens, totalTokens
    }

    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        inputTokens = try values.decode(Int64.self, forKey: .inputTokens)
        cachedInputTokens = try values.decode(Int64.self, forKey: .cachedInputTokens)
        outputTokens = try values.decode(Int64.self, forKey: .outputTokens)
    }

    public func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(inputTokens, forKey: .inputTokens)
        try values.encode(cachedInputTokens, forKey: .cachedInputTokens)
        try values.encode(outputTokens, forKey: .outputTokens)
        try values.encode(totalTokens, forKey: .totalTokens)
    }

}

public enum CompanionPeriod: String, CaseIterable, Codable, Sendable {
    case today, week, month, allTime = "all-time"

    public var label: String {
        switch self {
        case .today: "Today"
        case .week: "This Week"
        case .month: "This Month"
        case .allTime: "Local History"
        }
    }
}

public struct CompanionLocalUsage: Codable, Equatable, Sendable {
    public let scope: String
    public var state: CompanionState
    public var updatedAt: Date?
    public var periodsAsOf: Date
    public var timeZoneIdentifier: String
    public var totals: [String: CompanionTokens]

    public init(state: CompanionState, updatedAt: Date?, periodsAsOf: Date,
                timeZoneIdentifier: String = TimeZone.current.identifier,
                totals: [String: CompanionTokens]) {
        scope = "this-mac"
        self.state = state
        self.updatedAt = updatedAt
        self.periodsAsOf = periodsAsOf
        self.timeZoneIdentifier = timeZoneIdentifier
        self.totals = totals
    }

    public func evaluated(at now: Date, calendar: Calendar = .current) -> Self {
        var result = self
        guard state == .ready || state == .partial || state == .stale else { return result }
        if updatedAt == nil || now.timeIntervalSince(updatedAt!) > 300
            || updatedAt! > now.addingTimeInterval(60)
            || !calendar.isDate(periodsAsOf, inSameDayAs: now)
            || timeZoneIdentifier != calendar.timeZone.identifier {
            result.state = .stale
        }
        return result
    }
}

public struct CompanionLimits: Codable, Equatable, Sendable {
    public var state: CompanionState
    public var updatedAt: Date?
    public var windows: [CompanionLimitWindow]
    public var staleAfterSeconds: TimeInterval
    public var message: String?
    public var headlineID: String?
    public var headline: CompanionLimitWindow? {
        guard let headlineID else { return windows.first }
        return windows.first { $0.id == headlineID }
    }

    public init(state: CompanionState, updatedAt: Date?, windows: [CompanionLimitWindow],
                staleAfterSeconds: TimeInterval = 300, message: String? = nil, headlineID: String? = nil) {
        self.state = state
        self.updatedAt = updatedAt
        self.windows = windows
        self.staleAfterSeconds = staleAfterSeconds
        self.message = message
        self.headlineID = headlineID
    }

    public func evaluated(at now: Date) -> Self {
        var result = self
        if state == .ready || state == .stale {
            if updatedAt == nil || now.timeIntervalSince(updatedAt!) > staleAfterSeconds
                || updatedAt! > now.addingTimeInterval(60)
                || windows.contains(where: { $0.resetsAt.map { $0 <= now } ?? false }) {
                result.state = .stale
            }
        }
        return result
    }
}

public struct CompanionLimitWindow: Codable, Equatable, Sendable, Identifiable {
    public var id: String
    public var name: String
    public var durationMinutes: Int
    public var usedPercent: Double?
    public var remainingCount: Int?
    public var usedCount: Int?
    public var unit: String?
    public var displayValue: String?
    public var resetsAt: Date?
    public var remainingPercent: Double? { usedPercent.map { min(100, max(0, 100 - $0)) } }

    public init(id: String, name: String, durationMinutes: Int = 0, usedPercent: Double? = nil, resetsAt: Date? = nil,
                remainingCount: Int? = nil, usedCount: Int? = nil, unit: String? = nil, displayValue: String? = nil) {
        self.id = id
        self.name = name
        self.durationMinutes = durationMinutes
        self.usedPercent = usedPercent
        self.remainingCount = remainingCount
        self.usedCount = usedCount
        self.unit = unit
        self.displayValue = displayValue
        self.resetsAt = resetsAt
    }
}

/// Daily local history without model names, projects, sessions or account identity.
public struct CompanionHistory: Codable, Equatable, Sendable {
    public var scope: String = "this-mac"
    public var state: CompanionState
    public var updatedAt: Date
    public var timeZoneIdentifier: String
    public var days: [CompanionHistoryDay]
    public var totalTokens: Int64
    public var estimatedCostUSD: Double?
    public var costIsPartial: Bool

    public init(state: CompanionState, updatedAt: Date,
                timeZoneIdentifier: String = TimeZone.current.identifier,
                days: [CompanionHistoryDay], totalTokens: Int64,
                estimatedCostUSD: Double?, costIsPartial: Bool) {
        self.state = state
        self.updatedAt = updatedAt
        self.timeZoneIdentifier = timeZoneIdentifier
        self.days = days
        self.totalTokens = totalTokens
        self.estimatedCostUSD = estimatedCostUSD
        self.costIsPartial = costIsPartial
    }

    public var isReadable: Bool { [.ready, .partial, .stale].contains(state) && !days.isEmpty }

    public func evaluated(at now: Date, calendar: Calendar = .current) -> Self {
        var value = self
        if [.ready, .partial, .stale].contains(state),
           now.timeIntervalSince(updatedAt) > 600 || updatedAt > now.addingTimeInterval(60)
            || !calendar.isDate(updatedAt, inSameDayAs: now)
            || timeZoneIdentifier != calendar.timeZone.identifier {
            value.state = .stale
        }
        return value
    }
}

public struct CompanionHistoryDay: Codable, Equatable, Sendable, Identifiable {
    public var date: Date
    public var tokens: Int64
    public var estimatedCostUSD: Double?
    public var costIsPartial: Bool
    public var id: Date { date }

    public init(date: Date, tokens: Int64, estimatedCostUSD: Double?, costIsPartial: Bool) {
        self.date = date
        self.tokens = tokens
        self.estimatedCostUSD = estimatedCostUSD
        self.costIsPartial = costIsPartial
    }
}


extension CompanionProvider {
    public var readableLocalUsage: CompanionLocalUsage? {
        guard let usage = localUsage, [.ready, .partial, .stale].contains(usage.state),
              usage.totals["today"] != nil else { return nil }
        return usage
    }
}
