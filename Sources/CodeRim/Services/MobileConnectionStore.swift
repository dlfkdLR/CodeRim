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
    @Published var deviceName = UserDefaults.standard.string(forKey: "mobileDeviceName") ?? "Mac" {
        didSet { UserDefaults.standard.set(deviceName, forKey: "mobileDeviceName") }
    }
    @Published var shareTaskTitles = false {
        didSet { UserDefaults.standard.set(shareTaskTitles, forKey: "mobileShareTaskTitles") }
    }
    private var credential: MobileCredential?
    private var client: MobileRelayClient?
    private var latestSnapshot: CompanionSnapshot?
    private var loop: Task<Void, Never>?
    private var generation = UUID()
    private var monitors: [String: any AgentActivityMonitor] = [:]

    private init() {
        shareTaskTitles = UserDefaults.standard.bool(forKey: "mobileShareTaskTitles")
        guard UserDefaults.standard.bool(forKey: "mobileRelayPaired") else { return }
        isBusy = true
        Task {
            defer { isBusy = false }
            do {
                guard let saved = try await MobileCredentialStore.shared.load() else { return }
                guard saved.expiresAt > Date().timeIntervalSince1970 else { throw MobileRelayError.expired }
                try activate(saved)
            } catch { errorMessage = error.localizedDescription; status = "Reconnect iPhone" }
        }
    }

    func receive(_ snapshot: CompanionSnapshot) { latestSnapshot = snapshot }

    func pair(server: String, code: String) async {
        guard !isBusy, !isConnected else { return }
        isBusy = true; errorMessage = nil
        defer { isBusy = false }
        do {
            let endpoint = try MobileRelayClient.validatedEndpoint(server)
            let client = try MobileRelayClient(endpoint: endpoint)
            let normalized = code.uppercased().filter { !$0.isWhitespace && $0 != "-" }
            guard normalized.count == 8 else { status = "Enter the eight-character code from your iPhone."; return }
            let token: MobileSessionToken = try await client.send("POST", "/v1/pairing/claim", body: MobilePairClaim(code: normalized, platform: "macOS", name: String(deviceName.prefix(24))))
            let saved = MobileCredential(endpoint: endpoint, session: token)
            do { try await MobileCredentialStore.shared.save(saved) }
            catch {
                let _: MobileOK? = try? await client.send("DELETE", "/v1/session", token: token.token, body: MobileEmpty())
                throw error
            }
            UserDefaults.standard.set(true, forKey: "mobileRelayPaired")
            try activate(saved)
        } catch { errorMessage = error.localizedDescription; status = "Connection failed" }
    }

    private func activate(_ saved: MobileCredential) throws {
        let client = try MobileRelayClient(endpoint: saved.endpoint)
        loop?.cancel(); monitors.values.forEach { $0.stop() }
        self.credential = saved; self.client = client
        generation = UUID(); let run = generation
        serverAddress = saved.endpoint.absoluteString; isConnected = true; status = "Waiting for usage"
        let home = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(".claude")
        monitors = ["codex": CodexActivityMonitor(), "claude": ClaudeSessionMonitor(
            directory: home.appendingPathComponent("sessions"), projects: home.appendingPathComponent("projects"))]
        monitors.values.forEach { $0.start() }
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
            guard credential.expiresAt > Date().timeIntervalSince1970 else { throw MobileRelayError.expired }
            let sessions = monitors.mapValues(\.sessions)
            let body = Self.snapshot(latestSnapshot, sessions: sessions, shareTitles: shareTaskTitles)
            let _: MobileOK = try await client.send("POST", "/v1/snapshot", token: credential.token, body: body)
            guard run == generation, !Task.isCancelled else { return }
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
            try await MobileCredentialStore.shared.delete()
            UserDefaults.standard.set(false, forKey: "mobileRelayPaired")
            if let old, let oldClient {
                let _: MobileOK = try await oldClient.send("DELETE", "/v1/session", token: old.token, body: MobileEmpty())
            }
            status = "Not connected"
        } catch {
            UserDefaults.standard.set(false, forKey: "mobileRelayPaired")
            status = "Sharing stopped on this Mac"
            errorMessage = "The server could not confirm disconnection. Remove the connection in iPhone Settings when online."
        }
    }

    private func stopLocal() {
        generation = UUID(); loop?.cancel(); loop = nil
        monitors.values.forEach { $0.stop() }; monitors = [:]
        credential = nil; client = nil; isConnected = false; lastSentAt = nil
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
