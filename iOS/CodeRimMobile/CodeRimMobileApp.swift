import SwiftUI

@main
struct CodeRimMobileApp: App {
    @StateObject private var model = MobileAppModel()
    @Environment(\.scenePhase) private var scenePhase
    @ViewBuilder private var rootView: some View {
        #if DEBUG
        if ProcessInfo.processInfo.arguments.contains("--ui-settings") { DebugSettingsPreview() }
        else if ProcessInfo.processInfo.arguments.contains("--ui-preview") { DebugPreviewView() }
        else { MobileSettingsView(model: model) }
        #else
        MobileSettingsView(model: model)
        #endif
    }
    var body: some Scene {
        WindowGroup {
            presentation
                .task {
                    guard !usesPreview else { return }
                    await model.restore(); model.setForeground(scenePhase == .active)
                }
                .onChange(of: scenePhase) { _, phase in
                    guard !usesPreview else { return }
                    model.setForeground(phase == .active)
                }
        }
    }
    private var usesPreview: Bool {
        #if DEBUG
        ProcessInfo.processInfo.arguments.contains("--ui-settings") || ProcessInfo.processInfo.arguments.contains("--ui-preview")
        #else
        false
        #endif
    }
    @ViewBuilder private var presentation: some View {
        #if DEBUG
        if ProcessInfo.processInfo.arguments.contains("--ui-large-text") {
            rootView.dynamicTypeSize(.accessibility2)
                .preferredColorScheme(ProcessInfo.processInfo.arguments.contains("--ui-dark") ? .dark : nil)
        } else {
            rootView.preferredColorScheme(ProcessInfo.processInfo.arguments.contains("--ui-dark") ? .dark : nil)
        }
        #else
        rootView
        #endif
    }

}


#if DEBUG
/// In-memory, opt-in native settings fixtures. Never uses Keychain or a network client.
private struct DebugSettingsPreview: View {
    @StateObject private var model: MobileAppModel
    init() {
        let empty = ProcessInfo.processInfo.arguments.contains("--ui-empty-settings")
        let relay = DebugSettingsRelay(empty: empty, noServices: ProcessInfo.processInfo.arguments.contains("--ui-no-services"))
        _model = StateObject(wrappedValue: MobileAppModel(credentials: DebugSettingsCredentials(),
            makeClient: { _ in relay }, appleState: { _ in .authorized }))
    }
    var body: some View {
        VStack(spacing: 0) {
            Text("Sample preview · Account and Live Activity actions are disabled.").font(.caption2).foregroundStyle(.secondary)
                .frame(maxWidth: .infinity).padding(8).background(.regularMaterial)
            MobileSettingsView(model: model)
        }.task { await model.restore() }
    }
}
private actor DebugSettingsCredentials: MobileCredentialStoring {
    func load() -> MobileCredential? {
        .init(endpoint: URL(string: "https://relay.example.com")!,
              // The in-memory fixture must never trigger real account-expiry cleanup.
              session: .init(token: "ui-settings-synthetic-token", expiresAt: Date.distantFuture.timeIntervalSince1970),
              appleUserID: "ui-settings-synthetic-user")
    }
    func save(_ credential: MobileCredential) {}
    func delete() {}
}
private actor DebugSettingsRelay: MobileRelayServing {
    nonisolated let endpoint = URL(string: "https://relay.example.com")!
    let empty: Bool
    let noServices: Bool
    var preferences = MobilePreferences()
    var selectedID = "codex"
    var revision = 0
    init(empty: Bool, noServices: Bool) { self.empty = empty; self.noServices = noServices }
    func get<Response: Decodable & Sendable>(_ path: String, token: String) throws -> Response {
        let now = Date().timeIntervalSince1970
        let options: [MobileProviderOption] = (empty || noServices) ? [] : [.init(id: "codex", name: "Codex"), .init(id: "claude", name: "Claude")]
            + (1...80).map { .init(id: "service-\($0)", name: "Service \($0)") }
        let devices: [MobileDevice] = empty ? [] : [
            .init(id: "mac", name: "Personal Mac", platform: "macOS", online: true, lastSeen: now),
            .init(id: "pc", name: "Work PC", platform: "windows", online: false, lastSeen: now - 600)]
        let displayed = options.filter { preferences.providerIDs.isEmpty || preferences.providerIDs.contains($0.id) }
        let selected = displayed.first(where: { $0.id == selectedID }) ?? displayed.first
        var state = MobileActivityState.disconnected
        state.viewRevision = revision
        state.focus = .init(deviceID: devices.first?.id ?? "", deviceName: devices.first?.name ?? "No device", platform: "macOS",
                            deviceIndex: empty ? 0 : 1, deviceCount: devices.count,
                            providerIndex: selected.flatMap { pick in displayed.firstIndex(where: { $0.id == pick.id }) }.map { $0 + 1 } ?? 0,
                            providerCount: displayed.count)
        state.providers = selected.map { [.init(id: $0.id, name: $0.name, state: "unavailable", windows: [], todayTokens: nil, localState: "unavailable", updatedAt: nil)] } ?? []
        let response = MobileSnapshotResponse(state: state, preferences: preferences, availableProviders: options, devices: devices, displayProviders: displayed)
        return try JSONDecoder().decode(Response.self, from: JSONEncoder().encode(response))
    }
    func send<Response: Decodable & Sendable, Body: Encodable & Sendable>(
        _ method: String, _ path: String, token: String?, body: Body
    ) throws -> Response {
        if path == "/v1/view" {
            let request = try JSONDecoder().decode(MobileNavigation.self, from: JSONEncoder().encode(body))
            if request.expectedRevision == revision, let id = request.providerID, request.deviceID == "mac" {
                selectedID = id; revision += 1
            }
            return try get("/v1/snapshot", token: token ?? "")
        }
        if path == "/v1/preferences" {
            preferences = try JSONDecoder().decode(MobilePreferences.self, from: JSONEncoder().encode(body))
            return try JSONDecoder().decode(Response.self, from: Data(#"{"ok":true}"#.utf8))
        }
        if path == "/v1/pairing" {
            return try JSONDecoder().decode(Response.self, from: JSONEncoder().encode(
                MobilePairing(code: "ABCD1234", expiresAt: Date().addingTimeInterval(300).timeIntervalSince1970)))
        }
        throw MobileRelayError.rejected(503)
    }
}
#endif
