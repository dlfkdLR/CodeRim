import XCTest
@testable import CodeRimMobile

private actor MemoryCredentials: MobileCredentialStoring {
    var value: MobileCredential?
    private let delay: Duration
    init(_ value: MobileCredential?, delay: Duration = .zero) { self.value = value; self.delay = delay }
    func load() async throws -> MobileCredential? { try await Task.sleep(for: delay); return value }
    func save(_ credential: MobileCredential) { value = credential }
    func delete() { value = nil }
}
private actor OfflineLogoutRelay: MobileRelayServing {
    nonisolated let endpoint = URL(string: "https://relay.example.com")!
    private(set) var calls: [String] = []
    func get<Response: Decodable & Sendable>(_ path: String, token: String) throws -> Response {
        calls.append("GET \(path)")
        let data = Data(#"{"state":{"connection":"offline","updatedAt":1790000000,"staleAt":0,"providers":[],"sessions":[],"additionalSessionCount":0,"workingCount":0,"waitingCount":0,"unavailableCount":0},"preferences":{"providerIDs":["codex","claude"]},"availableProviders":[{"id":"codex","name":"Codex"},{"id":"gemini","name":"Gemini"}]}"#.utf8)
        return try JSONDecoder().decode(Response.self, from: data)
    }
    func send<Response: Decodable & Sendable, Body: Encodable & Sendable>(_ method: String, _ path: String, token: String?, body: Body) throws -> Response {
        calls.append("\(method) \(path)")
        throw URLError(.notConnectedToInternet)
    }
}

@MainActor
final class MobileLifecycleTests: XCTestCase {
    private func credential(expiresAt: Double = Date().addingTimeInterval(600).timeIntervalSince1970) -> MobileCredential {
        .init(endpoint: URL(string: "https://relay.example.com")!,
              session: .init(token: "synthetic-test-token", expiresAt: expiresAt))
    }
    /// The saved expiry is where the token started; the relay renews tokens in use. A phone past it
    /// stays connected while the relay accepts the token, and signs out only on the relay's 401.
    func testThePastSavedExpiryDoesNotSignOutButARelay401Does() async throws {
        let renewed = MemoryCredentials(credential(expiresAt: 1))
        let accepted = MobileAppModel(credentials: renewed, makeClient: { _ in StatusRelay(status: 200) })
        await accepted.restore()
        XCTAssertTrue(accepted.signedIn)
        let kept = try await renewed.load(); XCTAssertNotNil(kept)

        let ended = MemoryCredentials(credential(expiresAt: 1))
        let model = MobileAppModel(credentials: ended, makeClient: { _ in StatusRelay(status: 401) })
        await model.restore()
        XCTAssertFalse(model.signedIn)
        let stored = try await ended.load(); XCTAssertNil(stored)
        XCTAssertFalse(model.activityActive)
    }
    func testRestoreLocksLoginUntilAsynchronousCredentialLookupCompletes() async throws {
        let storage = MemoryCredentials(nil, delay: .milliseconds(150)), relay = OfflineLogoutRelay()
        let model = MobileAppModel(credentials: storage, makeClient: { _ in relay })
        let restore = Task { await model.restore() }
        await Task.yield()
        XCTAssertTrue(model.busy)
        await model.connect(MobilePairingLink(relay: URL(string: "https://relay.example.com")!,
            id: String(repeating: "i", count: 43), secret: String(repeating: "s", count: 43)))
        let calls = await relay.calls; XCTAssertTrue(calls.isEmpty)
        await restore.value
        XCTAssertFalse(model.busy)
    }
    func testOfflineLogoutClearsCredentialAndLiveActivityLocally() async throws {
        let storage = MemoryCredentials(credential()), relay = OfflineLogoutRelay()
        let model = MobileAppModel(credentials: storage, makeClient: { _ in relay })
        await model.restore(); XCTAssertTrue(model.signedIn)
        await model.signOut()
        XCTAssertFalse(model.signedIn)
        XCTAssertFalse(model.activityActive)
        let stored = try await storage.load(); XCTAssertNil(stored)
        XCTAssertNotNil(model.errorMessage)
    }
    func testAccountDeletionDoesNotClaimSuccessWhenServerIsOffline() async throws {
        let storage = MemoryCredentials(credential()), relay = OfflineLogoutRelay()
        let model = MobileAppModel(credentials: storage, makeClient: { _ in relay })
        await model.restore()
        await model.signOut(deleteAccount: true)
        XCTAssertTrue(model.signedIn)
        let stored = try await storage.load(); XCTAssertNotNil(stored)
        XCTAssertNotNil(model.errorMessage)
    }
    func testSelectedButUnavailableProviderCanStillBeDeselected() async throws {
        let storage = MemoryCredentials(credential()), relay = OfflineLogoutRelay()
        let model = MobileAppModel(credentials: storage, makeClient: { _ in relay })
        await model.restore()
        XCTAssertEqual(Set(model.selectionOptions.map(\.id)), ["codex", "claude", "gemini"])
        XCTAssertEqual(model.preferences.providerIDs, ["codex", "claude"])
    }
}

private actor ReorderedRelay: MobileRelayServing {
    nonisolated let endpoint = URL(string: "https://relay.example.com")!
    private var count = 0
    func get<Response: Decodable & Sendable>(_ path: String, token: String) async throws -> Response {
        let revision = count; count += 1
        if revision == 1 { try await Task.sleep(for: .milliseconds(120)) }
        let state = MobileActivityState(viewRevision: revision, connection: "offline", updatedAt: 0, staleAt: 0,
            providers: [], sessions: [], additionalSessionCount: 0, workingCount: 0, waitingCount: 0, unavailableCount: 0)
        let response = MobileSnapshotResponse(state: state, preferences: MobilePreferences(), availableProviders: [])
        return try JSONDecoder().decode(Response.self, from: JSONEncoder().encode(response))
    }
    func send<Response: Decodable & Sendable, Body: Encodable & Sendable>(_ method: String, _ path: String, token: String?, body: Body) throws -> Response {
        throw URLError(.notConnectedToInternet)
    }
}
extension MobileLifecycleTests {
    func testLateRefreshCannotRevertNewerProviderPage() async throws {
        let storage = MemoryCredentials(credential()), relay = ReorderedRelay()
        let model = MobileAppModel(credentials: storage, makeClient: { _ in relay })
        await model.restore()
        let oldRequest = Task { await model.refresh() }
        try await Task.sleep(for: .milliseconds(20))
        await model.refresh(); await oldRequest.value
        XCTAssertEqual(model.state.viewRevision, 2)
    }
}

private actor StatusRelay: MobileRelayServing {
    nonisolated let endpoint = URL(string: "https://relay.example.com")!
    let status: Int
    init(status: Int) { self.status = status }
    func get<Response: Decodable & Sendable>(_ path: String, token: String) async throws -> Response {
        guard status == 200 else { throw MobileRelayError.rejected(status) }
        let state = MobileActivityState(viewRevision: 0, connection: "offline", updatedAt: 0, staleAt: 0,
            providers: [], sessions: [], additionalSessionCount: 0, workingCount: 0, waitingCount: 0, unavailableCount: 0)
        let response = MobileSnapshotResponse(state: state, preferences: MobilePreferences(), availableProviders: [])
        return try JSONDecoder().decode(Response.self, from: JSONEncoder().encode(response))
    }
    func send<Response: Decodable & Sendable, Body: Encodable & Sendable>(_ method: String, _ path: String, token: String?, body: Body) throws -> Response {
        throw URLError(.notConnectedToInternet)
    }
}

/// Same view revision, different content times: the answer to the older request arrives last.
private actor SameRevisionRelay: MobileRelayServing {
    nonisolated let endpoint = URL(string: "https://relay.example.com")!
    private var count = 0
    func get<Response: Decodable & Sendable>(_ path: String, token: String) async throws -> Response {
        let call = count; count += 1
        let dataVersion: Double = call == 1 ? 100 : call == 2 ? 200 : 50
        if call == 1 { try await Task.sleep(for: .milliseconds(120)) }
        let state = MobileActivityState(viewRevision: 3, dataVersion: dataVersion, connection: "offline", updatedAt: dataVersion, staleAt: 0,
            providers: [], sessions: [], additionalSessionCount: 0, workingCount: 0, waitingCount: 0, unavailableCount: 0)
        let response = MobileSnapshotResponse(state: state, preferences: MobilePreferences(), availableProviders: [])
        return try JSONDecoder().decode(Response.self, from: JSONEncoder().encode(response))
    }
    func send<Response: Decodable & Sendable, Body: Encodable & Sendable>(_ method: String, _ path: String, token: String?, body: Body) throws -> Response {
        throw URLError(.notConnectedToInternet)
    }
}
extension MobileLifecycleTests {
    func testLateAnswerWithTheSameRevisionCannotReplaceNewerData() async throws {
        let storage = MemoryCredentials(credential()), relay = SameRevisionRelay()
        let model = MobileAppModel(credentials: storage, makeClient: { _ in relay })
        await model.restore()
        let oldRequest = Task { await model.refresh() }
        try await Task.sleep(for: .milliseconds(20))
        await model.refresh(); await oldRequest.value
        XCTAssertEqual(model.state.dataVersion, 200)
    }

    func testRevisionOrdersScreensAndDataVersionOrdersContent() {
        func state(_ revision: Int?, _ data: Double?) -> MobileActivityState {
            MobileActivityState(viewRevision: revision, dataVersion: data, connection: "connected", updatedAt: 0, staleAt: 0,
                providers: [], sessions: [], additionalSessionCount: 0, workingCount: 0, waitingCount: 0, unavailableCount: 0)
        }
        XCTAssertTrue(state(2, 1).supersedes(state(1, 99)), "a later screen wins even with older content")
        XCTAssertFalse(state(1, 99).supersedes(state(2, 1)))
        XCTAssertTrue(state(2, 5).supersedes(state(2, 5)))
        XCTAssertFalse(state(2, 4).supersedes(state(2, 5)))
        XCTAssertTrue(state(nil, nil).supersedes(state(0, nil)), "answers from an older relay still apply")
    }
}

private actor ProviderChoiceRelay: MobileRelayServing {
    nonisolated let endpoint = URL(string: "https://relay.example.com")!
    enum ResultMode: Sendable { case success, failure, conflict }
    let mode: ResultMode
    let conflictID: String
    var selected = "codex"
    var revision = 0
    private(set) var request: MobileNavigation?
    init(_ mode: ResultMode, conflictID: String = "codex") { self.mode = mode; self.conflictID = conflictID }
    func get<Response: Decodable & Sendable>(_ path: String, token: String) throws -> Response {
        let options = [MobileProviderOption(id: "codex", name: "Codex"), .init(id: "claude", name: "Claude")]
        let provider = MobileProvider(id: selected, name: selected == "codex" ? "Codex" : "Claude", state: "unavailable", windows: [], todayTokens: nil, localState: "unavailable", updatedAt: nil)
        let state = MobileActivityState(focus: .init(deviceID: "mac", deviceName: "Mac", platform: "macOS", deviceIndex: 1, deviceCount: 1, providerIndex: selected == "codex" ? 1 : 2, providerCount: 2), viewRevision: revision, connection: "offline", updatedAt: 0, staleAt: 0, providers: [provider], sessions: [], additionalSessionCount: 0, workingCount: 0, waitingCount: 0, unavailableCount: 0)
        let response = MobileSnapshotResponse(state: state, preferences: MobilePreferences(), availableProviders: options, displayProviders: options)
        return try JSONDecoder().decode(Response.self, from: JSONEncoder().encode(response))
    }
    func send<Response: Decodable & Sendable, Body: Encodable & Sendable>(_ method: String, _ path: String, token: String?, body: Body) throws -> Response {
        request = try JSONDecoder().decode(MobileNavigation.self, from: JSONEncoder().encode(body))
        if mode == .failure { throw URLError(.notConnectedToInternet) }
        revision += 1
        if mode == .success { selected = request?.providerID ?? selected }
        if mode == .conflict { selected = conflictID }
        return try get("/v1/snapshot", token: token ?? "")
    }
}
extension MobileLifecycleTests {
    func testExplicitChoiceIsRememberedAndBoundToDisplayedDeviceAndRevision() async throws {
        let relay = ProviderChoiceRelay(.success)
        let model = MobileAppModel(credentials: MemoryCredentials(credential()), makeClient: { _ in relay })
        await model.restore()
        let changed = await model.showProvider("claude")
        XCTAssertTrue(changed); XCTAssertEqual(model.state.providers.first?.id, "claude")
        let request = await relay.request
        XCTAssertEqual(request?.deviceID, "mac"); XCTAssertEqual(request?.expectedRevision, 0)
        await model.refresh()
        XCTAssertEqual(model.state.providers.first?.id, "claude")
        XCTAssertEqual(model.displayProviders.map(\.id), ["codex", "claude"])
    }
    func testFailedChoiceKeepsExistingServiceAndReportsError() async throws {
        let relay = ProviderChoiceRelay(.failure)
        let model = MobileAppModel(credentials: MemoryCredentials(credential()), makeClient: { _ in relay })
        await model.restore()
        let changed = await model.showProvider("claude")
        XCTAssertFalse(changed); XCTAssertEqual(model.state.providers.first?.id, "codex")
        XCTAssertNotNil(model.errorMessage); XCTAssertFalse(model.busy)
    }
    func testConflictingChoiceDoesNotDismissAsSuccess() async throws {
        let relay = ProviderChoiceRelay(.conflict)
        let model = MobileAppModel(credentials: MemoryCredentials(credential()), makeClient: { _ in relay })
        await model.restore()
        let changed = await model.showProvider("claude")
        XCTAssertFalse(changed); XCTAssertEqual(model.state.viewRevision, 1)
        XCTAssertEqual(model.state.providers.first?.id, "codex"); XCTAssertNotNil(model.errorMessage)
    }
    func testRechoosingCheckedServiceStillValidatesNewerIslandFocus() async throws {
        let relay = ProviderChoiceRelay(.conflict, conflictID: "claude")
        let model = MobileAppModel(credentials: MemoryCredentials(credential()), makeClient: { _ in relay })
        await model.restore()
        XCTAssertEqual(model.state.providers.first?.id, "codex")
        let changed = await model.showProvider("codex")
        XCTAssertFalse(changed)
        XCTAssertEqual(model.state.providers.first?.id, "claude")
        XCTAssertNotNil(model.errorMessage)
        let request = await relay.request; XCTAssertNotNil(request)
    }
    func testChoiceOutsideCurrentComputerIsNotSent() async throws {
        let relay = ProviderChoiceRelay(.success)
        let model = MobileAppModel(credentials: MemoryCredentials(credential()), makeClient: { _ in relay })
        await model.restore()
        let changed = await model.showProvider("gemini")
        XCTAssertFalse(changed)
        let request = await relay.request; XCTAssertNil(request)
    }
}

private actor PairingRelay: MobileRelayServing {
    nonisolated let endpoint = URL(string: "https://relay.example.com")!
    private(set) var calls: [String] = []
    var claim: Result<Void, MobileRelayError> = .success(())
    func failClaims(_ error: MobileRelayError) { claim = .failure(error) }
    func get<Response: Decodable & Sendable>(_ path: String, token: String) throws -> Response {
        calls.append("GET \(path)")
        let data = Data(#"{"state":{"connection":"connected","updatedAt":1790000000,"staleAt":0,"providers":[],"sessions":[],"additionalSessionCount":0,"workingCount":0,"waitingCount":0,"unavailableCount":0},"preferences":{"providerIDs":[]},"availableProviders":[],"devices":[{"id":"d","name":"작업 PC","platform":"windows","online":true,"lastSeen":1790000000}],"notice":"The CodeRim relay is busy today."}"#.utf8)
        return try JSONDecoder().decode(Response.self, from: data)
    }
    func send<Response: Decodable & Sendable, Body: Encodable & Sendable>(_ method: String, _ path: String, token: String?, body: Body) throws -> Response {
        calls.append("\(method) \(path)")
        let json: String
        switch path {
        case "/v1/accounts": json = #"{"token":"anonymous-phone-token-0000000000000000000000","expiresAt":4102444800}"#
        case "/v1/pairing/claim": try claim.get(); json = #"{"deviceID":"d","name":"작업 PC","platform":"windows"}"#
        default: json = #"{"ok":true}"#
        }
        return try JSONDecoder().decode(Response.self, from: Data(json.utf8))
    }
}
extension MobileLifecycleTests {
    private var link: MobilePairingLink {
        MobilePairingLink(relay: URL(string: "https://relay.example.com")!, id: String(repeating: "i", count: 43), secret: String(repeating: "s", count: 43))
    }
    func testFirstScanCreatesAnAnonymousAccountThenJoinsTheComputer() async throws {
        let storage = MemoryCredentials(nil), relay = PairingRelay()
        let model = MobileAppModel(credentials: storage, makeClient: { _ in relay })
        await model.restore(); XCTAssertFalse(model.signedIn)
        await model.connect(link)
        XCTAssertTrue(model.signedIn); XCTAssertNil(model.errorMessage)
        let calls = await relay.calls
        XCTAssertEqual(Array(calls.prefix(2)), ["POST /v1/accounts", "POST /v1/pairing/claim"])
        XCTAssertEqual(model.devices.first?.name, "작업 PC")
        XCTAssertEqual(model.serverNotice, "The CodeRim relay is busy today.")
        let stored = try await storage.load(); XCTAssertEqual(stored?.endpoint, link.relay)
    }
    func testLaterScansReuseTheAccountAndExplainExpiredCodes() async throws {
        let relay = PairingRelay()
        let model = MobileAppModel(credentials: MemoryCredentials(credential()), makeClient: { _ in relay })
        await model.restore()
        await relay.failClaims(.rejected(401))
        await model.connect(link)
        let calls = await relay.calls
        XCTAssertFalse(calls.contains("POST /v1/accounts"))
        XCTAssertEqual(model.errorMessage, "This QR code has expired or was already used. Show a new one on your computer.")
    }
    /// The QR code names its computer's server: scanning it connects there without typing an address.
    func testAComputerOnAnotherRelayIsJoinedOnItsOwnServer() async throws {
        let relay = PairingRelay()
        let storage = MemoryCredentials(credential())
        let model = MobileAppModel(credentials: storage, makeClient: { _ in relay })
        await model.restore()
        let other = URL(string: "https://other.example.com")!
        await model.connect(MobilePairingLink(relay: other, id: link.id, secret: link.secret))
        let calls = await relay.calls
        XCTAssertEqual(Array(calls.filter { !$0.hasPrefix("GET ") }.prefix(3)), ["DELETE /v1/session", "POST /v1/accounts", "POST /v1/pairing/claim"])
        XCTAssertNil(model.errorMessage)
        let stored = try await storage.load(); XCTAssertEqual(stored?.endpoint, other)
    }
}
