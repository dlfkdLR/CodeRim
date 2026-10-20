import CodeRimShared
import XCTest
@testable import CodeRim

/// Cancel is final: an answer or a Keychain save that completes after it connects nothing, and a cancelled
/// pairing that is still finishing never disturbs the next one.
@MainActor
final class MobilePairingCancelTests: XCTestCase {
    private var defaults: UserDefaults!
    private var suite: String!

    override func setUp() async throws {
        suite = "MobilePairingCancelTests.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suite)!
        defaults.set("https://relay.example.com", forKey: "mobileRelayURL")
    }
    override func tearDown() async throws { defaults.removePersistentDomain(forName: suite) }

    private func store(_ relay: PairingFake, credentials: GatedCredentials = GatedCredentials()) -> MobileConnectionStore {
        MobileConnectionStore(defaults: defaults, credentials: credentials, makeClient: { _, _ in relay }, makeMonitors: { [:] })
    }

    /// The poll's answer has arrived; Cancel runs before the pairing continues with it.
    func testAnAnswerThatArrivesAsThePairingIsCancelledConnectsNothing() async throws {
        let relay = PairingFake(), credentials = GatedCredentials()
        let mobile = store(relay, credentials: credentials)
        relay.onPoll = { await MainActor.run { mobile.cancelPairing() } }
        mobile.startPairing()
        await relay.waitFor("DELETE /v1/session")
        XCTAssertFalse(mobile.isConnected)
        XCTAssertFalse(defaults.bool(forKey: "mobileRelayPaired"))
        let saved = try await credentials.load(); XCTAssertNil(saved)
        XCTAssertEqual(relay.revoked, ["desktop-token"], "the computer the iPhone added is taken back")
    }

    /// Keychain is still saving when Cancel is pressed.
    func testACancelDuringTheKeychainSaveConnectsNothingAndForgetsTheToken() async throws {
        let relay = PairingFake(), credentials = GatedCredentials(holdSaves: true)
        let mobile = store(relay, credentials: credentials)
        mobile.startPairing()
        await credentials.waitForSave()
        mobile.cancelPairing()
        await credentials.releaseSaves()
        await relay.waitFor("DELETE /v1/session")
        XCTAssertFalse(mobile.isConnected)
        let saved = try await credentials.load(); XCTAssertNil(saved)
        XCTAssertFalse(defaults.bool(forKey: "mobileRelayPaired"))
    }

    /// A pairing cancelled while its poll is out finishes later; the pairing started after it stays open.
    func testACancelledPairingFinishingLaterDoesNotClearTheNextOne() async throws {
        let relay = PairingFake()
        relay.holdPolls = true
        let mobile = store(relay)
        mobile.startPairing()
        await relay.waitFor("POST /v1/pairing/poll")
        mobile.cancelPairing()
        mobile.startPairing()
        await relay.waitFor("POST /v1/pairing/poll", count: 2)
        relay.releaseOnePoll(pending: true)   // the first pairing's poll comes back; it is cancelled and ends
        try await Task.sleep(for: .milliseconds(100))
        mobile.startPairing()                  // the second is still open, so this is ignored
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(relay.count("POST /v1/pairing/start"), 2)
        XCTAssertNotNil(mobile.pairingLink)
        mobile.cancelPairing(); relay.releaseAll()
    }

    func testThePollClientWaitsLongerThanTheRelayHoldsAPoll() async throws {
        let relay = PairingFake(), waits = Waits()
        let mobile = MobileConnectionStore(defaults: defaults, credentials: GatedCredentials(),
            makeClient: { _, wait in waits.add(wait); return relay }, makeMonitors: { [:] })
        mobile.startPairing()
        await relay.waitFor("POST /v1/pairing/poll")
        XCTAssertEqual(waits.values.max(), 20, "the relay holds a poll up to 20 seconds")
        let client = try MobileRelayClient(endpoint: relay.endpoint, pollWait: 20)
        XCTAssertNotNil(client)
    }

    func testEndpointsAreOneSpellingPerOrigin() throws {
        let mac = try MobileRelayClient.validatedEndpoint("https://Relay.Example.com:443/")
        let windows = try MobileRelayClient.validatedEndpoint("https://relay.example.com")
        XCTAssertEqual(mac, windows)
        XCTAssertEqual(mac.absoluteString, "https://relay.example.com")
        XCTAssertTrue(MobileRelayClient.sameOrigin(URL(string: "https://relay.example.com/")!, windows))
        XCTAssertFalse(MobileRelayClient.sameOrigin(URL(string: "https://relay.example.com:8443")!, windows))
    }
}

private final class PairingFake: MobileRelayServing, @unchecked Sendable {
    let endpoint = URL(string: "https://relay.example.com")!
    private let lock = NSLock()
    private var log: [String] = []
    private var tokens: [String] = []
    private var held: [CheckedContinuation<Bool, Never>] = []
    var holdPolls = false
    var onPoll: @Sendable () async -> Void = {}

    var revoked: [String] { lock.withLock { tokens } }
    func count(_ call: String) -> Int { lock.withLock { log.filter { $0 == call }.count } }
    func waitFor(_ call: String, count: Int = 1) async {
        for _ in 0..<400 where self.count(call) < count { try? await Task.sleep(for: .milliseconds(10)) }
    }
    func releaseOnePoll(pending: Bool) {
        let next = lock.withLock { held.isEmpty ? nil : held.removeFirst() }
        next?.resume(returning: pending)
    }
    func releaseAll() { for continuation in lock.withLock({ defer { held = [] }; return held }) { continuation.resume(returning: true) } }

    func send<Response: Decodable & Sendable, Body: Encodable & Sendable>(
        _ method: String, _ path: String, token: String?, body: Body
    ) async throws -> Response {
        lock.withLock { log.append("\(method) \(path)") }
        let json: String
        switch path {
        case "/v1/pairing/start":
            json = #"{"id":"pair-id-000000000000000000","secret":"pair-secret-0000000000000000","expiresAt":\#(Date().timeIntervalSince1970 + 300)}"#
        case "/v1/pairing/poll":
            if holdPolls {
                let pending = await withCheckedContinuation { continuation in lock.withLock { held.append(continuation) } }
                if pending { json = #"{"pending":true}"#; break }
            }
            await onPoll()
            json = #"{"token":"desktop-token","expiresAt":4102444800,"deviceID":"d"}"#
        case "/v1/session":
            if let token { lock.withLock { tokens.append(token) } }
            json = #"{"ok":true}"#
        default: json = #"{"ok":true}"#
        }
        return try JSONDecoder().decode(Response.self, from: Data(json.utf8))
    }
    func get<Response: Decodable & Sendable>(_ path: String, token: String) async throws -> Response {
        throw MobileRelayError.invalidResponse
    }
}

private final class Waits: @unchecked Sendable {
    private let lock = NSLock()
    private var list: [TimeInterval] = []
    var values: [TimeInterval] { lock.withLock { list } }
    func add(_ value: TimeInterval) { lock.withLock { list.append(value) } }
}

private actor GatedCredentials: MobileCredentialStoring {
    private var value: MobileCredential?
    private let holdSaves: Bool
    private var saving = false
    private var gate: CheckedContinuation<Void, Never>?
    init(holdSaves: Bool = false) { self.holdSaves = holdSaves }
    func load() -> MobileCredential? { value }
    func save(_ credential: MobileCredential) async {
        saving = true
        if holdSaves { await withCheckedContinuation { gate = $0 } }
        value = credential
    }
    func delete() { value = nil }
    func waitForSave() async { while !saving { await Task.yield() } }
    func releaseSaves() { gate?.resume(); gate = nil }
}
