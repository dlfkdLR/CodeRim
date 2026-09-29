import ActivityKit
import AuthenticationServices
import Combine
import Foundation

@MainActor
final class MobileAppModel: ObservableObject {
    @Published var serverAddress = Bundle.main.object(forInfoDictionaryKey: "CodeRimRelayURL") as? String ?? ""
    @Published private(set) var signedIn = false
    @Published private(set) var busy = false
    @Published private(set) var challenge: MobileChallenge?
    @Published private(set) var pairing: MobilePairing?
    @Published private(set) var preferences = MobilePreferences()
    @Published private(set) var devices: [MobileDevice] = []
    @Published private(set) var providers: [MobileProviderOption] = []
    @Published private(set) var displayProviders: [MobileProviderOption] = []
    @Published private(set) var state = MobileActivityState.disconnected
    @Published private(set) var activityStatus = "Off"
    @Published private(set) var activityActive = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var statusMessage: String?
    private var credential: MobileCredential?
    private var client: (any MobileRelayServing)?
    private var loginChallenge: MobileChallenge?
    private var loginGeneration = UUID()
    private var observers: [String: Task<Void, Never>] = [:]
    private var registrations: [String: Task<Void, Never>] = [:]
    private var activityObservers: [String: Task<Void, Never>] = [:]
    private var foregroundPoll: Task<Void, Never>?
    private var restored = false
    private let credentials: any MobileCredentialStoring
    private let makeClient: @Sendable (URL) throws -> any MobileRelayServing
    private let appleState: @Sendable (String) async throws -> ASAuthorizationAppleIDProvider.CredentialState

    init(credentials: any MobileCredentialStoring = MobileCredentialStore.shared,
         makeClient: @escaping @Sendable (URL) throws -> any MobileRelayServing = { try MobileRelayClient(endpoint: $0) },
         appleState: @escaping @Sendable (String) async throws -> ASAuthorizationAppleIDProvider.CredentialState = {
             try await ASAuthorizationAppleIDProvider().credentialState(forUserID: $0)
         }) {
        self.credentials = credentials; self.makeClient = makeClient; self.appleState = appleState
    }

    func restore() async {
        guard !restored else { return }; restored = true
        busy = true
        let generation = loginGeneration
        defer { busy = false }
        do {
            if let saved = try await credentials.load() {
                guard generation == loginGeneration else { return }
                guard saved.expiresAt > Date().timeIntervalSince1970 else { throw MobileRelayError.expired }
                if let user = saved.appleUserID {
                    let status = try await appleState(user)
                    guard generation == loginGeneration else { return }
                    guard status == .authorized else { throw MobileRelayError.expired }
                }
                try activate(saved)
                await refresh()
            } else if !serverAddress.isEmpty {
                busy = false
                await prepareSignIn()
            }
        } catch {
            guard generation == loginGeneration else { return }
            errorMessage = error.localizedDescription
            if case MobileRelayError.expired = error { await clearLocalLogin() }
        }
    }

    var selectionOptions: [MobileProviderOption] {
        var result = providers
        for id in preferences.providerIDs where !result.contains(where: { $0.id == id }) {
            let name = id == "codex" ? "Codex" : id == "claude" ? "Claude" : id
            result.append(MobileProviderOption(id: id, name: "\(name) · not connected"))
        }
        return result
    }

    func prepareSignIn() async {
        guard !busy, !signedIn else { return }
        busy = true; challenge = nil; errorMessage = nil
        defer { busy = false }
        do {
            let endpoint = try MobileRelayClient.validatedEndpoint(serverAddress)
            let client = try makeClient(endpoint)
            let challenge: MobileChallenge = try await client.send("POST", "/v1/auth/challenge", token: nil, body: MobileEmpty())
            self.client = client; self.challenge = challenge
            serverAddress = endpoint.absoluteString
        } catch { errorMessage = error.localizedDescription }
    }

    func changeServer() { challenge = nil; client = nil; errorMessage = nil }

    func configureAppleRequest(_ request: ASAuthorizationAppleIDRequest) {
        loginChallenge = challenge
        request.nonce = challenge?.nonce
        request.requestedScopes = []
        busy = true
    }

    func completeAppleSignIn(_ result: Result<ASAuthorization, Error>) async {
        defer { busy = false; loginChallenge = nil; challenge = nil }
        errorMessage = nil
        do {
            let authorization = try result.get()
            guard let apple = authorization.credential as? ASAuthorizationAppleIDCredential,
                  let data = apple.identityToken, let identity = String(data: data, encoding: .utf8),
                  let challenge = loginChallenge, challenge.expiresAt > Date().timeIntervalSince1970,
                  let client else { throw MobileRelayError.expired }
            let session: MobileSessionToken = try await client.send("POST", "/v1/auth/apple", token: nil,
                body: MobileAppleLogin(challengeID: challenge.id, identityToken: identity))
            let saved = MobileCredential(endpoint: client.endpoint, session: session, appleUserID: apple.user)
            // Never attach a new account to a previous account's Activity/push token.
            await clearLocalLogin()
            do { try await credentials.save(saved) }
            catch {
                let _: MobileOK? = try? await client.send("DELETE", "/v1/session", token: saved.token, body: MobileEmpty())
                throw error
            }
            try activate(saved)
            await refresh()
        } catch let error as ASAuthorizationError where error.code == .canceled {
            statusMessage = "Sign-in was canceled. You can connect again."
        } catch { errorMessage = error.localizedDescription }
    }

    private func activate(_ saved: MobileCredential) throws {
        client = try makeClient(saved.endpoint)
        credential = saved; serverAddress = saved.endpoint.absoluteString
        signedIn = true; loginGeneration = UUID()
        observeActivities()
    }

    func setForeground(_ active: Bool) {
        foregroundPoll?.cancel(); foregroundPoll = nil
        guard active else { return }
        foregroundPoll = Task { [weak self] in
            while !Task.isCancelled {
                if self?.signedIn == true { await self?.refresh() }
                do { try await Task.sleep(for: .seconds(15)) } catch { break }
            }
        }
    }

    func refresh() async {
        guard let credential, let client else { return }
        let generation = loginGeneration
        do {
            guard credential.expiresAt > Date().timeIntervalSince1970 else { throw MobileRelayError.expired }
            let response: MobileSnapshotResponse = try await client.get("/v1/snapshot", token: credential.token)
            guard generation == loginGeneration else { return }
            _ = try await apply(response, token: credential.token)
        } catch {
            guard generation == loginGeneration else { return }
            errorMessage = error.localizedDescription
            if case MobileRelayError.rejected(401) = error { await clearLocalLogin() }
            if case MobileRelayError.expired = error { await clearLocalLogin() }
        }
    }

    private func apply(_ response: MobileSnapshotResponse, token: String, retryStale: Bool = true) async throws -> Bool {
        guard credential?.token == token, (response.state.viewRevision ?? 0) >= (state.viewRevision ?? 0) else { return false }
        let connection = MobileActivityConnection.id(for: token)
        let current = Activity<CodeRimActivityAttributes>.activities.filter { $0.attributes.connectionID == connection }
            .map(\.content.state).max { ($0.viewRevision ?? 0) < ($1.viewRevision ?? 0) }
        if let current, (current.viewRevision ?? 0) > (response.state.viewRevision ?? 0) {
            // Keep the coherent state/list pair until the relay catches up with the Island.
            // One fresh request handles an in-flight old response without a retry loop.
            if retryStale, let client {
                let latest: MobileSnapshotResponse = try await client.get("/v1/snapshot", token: token)
                return try await apply(latest, token: token, retryStale: false)
            }
            return false
        }
        preferences = response.preferences; providers = response.availableProviders; devices = response.devices ?? []
        displayProviders = response.displayProviders ?? []
        state = response.state
        observeActivities()
        for activity in Activity<CodeRimActivityAttributes>.activities where activity.attributes.connectionID == connection && (activity.activityState == .active || activity.activityState == .stale) {
            guard (activity.content.state.viewRevision ?? 0) <= (response.state.viewRevision ?? 0) else { continue }
            await activity.update(ActivityContent(state: response.state, staleDate: Date(timeIntervalSince1970: response.state.staleAt)))
        }
        return true
    }

    /// Explicit, phone-specific focus. Filtering the rotation remains a separate setting.
    func showProvider(_ id: String) async -> Bool {
        guard !busy, let credential, let client, let deviceID = state.focus?.deviceID,
              displayProviders.contains(where: { $0.id == id }) else { return false }
        busy = true; errorMessage = nil
        let generation = loginGeneration
        defer { busy = false }
        do {
            guard credential.expiresAt > Date().timeIntervalSince1970 else { throw MobileRelayError.expired }
            let request = MobileNavigation(axis: "provider", direction: 1, expectedRevision: state.viewRevision ?? 0,
                                           providerID: id, deviceID: deviceID)
            let response: MobileSnapshotResponse = try await client.send("POST", "/v1/view", token: credential.token, body: request)
            guard generation == loginGeneration else { return false }
            let applied = try await apply(response, token: credential.token)
            guard generation == loginGeneration else { return false }
            guard applied, state.providers.first?.id == id, state.focus?.deviceID == deviceID else {
                errorMessage = "The Island changed while you were choosing. Please choose again."
                return false
            }
            return true
        } catch {
            guard generation == loginGeneration else { return false }
            errorMessage = "Could not change the service. Check your connection and try again."
            if case MobileRelayError.rejected(401) = error { await clearLocalLogin() }
            if case MobileRelayError.expired = error { await clearLocalLogin() }
            return false
        }
    }

    func makePairingCode() async {
        guard !busy, let credential, let client else { return }
        busy = true; errorMessage = nil; defer { busy = false }
        do { pairing = try await client.send("POST", "/v1/pairing", token: credential.token, body: MobileEmpty()) }
        catch { errorMessage = error.localizedDescription }
    }

    func selectProvider(_ id: String, enabled: Bool) async {
        guard !busy, let credential, let client else { return }
        var ids = preferences.providerIDs.isEmpty ? providers.map(\.id) : preferences.providerIDs
        if enabled && !ids.contains(id) { ids.append(id) }
        if !enabled { ids.removeAll { $0 == id } }
        guard (1...100).contains(ids.count) else { errorMessage = "Select at least one provider to display."; return }
        busy = true; errorMessage = nil; defer { busy = false }
        do {
            let next = MobilePreferences(providerIDs: ids)
            let _: MobileOK = try await client.send("PUT", "/v1/preferences", token: credential.token, body: next)
            preferences = next
            await refresh()
        } catch { errorMessage = error.localizedDescription }
    }

    func showAllProviders() async {
        guard !busy, let credential, let client else { return }
        busy = true; defer { busy = false }
        do {
            let _: MobileOK = try await client.send("PUT", "/v1/preferences", token: credential.token, body: MobilePreferences())
            await refresh()
        } catch { errorMessage = error.localizedDescription }
    }
    func removeDevice(_ device: MobileDevice) async {
        guard !busy, let credential, let client else { return }
        busy = true; defer { busy = false }
        do {
            let _: MobileOK = try await client.send("DELETE", "/v1/devices/" + device.id, token: credential.token, body: MobileEmpty())
            await refresh()
        } catch { errorMessage = error.localizedDescription }
    }

    func startActivity() async {
        guard signedIn, !busy else { return }
        busy = true; defer { busy = false }
        guard ActivityAuthorizationInfo().areActivitiesEnabled else {
            errorMessage = "Allow Live Activities for CodeRim in iPhone Settings."; return
        }
        do {
            if let existing = Activity<CodeRimActivityAttributes>.activities.first(where: { $0.attributes.connectionID == credential.map { MobileActivityConnection.id(for: $0.token) } && ($0.activityState == .active || $0.activityState == .stale) }) {
                observe(existing); return
            }
            let activity = try Activity.request(attributes: CodeRimActivityAttributes(connectionID: credential.map { MobileActivityConnection.id(for: $0.token) } ?? ""),
                content: ActivityContent(state: state, staleDate: Date(timeIntervalSince1970: state.staleAt)), pushType: .token)
            observe(activity)
            errorMessage = nil
        } catch { errorMessage = "Could not start the Live Activity. Check permissions and your connection." }
    }

    private func observeActivities() {
        let activities = Activity<CodeRimActivityAttributes>.activities.filter { $0.attributes.connectionID == credential.map { MobileActivityConnection.id(for: $0.token) } && ($0.activityState == .active || $0.activityState == .stale) }
        activityActive = !activities.isEmpty
        if activities.isEmpty { activityStatus = "Off · Tap Show in Dynamic Island" }
        for activity in activities { observe(activity) }
    }
    private func observe(_ activity: Activity<CodeRimActivityAttributes>) {
        activityActive = true
        guard observers[activity.id] == nil else { return }
        activityStatus = "Connecting push updates"
        if let token = activity.pushToken { register(token, activity: activity) }
        observers[activity.id] = Task { [weak self] in
            for await token in activity.pushTokenUpdates {
                guard !Task.isCancelled else { break }
                self?.register(token, activity: activity)
            }
        }
        activityObservers[activity.id] = Task { [weak self] in
            for await status in activity.activityStateUpdates {
                guard !Task.isCancelled else { break }
                if status == .dismissed || status == .ended {
                    self?.observers.removeValue(forKey: activity.id)?.cancel()
                    self?.registrations.removeValue(forKey: activity.id)?.cancel()
                    self?.activityObservers.removeValue(forKey: activity.id)
                    self?.observeActivities()
                    // User dismissal is respected; a foreground settings action starts a new session.
                    break
                }
            }
        }
    }
    private func register(_ token: Data, activity: Activity<CodeRimActivityAttributes>) {
        guard let credential, let client else { return }
        registrations[activity.id]?.cancel()
        let generation = loginGeneration
        let hex = token.map { String(format: "%02x", $0) }.joined()
        registrations[activity.id] = Task { [weak self] in
            for delay in [0, 2, 5, 15, 30, 60] {
                do {
                    if delay > 0 { try await Task.sleep(for: .seconds(delay)) }
                    guard !Task.isCancelled, self?.loginGeneration == generation else { return }
                    let _: MobileOK = try await client.send("POST", "/v1/activities", token: credential.token,
                        body: MobileActivityRegistration(activityID: activity.id, pushToken: hex))
                    guard !Task.isCancelled, self?.loginGeneration == generation else { return }
                    self?.activityStatus = "Live"; return
                } catch {
                    if Task.isCancelled { return }
                    self?.activityStatus = "Retrying push connection"
                }
            }
            self?.activityStatus = "Push connection failed · Start again"
        }
    }

    func stopActivity() async {
        let wasBusy = busy; busy = true; defer { busy = wasBusy }
        for task in observers.values { task.cancel() }; observers = [:]
        for task in registrations.values { task.cancel() }; registrations = [:]
        for task in activityObservers.values { task.cancel() }; activityObservers = [:]
        for activity in Activity<CodeRimActivityAttributes>.activities { await activity.end(nil, dismissalPolicy: .immediate) }
        activityActive = false; activityStatus = "Off"
        if let credential, let client {
            do { let _: MobileOK = try await client.send("DELETE", "/v1/activities", token: credential.token, body: MobileEmpty()) }
            catch { errorMessage = "The Live Activity has ended on your iPhone. Connect again to retry cleanup on the server." }
        }
    }

    func signOut(deleteAccount: Bool = false) async {
        guard !busy, let credential, let client else { return }
        busy = true; errorMessage = nil; defer { busy = false }
        if !deleteAccount { await clearLocalLogin() }
        do {
            let _: MobileOK = try await client.send("DELETE", deleteAccount ? "/v1/account" : "/v1/session", token: credential.token, body: MobileEmpty())
            if deleteAccount { await clearLocalLogin() }
        } catch {
            errorMessage = deleteAccount ? error.localizedDescription
                : "Signed out on your iPhone. The server could not be reached; the remote session will be removed when it expires."
        }
    }
    private func clearLocalLogin() async {
        loginGeneration = UUID()
        self.credential = nil; signedIn = false
        await stopActivity()
        do { try await credentials.delete() }
        catch { errorMessage = error.localizedDescription }
        pairing = nil; preferences = MobilePreferences(); providers = []; displayProviders = []; devices = []; state = .disconnected
    }
}
