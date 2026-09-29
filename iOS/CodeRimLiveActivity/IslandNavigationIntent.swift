import ActivityKit
import AppIntents
import CryptoKit
import Foundation

struct MobileActivityConnection {
    static func id(for token: String) -> String { SHA256.hash(data: Data(token.utf8)).map { String(format: "%02x", $0) }.joined() }
}

struct IslandNavigationIntent: LiveActivityIntent {
    static let title: LocalizedStringResource = "Change display"
    static let openAppWhenRun = false
    @Parameter(title: "Target") var axis: String
    @Parameter(title: "Direction") var direction: Int
    @Parameter(title: "View revision") var revision: Int
    @Parameter(title: "Provider") var providerID: String
    @Parameter(title: "Computer") var deviceID: String
    @Parameter(title: "Group") var groupID: String
    init() { axis = "provider"; direction = 1; revision = 0; providerID = ""; deviceID = ""; groupID = "" }
    init(axis: String, direction: Int = 1, revision: Int = 0, providerID: String = "", deviceID: String = "", groupID: String = "") {
        self.axis = axis; self.direction = direction; self.revision = revision
        self.providerID = providerID; self.deviceID = deviceID; self.groupID = groupID
    }
    func perform() async throws -> some IntentResult {
        try await IslandNavigation.perform(axis: axis, direction: direction, revision: revision, providerID: providerID, deviceID: deviceID, groupID: groupID)
        return .result()
    }
}

/// Executes in the app process even when the Settings scene has never been created.
/// An actor serializes local button actions; the server also guards the view revision.
actor IslandNavigation {
    static let shared = IslandNavigation()
    #if DEBUG
    private var previewActivityID = ""
    private var previewPins: [String: [String]] = [:]
    private var previewRecent: [String: [String]] = [:]
    private var previewPath: [Int] = []
    private var previewMode = "quick"
    #endif
    static func perform(axis: String, direction: Int, revision: Int, providerID: String = "", deviceID: String = "", groupID: String = "") async throws {
        try await shared.navigate(axis: axis, direction: direction, revision: revision, providerID: providerID, deviceID: deviceID, groupID: groupID)
    }
    private func navigate(axis: String, direction: Int, revision: Int, providerID: String, deviceID: String, groupID: String) async throws {
        #if DEBUG
        if let sample = Activity<CodeRimActivityAttributes>.activities.first(where: { $0.attributes.connectionID == "preview" }) {
            var state = sample.content.state
            guard (state.viewRevision ?? 0) == revision else { return }
            if previewActivityID != sample.id {
                previewActivityID = sample.id; previewPins = [:]; previewRecent = [:]; previewPath = []; previewMode = "quick"
            }
            let count = state.focus?.providerCount ?? 0
            let catalog = DebugPreviewView.providerCatalog(count: count)
            let device = state.focus?.deviceID ?? "preview"
            var opened = state.providerPicker?.isOpen == true
            if axis == "provider-picker" {
                opened.toggle(); previewMode = "quick"; previewPath = []
            } else if axis == "provider-all" {
                previewMode = "all"; previewPath = []
            } else if axis == "provider-group" {
                guard state.providerPicker?.groups?.contains(where: { $0.id == groupID }) == true else { return }
                previewMode = "all"; previewPath = groupID.split(separator: ".").compactMap { Int($0) }
            } else if axis == "provider-back" {
                if previewPath.isEmpty { previewMode = "quick" } else { previewPath.removeLast() }
            } else if axis == "provider-pin", let selected = state.providers.first?.id {
                var pins = previewPins[device] ?? []
                if pins.contains(selected) { pins.removeAll { $0 == selected } }
                else { guard pins.count < 3 else { return }; pins.append(selected) }
                previewPins[device] = pins
            } else if axis == "device", var focus = state.focus {
                let windows = focus.platform != "windows"
                focus.platform = windows ? "windows" : "macOS"; focus.deviceName = windows ? "Work PC" : "Personal Mac"; focus.deviceIndex = windows ? 2 : 1
                focus.deviceID = windows ? "preview-pc" : "preview"
                let target = previewRecent[focus.deviceID]?.first ?? state.providers.first?.id
                if let index = catalog.firstIndex(where: { $0.id == target }) { state.providers = [catalog[index]]; focus.providerIndex = index + 1 }
                state.focus = focus; opened = false; previewPath = []; previewMode = "quick"
            } else {
                guard !catalog.isEmpty else { return }
                let target = providerID.isEmpty ? catalog[(max(0, (state.focus?.providerIndex ?? 1) - 1) + direction + max(1, count)) % max(1, count)].id : providerID
                guard let index = catalog.firstIndex(where: { $0.id == target }), providerID.isEmpty || deviceID == device else { return }
                state.providers = [catalog[index]]; state.focus?.providerIndex = index + 1
                previewRecent[device] = Array(([target] + (previewRecent[device] ?? []).filter { $0 != target }).prefix(3))
                opened = false; previewPath = []; previewMode = "quick"
                state.sessions = state.sessions.map { var session = $0; session.providerID = target; return session }
            }
            let focusedDevice = state.focus?.deviceID ?? "preview"
            state.providerPicker = DebugPreviewView.picker(catalog: catalog, selected: state.providers.first?.id,
                pinned: previewPins[focusedDevice] ?? [], recent: previewRecent[focusedDevice] ?? [], mode: previewMode, path: previewPath, isOpen: opened)
            state.viewRevision = (state.viewRevision ?? 0) + 1
            await sample.update(ActivityContent(state: state, staleDate: Date(timeIntervalSince1970: state.staleAt)))
            return
        }
        #endif
        guard let credential = try await MobileCredentialStore.shared.load(), credential.expiresAt > Date().timeIntervalSince1970 else { throw MobileRelayError.expired }
        let connection = MobileActivityConnection.id(for: credential.token)
        let activities = Activity<CodeRimActivityAttributes>.activities.filter { $0.attributes.connectionID == connection && ($0.activityState == .active || $0.activityState == .stale) }
        guard !activities.isEmpty else { return }
        let client = try MobileRelayClient(endpoint: credential.endpoint)
        let response: MobileSnapshotResponse = try await client.send("POST", "/v1/view", token: credential.token,
            body: MobileNavigation(axis: axis, direction: direction, expectedRevision: revision,
                providerID: providerID.isEmpty ? nil : providerID, deviceID: deviceID.isEmpty ? nil : deviceID, groupID: groupID.isEmpty ? nil : groupID, pickerVersion: 2))
        // Logout/account changes may race the network request; never restore another login's screen.
        guard try await MobileCredentialStore.shared.load()?.token == credential.token else { return }
        for activity in activities where activity.activityState == .active || activity.activityState == .stale {
            guard (activity.content.state.viewRevision ?? 0) <= (response.state.viewRevision ?? 0) else { continue }
            await activity.update(ActivityContent(state: response.state, staleDate: Date(timeIntervalSince1970: response.state.staleAt)))
        }
    }
}
