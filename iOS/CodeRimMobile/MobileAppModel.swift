import ActivityKit
import Combine
import Foundation

@MainActor
final class MobileAppModel: ObservableObject {
    @Published private(set) var serverAddress = ""
    @Published private(set) var signedIn = false
    @Published private(set) var busy = false
    /// The relay's own notice while it is rationing its free daily allowance.
    @Published private(set) var serverNotice: String?
    @Published private(set) var preferences = MobilePreferences()
    @Published private(set) var devices: [MobileDevice] = []
    @Published private(set) var providers: [MobileProviderOption] = []
    @Published private(set) var displayProviders: [MobileProviderOption] = []
    @Published private(set) var state = MobileActivityState.disconnected
    @Published private(set) var activityStatus = "Off"
    @Published private(set) var activityActive = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var statusMessage: String?
    /// Bumped when the Island or a link asks for the dashboard, so the root can pop back to it.
    @Published private(set) var dashboardRequest = 0
    /// When the relay last answered, for "Updated … ago".
    @Published private(set) var lastRefresh: Date?
    private var credential: MobileCredential?
    private var client: (any MobileRelayServing)?
    private var loginGeneration = UUID()
    private var observers: [String: Task<Void, Never>] = [:]
    private var registrations: [String: Task<Void, Never>] = [:]
    private var activityObservers: [String: Task<Void, Never>] = [:]
    private var foregroundPoll: Task<Void, Never>?
    private var restored = false
    private var pushAvailable = true
    private var foreground = false
    /// The person chose Stop showing; respected until the running tasks finish.
    private var userStopped = false
    private var autoStarted = false
    private var idleSince: Date?
    private var remoteActivities: [Task<Void, Never>] = []
    private let credentials: any MobileCredentialStoring
    private let makeClient: @Sendable (URL) throws -> any MobileRelayServing

    init(credentials: any MobileCredentialStoring = MobileCredentialStore.shared,
         makeClient: @escaping @Sendable (URL) throws -> any MobileRelayServing = { try MobileRelayClient(endpoint: $0) }) {
        self.credentials = credentials; self.makeClient = makeClient
    }

    func restore() async {
        guard !restored else { return }; restored = true
        busy = true
        let generation = loginGeneration
        defer { busy = false }
        do {
            if let saved = try await credentials.load() {
                guard generation == loginGeneration else { return }
                // The relay extends a token each time it is used, so the expiry saved at sign-in is only
                // where it started; the relay alone decides, with 401, when this phone must sign in again.
                try activate(saved)
                await refresh()
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

    /// Joins the computer whose QR code was scanned. The first scan also creates
    /// this iPhone's anonymous account on that computer's relay.
    func connect(_ link: MobilePairingLink) async {
        guard !busy else { return }
        busy = true; errorMessage = nil; statusMessage = nil
        defer { busy = false }
        do {
            // The QR code carries the server its computer uses: follow it rather than asking for an
            // address. The old server's session is ended on a best-effort basis.
            if let old = credential, old.endpoint != link.relay {
                if let oldClient = client {
                    let _: MobileOK? = try? await oldClient.send("DELETE", "/v1/session", token: old.token, body: MobileEmpty())
                }
                await clearLocalLogin()
            }
            if credential == nil {
                let client = try makeClient(link.relay)
                let session: MobileSessionToken = try await client.send("POST", "/v1/accounts", token: nil, body: MobileEmpty())
                let saved = MobileCredential(endpoint: link.relay, session: session)
                await clearLocalLogin()
                do { try await credentials.save(saved) }
                catch {
                    let _: MobileOK? = try? await client.send("DELETE", "/v1/account", token: saved.token, body: MobileEmpty())
                    throw error
                }
                try activate(saved)
            }
            guard let credential, let client else { return }
            let joined: MobileClaimResult = try await client.send("POST", "/v1/pairing/claim", token: credential.token,
                body: MobilePairingRequest(id: link.id, secret: link.secret))
            statusMessage = "\(joined.name) is connected."
            await refresh()
        } catch MobileRelayError.rejected(401) {
            errorMessage = "This QR code has expired or was already used. Show a new one on your computer."
        } catch MobileRelayError.rejected(409) {
            errorMessage = "This QR code was already used. Show a new one on your computer."
        } catch { errorMessage = error.localizedDescription }
    }

    private func activate(_ saved: MobileCredential) throws {
        client = try makeClient(saved.endpoint)
        credential = saved; serverAddress = saved.endpoint.absoluteString
        signedIn = true; loginGeneration = UUID()
        observeActivities()
        observeRemoteStarts()
    }

    /// The relay starts the Island itself when a task begins (push-to-start), so
    /// the phone hands it a start token and adopts any Activity it was given.
    private func observeRemoteStarts() {
        remoteActivities.forEach { $0.cancel() }
        let generation = loginGeneration
        remoteActivities = [
            Task { [weak self] in
                for await token in Activity<CodeRimActivityAttributes>.pushToStartTokenUpdates {
                    guard !Task.isCancelled else { break }
                    await self?.registerStarter(token, generation: generation)
                }
            },
            Task { [weak self] in
                for await activity in Activity<CodeRimActivityAttributes>.activityUpdates {
                    guard !Task.isCancelled, let self, self.loginGeneration == generation,
                          activity.attributes.connectionID == self.credential.map({ MobileActivityConnection.id(for: $0.token) })
                    else { continue }
                    self.observe(activity)
                }
            },
        ]
    }
    private func registerStarter(_ token: Data, generation: UUID) async {
        guard loginGeneration == generation, let credential, let client else { return }
        let hex = token.map { String(format: "%02x", $0) }.joined()
        for delay in [0, 5, 30, 120] {
            do {
                if delay > 0 { try await Task.sleep(for: .seconds(delay)) }
                guard loginGeneration == generation else { return }
                let _: MobileOK = try await client.send("POST", "/v1/push-to-start", token: credential.token, body: MobilePushToStart(pushToken: hex))
                return
            } catch { if Task.isCancelled { return } }
        }
    }

    func setForeground(_ active: Bool) {
        foreground = active
        foregroundPoll?.cancel(); foregroundPoll = nil
        guard active else { return }
        // Only while the app is on screen; computers send changes as they happen.
        foregroundPoll = Task { [weak self] in
            while !Task.isCancelled {
                if self?.signedIn == true { await self?.refresh() }
                do { try await Task.sleep(for: .seconds(5)) } catch { break }
            }
        }
    }

    func openDashboard() { dashboardRequest += 1 }

    func refresh() async {
        guard let credential, let client else { return }
        let generation = loginGeneration
        do {
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
        guard credential?.token == token, response.state.supersedes(state) else { return false }
        let connection = MobileActivityConnection.id(for: token)
        let current = Activity<CodeRimActivityAttributes>.activities.filter { $0.attributes.connectionID == connection }
            .map(\.content.state).max { !$0.supersedes($1) }
        if let current, !response.state.supersedes(current) {
            // Keep the coherent state/list pair until the relay catches up with the Island.
            // One fresh request handles an in-flight old response without a retry loop.
            if retryStale, let client {
                let latest: MobileSnapshotResponse = try await client.get("/v1/snapshot", token: token)
                return try await apply(latest, token: token, retryStale: false)
            }
            return false
        }
        preferences = response.preferences; providers = response.availableProviders; devices = response.devices ?? []
        serverNotice = response.notice
        displayProviders = response.displayProviders ?? []
        state = response.state
        lastRefresh = Date()
        observeActivities()
        for activity in Activity<CodeRimActivityAttributes>.activities where activity.attributes.connectionID == connection && (activity.activityState == .active || activity.activityState == .stale) {
            guard response.state.supersedes(activity.content.state) else { continue }
            await activity.update(ActivityContent(state: response.state, staleDate: Date(timeIntervalSince1970: response.state.staleAt)))
        }
        await followTasks()
        return true
    }

    /// Without push (an app signed with a free Apple ID) nothing can start or end the Island
    /// remotely, so while the app is open it follows the tasks itself: shown once one runs,
    /// ended two minutes after the last one stops.
    private func followTasks() async {
        guard foreground, signedIn else { return }
        if state.workingCount + state.waitingCount > 0 {
            idleSince = nil
            guard !activityActive, !userStopped, !busy, ActivityAuthorizationInfo().areActivitiesEnabled else { return }
            autoStarted = true
            await startActivity()
        } else {
            userStopped = false
            guard activityActive, autoStarted, !pushAvailable else { idleSince = nil; return }
            let since = idleSince ?? Date(); idleSince = since
            guard Date().timeIntervalSince(since) >= 120 else { return }
            autoStarted = false; idleSince = nil
            await stopActivity()
        }
    }

    /// The person's own Stop showing, which automatic starts respect until the tasks finish.
    func stopShowing() async {
        userStopped = true; autoStarted = false
        await stopActivity()
    }

    /// Explicit, phone-specific focus. Filtering the rotation remains a separate setting.
    func showProvider(_ id: String) async -> Bool {
        guard !busy, let credential, let client, let deviceID = state.focus?.deviceID,
              displayProviders.contains(where: { $0.id == id }) else { return false }
        busy = true; errorMessage = nil
        let generation = loginGeneration
        defer { busy = false }
        do {
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

    func selectProvider(_ id: String, enabled: Bool) async {
        guard !busy, let credential, let client else { return }
        var ids = preferences.providerIDs.isEmpty ? providers.map(\.id) : preferences.providerIDs
        if enabled && !ids.contains(id) { ids.append(id) }
        if !enabled { ids.removeAll { $0 == id } }
        guard (1...100).contains(ids.count) else { errorMessage = "Select at least one provider to display."; return }
        busy = true; errorMessage = nil; defer { busy = false }
        let generation = loginGeneration
        do {
            let next = MobilePreferences(providerIDs: ids)
            let _: MobileOK = try await client.send("PUT", "/v1/preferences", token: credential.token, body: next)
            // A sign-out (or a 401 from a foreground refresh) while this was in flight wins.
            guard generation == loginGeneration else { return }
            preferences = next
            await refresh()
        } catch { settingsFailed(error, generation: generation) }
    }

    func showAllProviders() async {
        guard !busy, let credential, let client else { return }
        busy = true; defer { busy = false }
        let generation = loginGeneration
        do {
            let _: MobileOK = try await client.send("PUT", "/v1/preferences", token: credential.token, body: MobilePreferences())
            guard generation == loginGeneration else { return }
            await refresh()
        } catch { settingsFailed(error, generation: generation) }
    }
    func removeDevice(_ device: MobileDevice) async {
        guard !busy, let credential, let client else { return }
        busy = true; defer { busy = false }
        let generation = loginGeneration
        do {
            let _: MobileOK = try await client.send("DELETE", "/v1/devices/" + device.id, token: credential.token, body: MobileEmpty())
            guard generation == loginGeneration else { return }
            await refresh()
        } catch { settingsFailed(error, generation: generation) }
    }

    /// A late failure from an earlier login says nothing about the current one.
    private func settingsFailed(_ error: Error, generation: UUID) {
        guard generation == loginGeneration else { return }
        errorMessage = error.localizedDescription
        if case MobileRelayError.rejected(401) = error { Task { await clearLocalLogin() } }
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
            let attributes = CodeRimActivityAttributes(connectionID: credential.map { MobileActivityConnection.id(for: $0.token) } ?? "")
            let content = ActivityContent(state: state, staleDate: Date(timeIntervalSince1970: state.staleAt))
            let activity: Activity<CodeRimActivityAttributes>
            do {
                activity = try Activity.request(attributes: attributes, content: content, pushType: .token)
                pushAvailable = true
            } catch {
                // An app signed without the Push Notifications capability (a free Apple ID) cannot
                // take push tokens. The Island still works, refreshed while this app is open.
                activity = try Activity.request(attributes: attributes, content: content, pushType: nil)
                pushAvailable = false
            }
            observe(activity)
            errorMessage = nil
        } catch { errorMessage = "Could not start the Live Activity. Check permissions and your connection." }
    }

    private func observeActivities() {
        let activities = Activity<CodeRimActivityAttributes>.activities.filter { $0.attributes.connectionID == credential.map { MobileActivityConnection.id(for: $0.token) } && ($0.activityState == .active || $0.activityState == .stale) }
        activityActive = !activities.isEmpty
        if activities.isEmpty { activityStatus = "Appears while a task is running" }
        for activity in activities { observe(activity) }
    }
    private func observe(_ activity: Activity<CodeRimActivityAttributes>) {
        activityActive = true
        guard observers[activity.id] == nil else { return }
        guard pushAvailable else { activityStatus = "Updates while this app is open"; return observeEnd(activity) }
        activityStatus = "Connecting push updates"
        if let token = activity.pushToken { register(token, activity: activity) }
        observers[activity.id] = Task { [weak self] in
            for await token in activity.pushTokenUpdates {
                guard !Task.isCancelled else { break }
                self?.register(token, activity: activity)
            }
        }
        observeEnd(activity)
    }
    /// Forgets an Activity once the system or the user ends it.
    private func observeEnd(_ activity: Activity<CodeRimActivityAttributes>) {
        guard activityObservers[activity.id] == nil else { return }
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
        remoteActivities.forEach { $0.cancel() }; remoteActivities = []
        self.credential = nil; signedIn = false
        await stopActivity()
        do { try await credentials.delete() }
        catch { errorMessage = error.localizedDescription }
        serverNotice = nil; preferences = MobilePreferences(); providers = []; displayProviders = []; devices = []; state = .disconnected
    }
}
