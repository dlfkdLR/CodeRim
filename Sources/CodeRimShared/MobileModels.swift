import Foundation

/// Mobile wire types use Unix seconds explicitly, including ActivityKit push content-state.
/// No provider credentials, email addresses, filesystem paths or transcript bodies belong here.
public struct MobileUsageWindow: Codable, Hashable, Sendable {
    public var name: String
    public var remainingPercent: Double?
    public var resetsAt: Double?
    public init(name: String, remainingPercent: Double?, resetsAt: Double?) {
        self.name = name; self.remainingPercent = remainingPercent; self.resetsAt = resetsAt
    }
}

public struct MobileProvider: Codable, Hashable, Sendable, Identifiable {
    public var id: String
    public var name: String
    public var state: String
    public var windows: [MobileUsageWindow]
    public var todayTokens: Int64?
    public var localState: String
    public var updatedAt: Double?
    public init(id: String, name: String, state: String, windows: [MobileUsageWindow],
                todayTokens: Int64?, localState: String, updatedAt: Double?) {
        self.id = id; self.name = name; self.state = state; self.windows = windows
        self.todayTokens = todayTokens; self.localState = localState; self.updatedAt = updatedAt
    }
}

public struct MobileSession: Codable, Hashable, Sendable {
    public enum Phase: String, Codable, Sendable { case working, waiting, idle, unavailable }
    public var providerID: String
    public var phase: Phase
    /// Empty unless the Mac user explicitly enables task-title sharing.
    public var title: String
    public var since: Double?
    public init(providerID: String, phase: Phase, title: String = "", since: Double?) {
        self.providerID = providerID; self.phase = phase; self.title = title; self.since = since
    }
}

public struct MobileSnapshot: Codable, Sendable {
    public var schemaVersion = 1
    public var generatedAt: Double
    public var providers: [MobileProvider]
    public var sessions: [MobileSession]
    public init(generatedAt: Double, providers: [MobileProvider], sessions: [MobileSession]) {
        self.generatedAt = generatedAt; self.providers = providers; self.sessions = sessions
    }
}

public struct MobileActivityState: Codable, Hashable, Sendable {
    public var focus: MobileFocus? = nil
    public var viewRevision: Int? = nil
    public var providerPicker: MobileProviderPicker? = nil
    public var connection: String
    public var updatedAt: Double
    public var staleAt: Double
    public var providers: [MobileProvider]
    public var sessions: [MobileSession]
    public var additionalSessionCount: Int
    public var workingCount: Int
    public var waitingCount: Int
    public var unavailableCount: Int
    public static var disconnected: Self {
        .init(connection: "offline", updatedAt: Date().timeIntervalSince1970, staleAt: 0,
              providers: [], sessions: [], additionalSessionCount: 0, workingCount: 0, waitingCount: 0, unavailableCount: 0)
    }
    public func isStale(at date: Date = Date()) -> Bool {
        connection != "connected" || date.timeIntervalSince1970 >= staleAt
    }
}

public struct MobilePreferences: Codable, Sendable {
    public var providerIDs: [String]
    public init(providerIDs: [String] = []) { self.providerIDs = providerIDs }
}
public struct MobileProviderOption: Codable, Hashable, Sendable, Identifiable {
    public var id: String
    public var name: String
    public var isPinned: Bool? = nil
    public var phase: String? = nil
}
public struct MobileProviderGroup: Codable, Hashable, Sendable, Identifiable {
    public var id: String
    public var firstName: String
    public var lastName: String
    public var count: Int
}
/// Only one small page crosses ActivityKit's payload boundary, never the full catalog.
public struct MobileProviderPicker: Codable, Hashable, Sendable {
    public var isOpen: Bool
    public var page: Int
    public var pageCount: Int
    public var options: [MobileProviderOption]
    public var mode: String? = nil
    public var groups: [MobileProviderGroup]? = nil
    public var canGoBack: Bool? = nil
    public var isCurrentPinned: Bool? = nil
    public var canPinCurrent: Bool? = nil
}
public struct MobileSnapshotResponse: Codable, Sendable {
    public var state: MobileActivityState
    public var preferences: MobilePreferences
    public var availableProviders: [MobileProviderOption]
    public var devices: [MobileDevice]? = nil
    /// Eligible services on the currently displayed computer, in Island order.
    public var displayProviders: [MobileProviderOption]? = nil
}
public struct MobileChallenge: Codable, Sendable {
    public var id: String
    public var nonce: String
    public var expiresAt: Double
}
public struct MobilePairing: Codable, Sendable {
    public var code: String
    public var expiresAt: Double
}
public struct MobileSessionToken: Codable, Sendable {
    public var token: String
    public var expiresAt: Double
}
public struct MobileOK: Decodable, Sendable { public var ok: Bool }
public struct MobileEmpty: Encodable, Sendable { public init() {} }
public struct MobileAppleLogin: Encodable, Sendable {
    public var challengeID: String
    public var identityToken: String
    public init(challengeID: String, identityToken: String) { self.challengeID = challengeID; self.identityToken = identityToken }
}
public struct MobilePairClaim: Encodable, Sendable {
    public var code: String
    public var platform: String
    public var name: String
    public init(code: String, platform: String = "macOS", name: String = "Mac") { self.code = code; self.platform = platform; self.name = name }
}
public struct MobileActivityRegistration: Encodable, Sendable {
    public var activityID: String
    public var pushToken: String
    public init(activityID: String, pushToken: String) { self.activityID = activityID; self.pushToken = pushToken }
}

public struct MobileFocus: Codable, Hashable, Sendable {
    public var deviceID: String
    public var deviceName: String
    public var platform: String
    public var deviceIndex: Int
    public var deviceCount: Int
    public var providerIndex: Int
    public var providerCount: Int
    public var deviceSymbol: String { platform == "windows" ? "pc" : "laptopcomputer" }
}
public struct MobileDevice: Codable, Hashable, Sendable, Identifiable {
    public var id: String
    public var name: String
    public var platform: String
    public var online: Bool
    public var lastSeen: Double
}
public struct MobileNavigation: Codable, Sendable {
    public var axis: String
    public var direction: Int
    public var expectedRevision: Int?
    public var providerID: String? = nil
    public var deviceID: String? = nil
    public var groupID: String? = nil
    public var pickerVersion: Int? = nil
    public init(axis: String, direction: Int, expectedRevision: Int? = nil, providerID: String? = nil, deviceID: String? = nil, groupID: String? = nil, pickerVersion: Int? = nil) {
        self.axis = axis; self.direction = direction; self.expectedRevision = expectedRevision
        self.providerID = providerID; self.deviceID = deviceID; self.groupID = groupID; self.pickerVersion = pickerVersion
    }
}
