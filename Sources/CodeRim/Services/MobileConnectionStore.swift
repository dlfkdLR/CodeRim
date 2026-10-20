import Combine
import CodeRimShared
import Foundation

/// The iPhone connection owns its monitors, so hiding the Mac notch cannot stop phone updates.
@MainActor
final class MobileConnectionStore: ObservableObject {
    static let shared = MobileConnectionStore()
    @Published private(set) var isConnected = false
    @Published private(set) var isBusy = false
    @Published private(set) var status = "Not connected"
    @Published private(set) var errorMessage: String?
    @Published private(set) var lastSentAt: Date?
    @Published private(set) var serverAddress = ""
    /// The QR code the iPhone scans, while a pairing offer is open.
    @Published private(set) var pairingLink: MobilePairingLink?
    @Published private(set) var pairingExpiresAt: Date?
    /// The relay's own notice while it is rationing its free daily allowance.
    @Published private(set) var serverNotice: String?
    /// The relay every CodeRim connects to unless overridden for self-hosting.
    @Published var relayAddress: String {
        didSet { defaults.set(relayAddress, forKey: "mobileRelayURL") }
    }
    @Published var deviceName: String {
        didSet { defaults.set(deviceName, forKey: "mobileDeviceName") }
    }
    @Published var shareTaskTitles = false {
        didSet { defaults.set(shareTaskTitles, forKey: "mobileShareTaskTitles") }
    }
    private let defaults: UserDefaults
    private let credentials: any MobileCredentialStoring
    /// Makes a client for a relay; the second argument is how long the relay may hold a request open.
    private let makeClient: (URL, TimeInterval) throws -> any MobileRelayServing
    private let makeMonitors: () -> [String: any AgentActivityMonitor]
    private var credential: MobileCredential?
    private var client: (any MobileRelayServing)?
    private var latestSnapshot: CompanionSnapshot?
    private var loop: Task<Void, Never>?
    private var generation = UUID()
    private var monitors: [String: any AgentActivityMonitor] = [:]
    private var pairingTask: Task<Void, Never>?
    /// The pairing offer currently open. A cancelled pairing's task may still be finishing; it acts only while
    /// its ID is this one, so it can neither connect after Cancel nor clear a newer pairing.
    private var pairingID: UUID?
    private var lastSent: (body: MobileSnapshot, at: Date)?
    private var heartbeat: TimeInterval = 300

    private convenience init() {
        self.init(defaults: .standard, credentials: MobileCredentialStore.shared,
                  makeClient: { try MobileRelayClient(endpoint: $0, pollWait: $1) }, makeMonitors: MobileConnectionStore.localMonitors)
    }

    init(defaults: UserDefaults, credentials: any MobileCredentialStoring,
         makeClient: @escaping (URL, TimeInterval) throws -> any MobileRelayServing,
         makeMonitors: @escaping () -> [String: any AgentActivityMonitor]) {
        self.defaults = defaults; self.credentials = credentials; self.makeClient = makeClient; self.makeMonitors = makeMonitors
        relayAddress = defaults.string(forKey: "mobileRelayURL")
            ?? Bundle.main.object(forInfoDictionaryKey: "CodeRimRelayURL") as? String ?? ""
        deviceName = defaults.string(forKey: "mobileDeviceName") ?? "Mac"
        shareTaskTitles = defaults.bool(forKey: "mobileShareTaskTitles")
        guard defaults.bool(forKey: "mobileRelayPaired") else { return }
        isBusy = true
        Task {
            defer { isBusy = false }
            do {
                // The relay extends a token each time it is used, so the expiry saved at pairing is only
                // where it started; the relay alone decides, with 401, when the iPhone must pair again.
                guard let saved = try await credentials.load() else { return }
                try activate(saved)
            } catch { errorMessage = error.localizedDescription; status = "Reconnect iPhone" }
        }
    }

    func receive(_ snapshot: CompanionSnapshot) { latestSnapshot = snapshot }

    /// Opens a five-minute pairing offer and waits for the iPhone to scan it.
    func startPairing() {
        guard !isBusy, !isConnected, pairingTask == nil else { return }
        errorMessage = nil
        let id = UUID(); pairingID = id
        pairingTask = Task { [weak self] in
            await self?.runPairing(id)
            if self?.pairingID == id { self?.pairingTask = nil; self?.pairingID = nil }
        }
    }

    func cancelPairing() {
        pairingTask?.cancel(); pairingTask = nil; pairingID = nil
        pairingLink = nil; pairingExpiresAt = nil
        if !isConnected { status = "Not connected" }
    }

    /// Whether pairing `id` is still the one the person is waiting for.
    private func isCurrent(_ id: UUID) -> Bool { pairingID == id && !Task.isCancelled }

    private func runPairing(_ id: UUID) async {
        do {
            let endpoint = try MobileRelayClient.validatedEndpoint(relayAddress)
            let client = try makeClient(endpoint, 0)
            let offer: MobilePairingStart = try await client.send("POST", "/v1/pairing/start", token: nil,
                body: MobileDesktopLabel(platform: "macOS", name: String(deviceName.prefix(24))))
            guard isCurrent(id) else { return }
            pairingLink = MobilePairingLink(relay: endpoint, id: offer.id, secret: offer.secret)
            pairingExpiresAt = Date(timeIntervalSince1970: offer.expiresAt)
            status = "Scan the QR code with the CodeRim iPhone app"
            let request = MobilePairingRequest(id: offer.id, secret: offer.secret)
            // The relay holds each poll until the iPhone scans or about 20 seconds pass; this client waits that
            // long. A poll that still fails is asked again, and the relay answers it with the same token.
            let poller = try makeClient(endpoint, Self.pollWait)
            while isCurrent(id), Date().timeIntervalSince1970 < offer.expiresAt {
                let result: MobilePairingPoll
                do { result = try await poller.send("POST", "/v1/pairing/poll", token: nil, body: request) }
                catch MobileRelayError.rejected(401) { break }
                catch {
                    if !isCurrent(id) { return }
                    try? await Task.sleep(for: .seconds(3)); continue
                }
                guard let token = result.token, let expiresAt = result.expiresAt else { continue }
                // Cancelled while the answer was on its way: the computer the iPhone added is taken back.
                guard isCurrent(id) else { return await Self.revoke(token, client: client) }
                let saved = MobileCredential(endpoint: endpoint, session: MobileSessionToken(token: token, expiresAt: expiresAt))
                do { try await credentials.save(saved) }
                catch {
                    await Self.revoke(token, client: client)
                    throw error
                }
                // Cancelled while Keychain saved it: nothing is shared, and the saved token goes too.
                guard isCurrent(id) else {
                    if (try? await credentials.load())?.token == token { try? await credentials.delete() }
                    return await Self.revoke(token, client: client)
                }
                defaults.set(true, forKey: "mobileRelayPaired")
                pairingLink = nil; pairingExpiresAt = nil
                try activate(saved)
                return
            }
            guard isCurrent(id) else { return }
            pairingLink = nil; pairingExpiresAt = nil
            status = "The QR code expired. Connect again."
        } catch {
            guard isCurrent(id) else { return }
            pairingLink = nil; pairingExpiresAt = nil
            errorMessage = error.localizedDescription; status = "Connection failed"
        }
    }

    /// The relay's longest hold of a pairing poll.
    static let pollWait: TimeInterval = 20

    private static func revoke(_ token: String, client: any MobileRelayServing) async {
        let _: MobileOK? = try? await client.send("DELETE", "/v1/session", token: token, body: MobileEmpty())
    }

    private func activate(_ saved: MobileCredential) throws {
        let client = try makeClient(saved.endpoint, 0)
        loop?.cancel(); monitors.values.forEach { $0.stop() }
        self.credential = saved; self.client = client
        generation = UUID(); let run = generation
        serverAddress = saved.endpoint.absoluteString; isConnected = true; status = "Waiting for usage"
        monitors = makeMonitors()
        monitors.values.forEach { $0.start() }
        lastSent = nil
        loop = Task { [weak self] in
            while !Task.isCancelled {
                await self?.sendSnapshot(generation: run)
                do { try await Task.sleep(for: .seconds(15)) } catch { break }
            }
        }
    }

    private func sendSnapshot(generation run: UUID) async {
        guard run == generation, let credential, let client, let latestSnapshot else { return }
        do {
            let sessions = monitors.mapValues(\.sessions)
            let body = Self.snapshot(latestSnapshot, sessions: sessions, shareTitles: shareTaskTitles)
            // The free relay can afford changes as they happen plus a heartbeat every few minutes.
            guard Self.shouldSend(body, after: lastSent, heartbeat: heartbeat) else { return }
            let reply: MobileSnapshotReply = try await client.send("POST", "/v1/snapshot", token: credential.token, body: body)
            guard run == generation, !Task.isCancelled else { return }
            lastSent = (body, Date()); heartbeat = max(60, min(3600, reply.interval ?? 300))
            serverNotice = reply.notice
            lastSentAt = Date(); status = "Sharing with iPhone"; errorMessage = nil
        } catch {
            guard run == generation, !Task.isCancelled else { return }
            status = "Connection interrupted"; errorMessage = error.localizedDescription
            if case MobileRelayError.rejected(401) = error { stopLocal(); status = "Reconnect iPhone" }
            if case MobileRelayError.expired = error { stopLocal(); status = "Reconnect iPhone" }
        }
    }

    func disconnect() async {
        guard !isBusy else { return }
        isBusy = true; defer { isBusy = false }
        let old = credential, oldClient = client
        stopLocal(); errorMessage = nil
        // Stop local collection first, even if the server is temporarily unreachable.
        do {
            try await credentials.delete()
            defaults.set(false, forKey: "mobileRelayPaired")
            if let old, let oldClient {
                let _: MobileOK = try await oldClient.send("DELETE", "/v1/session", token: old.token, body: MobileEmpty())
            }
            status = "Not connected"
        } catch {
            defaults.set(false, forKey: "mobileRelayPaired")
            status = "Sharing stopped on this Mac"
            errorMessage = "The server could not confirm disconnection. Remove the connection in iPhone Settings when online."
        }
    }

    private static func localMonitors() -> [String: any AgentActivityMonitor] {
        let home = UsageProvider.claudeConfigRoot(home: URL(fileURLWithPath: NSHomeDirectory()))
        return ["codex": CodexActivityMonitor(), "claude": ClaudeSessionMonitor(
            directory: home.appendingPathComponent("sessions"), projects: home.appendingPathComponent("projects"))]
    }

    private func stopLocal() {
        generation = UUID(); loop?.cancel(); loop = nil
        monitors.values.forEach { $0.stop() }; monitors = [:]
        credential = nil; client = nil; isConnected = false; lastSentAt = nil; lastSent = nil; serverNotice = nil
    }

    static func shouldSend(_ body: MobileSnapshot, after last: (body: MobileSnapshot, at: Date)?,
                           heartbeat: TimeInterval, now: Date = Date()) -> Bool {
        guard let last else { return true }
        if now.timeIntervalSince(last.at) >= heartbeat { return true }
        var previous = last.body; previous.generatedAt = body.generatedAt
        // Sorted keys: the encoder's key order is not otherwise stable between calls.
        let encoder = JSONEncoder(); encoder.outputFormatting = .sortedKeys
        return (try? encoder.encode(previous)) != (try? encoder.encode(body))
    }

    static func snapshot(_ snapshot: CompanionSnapshot, sessions: [String: [AgentSession]], shareTitles: Bool,
                         now: Date = Date()) -> MobileSnapshot {
        let providers = snapshot.evaluated(at: now).providers.filter(\.enabled).prefix(100).map { provider in
            let windows = provider.limits.windows
            let headline = provider.limits.headline
            let ordered = headline.map { first in [first] + windows.filter { $0.id != first.id } } ?? windows
            let rawTokens = provider.localUsage?.totals[CompanionPeriod.today.rawValue]?.totalTokens
            let tokens = rawTokens.flatMap { $0 >= 0 && $0 <= 9_007_199_254_740_991 ? $0 : nil }
            return MobileProvider(id: provider.id, name: String(provider.name.prefix(32)), state: provider.limits.state.rawValue,
                windows: ordered.prefix(2).map { MobileUsageWindow(name: String($0.name.prefix(32)),
                    remainingPercent: $0.usedPercent.flatMap { $0.isFinite && (0...100).contains($0) ? 100 - $0 : nil },
                    resetsAt: $0.resetsAt?.timeIntervalSince1970) },
                todayTokens: tokens, localState: provider.localUsage?.state.rawValue ?? "unavailable",
                updatedAt: provider.limits.updatedAt?.timeIntervalSince1970)
        }
        let selected = Set(providers.map(\.id))
        let active = sessions.filter { selected.contains($0.key) }.flatMap { provider, values in
            values.map { session -> MobileSession in
                let phase: MobileSession.Phase = switch session.state {
                case .busy: .working
                case .waiting: .waiting
                case .idle: .idle
                case .unavailable: .unavailable
                }
                return MobileSession(providerID: provider, phase: phase,
                    title: shareTitles ? String(session.detail.prefix(60)) : "",
                    since: session.since == .distantPast ? nil : session.since.timeIntervalSince1970)
            }
        }.sorted { a, b in
            let rank: [MobileSession.Phase: Int] = [.waiting: 0, .working: 1, .idle: 2, .unavailable: 3]
            if rank[a.phase] != rank[b.phase] { return rank[a.phase, default: 3] < rank[b.phase, default: 3] }
            return (a.since ?? 0) > (b.since ?? 0)
        }
        return MobileSnapshot(generatedAt: now.timeIntervalSince1970, providers: providers, sessions: Array(active.prefix(64)))
    }
}
