import Foundation
#if CODERIM_RUST_MOBILE_SELECTION
import CCodeRimMobile
#endif

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
    /// The relay's stored content sequence when it answered (grows with every change, never reused).
    /// Orders answers that share
    /// a view revision; the revision alone orders changes of screen (filter, computer, service).
    public var dataVersion: Double? = nil
    /// When the relay computed this answer. Orders answers with the same content whose time-dependent
    /// parts differ: a limit that reset in between, or a computer that went quiet or came back.
    public var evaluatedAt: Double? = nil
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
    /// Set while the free relay is rationing its daily allowance.
    public var notice: String? = nil
    public static var disconnected: Self {
        .init(connection: "offline", updatedAt: Date().timeIntervalSince1970, staleAt: 0,
              providers: [], sessions: [], additionalSessionCount: 0, workingCount: 0, waitingCount: 0, unavailableCount: 0)
    }
    public func isStale(at date: Date = Date()) -> Bool {
        connection != "connected" || date.timeIntervalSince1970 >= staleAt
    }
    /// Whether this answer may replace `other`: a later screen, or the same screen with content at
    /// least as new. A late answer to an earlier request never wins.
    ///
    /// Among answers with the same content, the one the relay evaluated later wins: a heartbeat moves no
    /// content version, and neither does a limit resetting, yet both change the answer. Without an
    /// evaluation time (an older relay), the one that heard from the computer later wins.
    public func supersedes(_ other: MobileActivityState) -> Bool {
#if CODERIM_RUST_MOBILE_SELECTION
        cr_mobile_supersedes(comparisonVersion, other.comparisonVersion) == 1
#else
        let revision = viewRevision ?? 0, otherRevision = other.viewRevision ?? 0
        if revision != otherRevision { return revision > otherRevision }
        let version = dataVersion ?? 0, otherVersion = other.dataVersion ?? 0
        if version != otherVersion { return version > otherVersion }
        if let evaluatedAt, let otherEvaluated = other.evaluatedAt, evaluatedAt != otherEvaluated {
            return evaluatedAt > otherEvaluated
        }
        return staleAt >= other.staleAt
#endif
    }

#if CODERIM_RUST_MOBILE_SELECTION
    private var comparisonVersion: cr_mobile_version {
        cr_mobile_version(revision: Int64(viewRevision ?? 0), data_version: dataVersion ?? 0,
                          evaluated_at: evaluatedAt ?? 0, stale_at: staleAt,
                          has_evaluation: evaluatedAt == nil ? 0 : 1)
    }
#endif
}

enum MobileSnapshotResolution { case accept, keep, retry }

extension MobileActivityState {
    func refreshHeader(over displayed: Self, previousPush: Bool, nextPush: Bool) -> (registerPush: Bool, checkActivities: Bool) {
        #if CODERIM_RUST_MOBILE_SELECTION
        let result = cr_mobile_refresh_header(comparisonVersion, displayed.comparisonVersion, previousPush, nextPush)
        return (result.register_push, result.check_activities)
        #else
        return (!previousPush && nextPush, supersedes(displayed))
        #endif
    }

    @inline(__always)
    func resolveSnapshot<C: Collection>(against activities: C, mayRetry: Bool,
                                        stateFor: (C.Element) -> Self?) -> MobileSnapshotResolution {
        #if CODERIM_RUST_MOBILE_SELECTION
        guard !activities.isEmpty else { return .accept }
        return withUnsafeTemporaryAllocation(of: cr_mobile_version.self, capacity: activities.count) { buffer in
            var count = 0
            for activity in activities {
                if let state = stateFor(activity) {
                    buffer[count] = state.comparisonVersion
                    count += 1
                }
            }
            if count == 0 { return .accept }
            switch Int(cr_mobile_reconcile_snapshot(comparisonVersion, buffer.baseAddress, count, mayRetry)) {
            case CR_REFRESH_ACCEPT: return .accept
            case CR_REFRESH_KEEP: return .keep
            case CR_REFRESH_RETRY: return .retry
            default: preconditionFailure("Invalid snapshot version buffer")
            }
        }
        #else
        if let newest = activities.compactMap(stateFor).max(by: { !$0.supersedes($1) }), !supersedes(newest) {
            return mayRetry ? .retry : .keep
        }
        return .accept
        #endif
    }
}

public struct MobilePreferences: Codable, Sendable {
    public var providerIDs: [String]
    public init(providerIDs: [String] = []) { self.providerIDs = providerIDs }
}

extension MobilePreferences {
    func selectionOptions(available: [MobileProviderOption]) -> [MobileProviderOption] {
        guard !providerIDs.isEmpty else { return available }
        var result = available
        func appendMissing(_ id: String) {
            let name = id == "codex" ? "Codex" : id == "claude" ? "Claude" : id
            result.append(MobileProviderOption(id: id, name: "\(name) · not connected"))
        }
#if CODERIM_RUST_MOBILE_SELECTION
        if let indices = providerIndices(available, operation: CR_MOBILE_MISSING),
           indices.allSatisfy({ $0 >= available.count && $0 - available.count < providerIDs.count }) {
            for index in indices { appendMissing(providerIDs[index - available.count]) }
            return result
        }
#endif
        for id in providerIDs where !result.contains(where: { $0.id == id }) { appendMissing(id) }
        return result
    }

    func updating(_ id: String, enabled: Bool, available: [MobileProviderOption]) -> Self? {
#if CODERIM_RUST_MOBILE_SELECTION
        let operation = enabled ? CR_MOBILE_ENABLE : CR_MOBILE_DISABLE
        let connected = providerIDs.isEmpty ? available : []
        if let indices = providerIndices(connected, operation: operation, target: id) {
            guard !indices.isEmpty else { return nil }
            let count = connected.count + providerIDs.count
            if indices.allSatisfy({ $0 < count || (enabled && $0 == count) }) {
                return Self(providerIDs: indices.map { index in
                    if index == count { return id }
                    if index < connected.count { return connected[index].id }
                    return providerIDs[index - connected.count]
                })
            }
        }
#endif
        var ids = providerIDs.isEmpty ? available.map(\.id) : providerIDs
        if enabled && !ids.contains(id) { ids.append(id) }
        if !enabled { ids.removeAll { $0 == id } }
        guard (1...100).contains(ids.count) else { return nil }
        return Self(providerIDs: ids)
    }

#if CODERIM_RUST_MOBILE_SELECTION
    /// Swift assigns equal Unicode strings the same temporary key. Rust receives
    /// only these integers and returns positions in the original two lists.
    private func providerIndices(_ available: [MobileProviderOption], operation: Int, target: String? = nil) -> [Int]? {
        let (count, overflow) = available.count.addingReportingOverflow(providerIDs.count)
        guard !overflow, UInt32(exactly: count) != nil else { return nil }
        var keyByID: [String: UInt32] = [:]
        func key(_ id: String) -> UInt32 {
            if let existing = keyByID[id] { return existing }
            let next = UInt32(keyByID.count)
            keyByID[id] = next
            return next
        }
        var keys: [UInt32] = []
        keys.reserveCapacity(count)
        for option in available { keys.append(key(option.id)) }
        for id in providerIDs { keys.append(key(id)) }
        let targetKey = target.map(key) ?? 0
        let capacity = operation == CR_MOBILE_MISSING ? providerIDs.count : 100
        var output = [UInt32](repeating: 0, count: capacity)
        let written = keys.withUnsafeBufferPointer { inputs in
            output.withUnsafeMutableBufferPointer { indices in
                cr_mobile_provider_indices(UInt8(operation), inputs.baseAddress, inputs.count, available.count,
                                           targetKey, indices.baseAddress, indices.count)
            }
        }
        guard written >= 0 && written <= capacity else { return nil }
        return output.prefix(written).map(Int.init)
    }
#endif
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
    /// Whether the connected relay can send ActivityKit notifications through APNs.
    public var pushConfigured: Bool? = nil
    public var devices: [MobileDevice]? = nil
    /// Eligible services on the currently displayed computer, in Island order.
    public var displayProviders: [MobileProviderOption]? = nil
    public var notice: String? = nil
}
/// A computer's pairing offer, shown to the iPhone as a QR code.
public struct MobilePairingStart: Codable, Sendable {
    public var id: String
    public var secret: String
    public var expiresAt: Double
}
public struct MobilePairingRequest: Codable, Sendable {
    public var id: String
    public var secret: String
    public init(id: String, secret: String) { self.id = id; self.secret = secret }
}
/// What a computer receives once its QR code has been scanned.
public struct MobilePairingPoll: Codable, Sendable {
    public var pending: Bool? = nil
    public var token: String? = nil
    public var expiresAt: Double? = nil
    public var deviceID: String? = nil
}
public struct MobileClaimResult: Codable, Sendable {
    public var deviceID: String
    public var name: String
    public var platform: String
}
public struct MobileDesktopLabel: Codable, Sendable {
    public var platform: String
    public var name: String
    public init(platform: String, name: String) { self.platform = platform; self.name = name }
}
public struct MobileSnapshotReply: Decodable, Sendable {
    public var ok: Bool
    /// Seconds until the next heartbeat the relay can afford today.
    public var interval: Double? = nil
    public var notice: String? = nil
}

/// App-only QR/paste envelope. It is deliberately not a URL: the complete pairing
/// capability must never pass through an unbound custom-scheme handler.
public struct MobilePairingLink: Equatable, Sendable {
    public var relay: URL
    public var id: String
    public var secret: String
    public init(relay: URL, id: String, secret: String) {
        self.relay = (try? MobileRelayClient.validatedEndpoint(relay.absoluteString)) ?? relay
        self.id = id; self.secret = secret
    }

    public var qrPayload: String {
        ["CODERIM-PAIR-1", relay.absoluteString, id, secret].joined(separator: "\n")
    }

    public init?(_ text: String) {
        guard text.utf8.count <= 512 else { return nil }
        let fields = text.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        guard fields.count == 4, fields[0] == "CODERIM-PAIR-1",
              fields[1] == fields[1].trimmingCharacters(in: .whitespacesAndNewlines),
              let relay = try? MobileRelayClient.validatedEndpoint(fields[1]),
              Self.token(fields[2]), Self.token(fields[3]) else { return nil }
        self.init(relay: relay, id: fields[2], secret: fields[3])
    }
    private static func token(_ value: String) -> Bool {
        (16...64).contains(value.count) && value.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }
    }
}
public struct MobileSessionToken: Codable, Sendable {
    public var token: String
    public var expiresAt: Double
    public init(token: String, expiresAt: Double) { self.token = token; self.expiresAt = expiresAt }
}
public struct MobileOK: Decodable, Sendable { public var ok: Bool }
public struct MobileEmpty: Encodable, Sendable { public init() {} }
public struct MobilePushToStart: Encodable, Sendable {
    public var pushToken: String
    public init(pushToken: String) { self.pushToken = pushToken }
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
