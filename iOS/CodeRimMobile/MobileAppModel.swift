import ActivityKit
import Combine
import Foundation
#if CODERIM_RUST_MOBILE_SELECTION
import CCodeRimMobile
#endif

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
    /// When the relay last answered, distinct from when the computer last measured usage.
    @Published private(set) var lastRefresh: Date?
    /// The notice the last failed refresh put up, so a later good refresh can take down that one and
    /// leave any other action's error alone.
    private var refreshError: String?
    private var credential: MobileCredential?
    private var client: (any MobileRelayServing)?
    private var loginGeneration = UUID()
    private var observers: [String: Task<Void, Never>] = [:]
    private var registrations: [String: Task<Void, Never>] = [:]
    private var activityObservers: [String: Task<Void, Never>] = [:]
    private var foregroundPoll: Task<Void, Never>?
    /// The one restore of the saved connection. The app's launch task and a QR link that opened the app
    /// both wait for it, so a link never finds the model busy restoring and is never dropped.
    private var restoring: Task<Void, Never>?
    private var pushAvailable = true
    private var serverPushAvailable = false
    private var foreground = false
    /// The person chose Stop showing; respected until the running tasks finish.
    private var following = MobileActivityFollowing()
    private var remoteActivities: [Task<Void, Never>] = []
    private let credentials: any MobileCredentialStoring
    private let makeClient: @Sendable (URL) throws -> any MobileRelayServing

    init(credentials: any MobileCredentialStoring = MobileCredentialStore.shared,
         makeClient: @escaping @Sendable (URL) throws -> any MobileRelayServing = { try MobileRelayClient(endpoint: $0) }) {
        self.credentials = credentials; self.makeClient = makeClient
    }

    func restore() async {
        // Busy from the moment restoring begins, not from when its task first runs.
        if restoring == nil { busy = true; restoring = Task { await self.restoreSaved() } }
        await restoring?.value
    }
    private func restoreSaved() async {
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
            errorMessage = Self.message(for: error)
            if case MobileRelayError.expired = error { await clearLocalLogin() }
        }
    }

    /// What a person reads for a failure: a dropped signal says so, instead of a system error code.
    static func message(for error: Error) -> String {
        if let url = error as? URLError {
            switch url.code {
            case .notConnectedToInternet, .networkConnectionLost, .dataNotAllowed, .internationalRoamingOff:
                return "You're offline. Showing the last update from your computer."
            case .timedOut, .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed, .secureConnectionFailed:
                return "Can't reach the CodeRim relay right now. It will try again."
            default: break
            }
        }
        return error.localizedDescription
    }

    var selectionOptions: [MobileProviderOption] {
        preferences.selectionOptions(available: providers)
    }

    /// Joins the computer whose QR code was scanned. The first scan also creates
    /// this iPhone's anonymous account on that computer's relay.
    func connect(_ link: MobilePairingLink) async {
        // A QR code that opened the app arrives while the saved connection is still being restored.
        await restore()
        guard !busy else { return }
        busy = true; errorMessage = nil; statusMessage = nil
        defer { busy = false }
        let request = MobilePairingRequest(id: link.id, secret: link.secret)
        do {
            if let current = credential, let client, MobileRelayClient.sameOrigin(current.endpoint, link.relay) {
                let joined: MobileClaimResult = try await client.send("POST", "/v1/pairing/claim", token: current.token, body: request)
                statusMessage = "\(joined.name) is connected."
                await refresh()
                return
            }
            // The QR code carries the server its computer uses: follow it rather than asking for an address.
            // The account there is made and the computer joined before anything of the current connection is
            // let go, so an unreachable server or a used code leaves the working connection as it was.
            let client = try makeClient(link.relay)
            let (saved, joined) = try await MobileEnrollment.enroll(link, client: client, credentials: credentials)
            // Joined and saved: now the previous server's session is ended, on a best-effort basis.
            if let old = credential, let oldClient = self.client {
                let _: MobileOK? = try? await oldClient.send("DELETE", "/v1/session", token: old.token, body: MobileEmpty())
            }
            await clearLocalLogin(forgetSaved: false)
            try activate(saved)
            statusMessage = "\(joined.name) is connected."
            await refresh()
        } catch MobileRelayError.rejected(401) {
            errorMessage = "This QR code has expired or was already used. Show a new one on your computer."
        } catch MobileRelayError.rejected(409) {
            errorMessage = "This QR code was already used. Show a new one on your computer."
        } catch { errorMessage = Self.message(for: error) }
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
                // A token issued before this launch may not be emitted again as an update.
                if let token = Activity<CodeRimActivityAttributes>.pushToStartToken {
                    await self?.registerStarter(token, generation: generation)
                }
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
    private static func pushTokenText(_ token: Data) -> String {
        #if CODERIM_RUST_MOBILE_DIGEST
        guard !token.isEmpty else { return "" }
        let (length, overflow) = token.count.multipliedReportingOverflow(by: 2)
        if !overflow {
            let hex = token.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) in
                String(unsafeUninitializedCapacity: length) { output in
                    cr_mobile_push_token_hex(bytes.bindMemory(to: UInt8.self).baseAddress, bytes.count,
                                             output.baseAddress, output.count) ? length : 0
                }
            }
            if !hex.isEmpty { return hex }
        }
        #endif
        return token.map { String(format: "%02x", $0) }.joined()
    }
    private func registerStarter(_ token: Data, generation: UUID) async {
        guard loginGeneration == generation, let credential, let client else { return }
        let hex = Self.pushTokenText(token)
        #if CODERIM_RUST_MOBILE_SELECTION
        await MobilePushRegistration.run(activity: false, sameLogin: { self.loginGeneration == generation }) {
            let _: MobileOK = try await client.send("POST", "/v1/push-to-start", token: credential.token,
                                                     body: MobilePushToStart(pushToken: hex))
        }
        #else
        for delay in [0, 5, 30, 120] {
            do {
                if delay > 0 { try await Task.sleep(for: .seconds(delay)) }
                guard loginGeneration == generation else { return }
                let _: MobileOK = try await client.send("POST", "/v1/push-to-start", token: credential.token, body: MobilePushToStart(pushToken: hex))
                return
            } catch { if Task.isCancelled { return } }
        }
        #endif
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

    /// External URLs may navigate, but enrollment requires the app's scanner or paste action.
    func handleExternalURL(_ url: URL) {
        guard url.scheme == "coderim", url.host == "dashboard",
              url.user == nil, url.password == nil, url.port == nil,
              url.path.isEmpty, url.query == nil, url.fragment == nil else { return }
        openDashboard()
    }

    func openDashboard() { dashboardRequest += 1 }

    func refresh() async {
        guard let credential, let client else { return }
        let generation = loginGeneration
        do {
            let response: MobileSnapshotResponse = try await client.get("/v1/snapshot", token: credential.token)
            guard generation == loginGeneration else { return }
            _ = try await apply(response, token: credential.token)
            guard generation == loginGeneration else { return }
            // The relay answered: clear the notice a failed refresh raised, and only that one.
            if let shown = refreshError, errorMessage == shown { errorMessage = nil }
            refreshError = nil
        } catch {
            guard generation == loginGeneration else { return }
            errorMessage = Self.message(for: error)
            refreshError = errorMessage
            if case MobileRelayError.rejected(401) = error { await clearLocalLogin() }
            if case MobileRelayError.expired = error { await clearLocalLogin() }
        }
    }

    private func apply(_ response: MobileSnapshotResponse, token: String, retryStale: Bool = true) async throws -> Bool {
        guard credential?.token == token else { return false }
        #if CODERIM_RUST_MOBILE_SELECTION
        let nextPush = response.pushConfigured ?? false
        let plan = response.state.refreshHeader(over: state, previousPush: serverPushAvailable, nextPush: nextPush)
        serverPushAvailable = nextPush
        if plan.registerPush {
            let connection = MobileActivityConnection.id(for: token)
            for activity in Activity<CodeRimActivityAttributes>.activities where activity.attributes.connectionID == connection {
                if let pushToken = activity.pushToken { register(pushToken, activity: activity) }
            }
        }
        guard plan.checkActivities else { return false }
        let connection = MobileActivityConnection.id(for: token)
        let decision = response.state.resolveSnapshot(against: Activity<CodeRimActivityAttributes>.activities, mayRetry: retryStale) {
            $0.attributes.connectionID == connection ? $0.content.state : nil
        }
        switch decision {
        case .keep: return false
        case .retry:
            // Keep the screen and its lists together until one fresh relay response catches up.
            guard let client else { return false }
            let latest: MobileSnapshotResponse = try await client.get("/v1/snapshot", token: token)
            return try await apply(latest, token: token, retryStale: false)
        case .accept: break
        }
        #else
        let hadServerPush = serverPushAvailable
        serverPushAvailable = response.pushConfigured ?? false
        if !hadServerPush && serverPushAvailable {
            let connection = MobileActivityConnection.id(for: token)
            for activity in Activity<CodeRimActivityAttributes>.activities where activity.attributes.connectionID == connection {
                if let pushToken = activity.pushToken { register(pushToken, activity: activity) }
            }
        }
        guard response.state.supersedes(state) else { return false }
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
        #endif
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

    /// While relay push is unavailable, follow tasks on screen and end the local Activity
    /// two minutes after work stops. With APNs enabled the relay owns remote starts/ends;
    /// starting locally too could create a duplicate Island.
    private func followTasks() async {
        guard foreground, signedIn else { return }
        let action = following.observe(hasTasks: state.workingCount + state.waitingCount > 0,
                                       serverPush: serverPushAvailable, active: activityActive, busy: busy,
                                       authorized: { ActivityAuthorizationInfo().areActivitiesEnabled })
        switch action {
        case .start: await startActivity(userInitiated: false)
        case .stop: await stopActivity()
        case .none: break
        }
    }

    /// The person's own Stop showing, which automatic starts respect until the tasks finish.
    func stopShowing() async {
        following.stopByUser()
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

    /// Choose a paired computer directly, without stepping through every computer in the Island.
    func showDevice(_ id: String) async -> Bool {
        guard !busy, let credential, let client, devices.contains(where: { $0.id == id }) else { return false }
        busy = true; errorMessage = nil
        let generation = loginGeneration
        defer { busy = false }
        do {
            let request = MobileNavigation(axis: "device-select", direction: 1,
                                           expectedRevision: state.viewRevision ?? 0, deviceID: id)
            let response: MobileSnapshotResponse = try await client.send("POST", "/v1/view", token: credential.token, body: request)
            guard generation == loginGeneration else { return false }
            let applied = try await apply(response, token: credential.token)
            guard generation == loginGeneration else { return false }
            guard applied, state.focus?.deviceID == id else {
                errorMessage = "The Island changed while you were choosing. Please choose again."
                return false
            }
            return true
        } catch {
            guard generation == loginGeneration else { return false }
            errorMessage = "Could not change the computer. Check your connection and try again."
            if case MobileRelayError.rejected(401) = error { await clearLocalLogin() }
            if case MobileRelayError.expired = error { await clearLocalLogin() }
            return false
        }
    }

    func selectProvider(_ id: String, enabled: Bool) async {
        guard !busy, credential != nil, client != nil else { return }
        guard let next = preferences.updating(id, enabled: enabled, available: providers) else {
            errorMessage = "Select at least one provider to display."
            return
        }
        errorMessage = nil
        await updateSettings("PUT", "/v1/preferences", body: next) { preferences = next }
    }

    func showAllProviders() async {
        await updateSettings("PUT", "/v1/preferences", body: MobilePreferences())
    }
    func removeDevice(_ device: MobileDevice) async {
        await updateSettings("DELETE", "/v1/devices/" + device.id, body: MobileEmpty())
    }

    private func updateSettings<Body: Encodable & Sendable>(
        _ method: String, _ path: String, body: Body, applying update: () -> Void = {}
    ) async {
        guard !busy, let credential, let client else { return }
        busy = true; defer { busy = false }
        let generation = loginGeneration
        do {
            let _: MobileOK = try await client.send(method, path, token: credential.token, body: body)
            // A sign-out or foreground 401 while this was in flight wins.
            guard generation == loginGeneration else { return }
            update()
            await refresh()
        } catch { settingsFailed(error, generation: generation) }
    }

    /// A late failure from an earlier login says nothing about the current one.
    private func settingsFailed(_ error: Error, generation: UUID) {
        guard generation == loginGeneration else { return }
        errorMessage = Self.message(for: error)
        if case MobileRelayError.rejected(401) = error { Task { await clearLocalLogin() } }
    }

    /// Automatic starts and token rotation must never clear a previous Stop on the relay.
    func resumeActivityUpdates(userInitiated: Bool) async throws {
        guard userInitiated, let credential, let client else { return }
        let generation = loginGeneration
        let _: MobileOK = try await client.send("POST", "/v1/activities/resume", token: credential.token, body: MobileEmpty())
        guard generation == loginGeneration else { throw CancellationError() }
        following.didResume()
    }

    func startActivity(userInitiated: Bool = true) async {
        guard signedIn, !busy else { return }
        busy = true; defer { busy = false }
        guard ActivityAuthorizationInfo().areActivitiesEnabled else {
            errorMessage = "Allow Live Activities for CodeRim in iPhone Settings."; return
        }
        do {
            try await resumeActivityUpdates(userInitiated: userInitiated)
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
        let hex = Self.pushTokenText(token)
        registrations[activity.id] = Task { [weak self] in
            #if CODERIM_RUST_MOBILE_SELECTION
            await MobilePushRegistration.run(activity: true,
                                             sameLogin: { self?.loginGeneration == generation },
                                             relayPush: { self?.serverPushAvailable == true },
                                             report: { self?.activityStatus = $0 }) {
                let _: MobileOK = try await client.send("POST", "/v1/activities", token: credential.token,
                    body: MobileActivityRegistration(activityID: activity.id, pushToken: hex))
            }
            #else
            for delay in [0, 2, 5, 15, 30, 60] {
                do {
                    if delay > 0 { try await Task.sleep(for: .seconds(delay)) }
                    guard !Task.isCancelled, self?.loginGeneration == generation else { return }
                    let _: MobileOK = try await client.send("POST", "/v1/activities", token: credential.token,
                        body: MobileActivityRegistration(activityID: activity.id, pushToken: hex))
                    guard !Task.isCancelled, self?.loginGeneration == generation else { return }
                    self?.activityStatus = self?.serverPushAvailable == true ? "Live" : "Updates while this app is open"
                    return
                } catch {
                    if Task.isCancelled { return }
                    self?.activityStatus = "Retrying push connection"
                }
            }
            self?.activityStatus = "Push connection failed · Start again"
            #endif
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
            errorMessage = deleteAccount ? Self.message(for: error)
                : "Signed out on your iPhone. The server could not be reached; the remote session will be removed when it expires."
        }
    }
    /// `forgetSaved: false` when the saved connection was already replaced by the one taking over.
    private func clearLocalLogin(forgetSaved: Bool = true) async {
        loginGeneration = UUID()
        remoteActivities.forEach { $0.cancel() }; remoteActivities = []
        self.credential = nil; signedIn = false
        await stopActivity()
        if forgetSaved {
            do { try await credentials.delete() }
            catch { errorMessage = Self.message(for: error) }
        }
        serverNotice = nil; preferences = MobilePreferences(); providers = []; displayProviders = []; devices = []; state = .disconnected
        serverPushAvailable = false
    }
}


#if CODERIM_RUST_MOBILE_SELECTION
/// The relay receives the original Swift request. Rust sees the attempt and outcome only.
@MainActor
enum MobilePushRegistration {
    static func run(activity: Bool, sameLogin: () -> Bool,
                    relayPush: () -> Bool = { false }, report: (String) -> Void = { _ in },
                    sleep: (Int) async throws -> Void = { try await Task.sleep(for: .seconds($0)) },
                    send: () async throws -> Void) async {
        var attempt: UInt8 = 0
        var event = CR_REG_BEGIN
        while true {
            let next = cr_mobile_registration_step(activity, attempt, UInt8(event), Task.isCancelled, sameLogin(), relayPush())
            switch Int(next.notice) {
            case CR_REG_LIVE: report("Live")
            case CR_REG_FOREGROUND: report("Updates while this app is open")
            case CR_REG_RETRYING: report("Retrying push connection")
            case CR_REG_EXHAUSTED:
                report("Retrying push connection")
                report("Push connection failed · Start again")
            default: break
            }
            do {
                switch Int(next.action) {
                case CR_REG_SLEEP:
                    try await sleep(Int(next.delay))
                    event = CR_REG_WAKE
                case CR_REG_SEND:
                    try await send()
                    event = CR_REG_SUCCESS
                case CR_REG_ADVANCE:
                    attempt += 1
                    event = CR_REG_BEGIN
                default: return
                }
            } catch {
                event = CR_REG_ERROR
            }
        }
    }
}
#endif

/// State committed synchronously before the app starts or ends an Activity.
struct MobileActivityFollowing {
    enum Action: Int { case none, start, stop }
    private(set) var userStopped = false
    private(set) var autoStarted = false
    private(set) var idleSince: Date?

    mutating func stopByUser() {
        #if CODERIM_RUST_MOBILE_SELECTION
        _ = transition(event: CR_FOLLOW_USER_STOP)
        #else
        userStopped = true
        autoStarted = false
        #endif
    }

    mutating func didResume() {
        #if CODERIM_RUST_MOBILE_SELECTION
        _ = transition(event: CR_FOLLOW_RESUMED)
        #else
        userStopped = false
        #endif
    }

    mutating func observe(hasTasks: Bool, serverPush: Bool, active: Bool, busy: Bool,
                          authorized: () -> Bool, now: () -> Date = Date.init) -> Action {
        #if CODERIM_RUST_MOBILE_SELECTION
        let reading = cr_follow_observation(tasks: hasTasks, push: serverPush, active: active, busy: busy)
        return transition(event: CR_FOLLOW_REFRESH, observation: reading, authorized: authorized, now: now)
        #else
        if hasTasks {
            idleSince = nil
            guard !serverPush, !active, !userStopped, !busy, authorized() else { return .none }
            autoStarted = true
            return .start
        }
        userStopped = false
        guard active, autoStarted, !serverPush else { idleSince = nil; return .none }
        let since = idleSince ?? now()
        idleSince = since
        guard now().timeIntervalSince(since) >= 120 else { return .none }
        autoStarted = false
        idleSince = nil
        return .stop
        #endif
    }

    #if CODERIM_RUST_MOBILE_SELECTION
    private mutating func transition(event: Int, observation: cr_follow_observation = .init(),
                                     authorized: () -> Bool = { false }, now: () -> Date = Date.init) -> Action {
        let previous = cr_follow_state(stopped: userStopped, automatic: autoStarted)
        var result = cr_mobile_follow(previous, observation, UInt8(event), false, false, 0)
        var since: Date?
        switch Int(result.action) {
        case CR_FOLLOW_PERMISSION:
            result = cr_mobile_follow(previous, observation, UInt8(event), true, authorized(), 0)
        case CR_FOLLOW_CLOCK:
            let start = idleSince ?? now()
            since = start
            result = cr_mobile_follow(previous, observation, UInt8(event), true, false, now().timeIntervalSince(start))
        default: break
        }
        userStopped = result.state.stopped
        autoStarted = result.state.automatic
        switch Int(result.timer) {
        case CR_FOLLOW_CLEAR_TIMER: idleSince = nil
        case CR_FOLLOW_REMEMBER_TIMER: idleSince = since
        default: break
        }
        switch Int(result.action) {
        case CR_FOLLOW_START: return .start
        case CR_FOLLOW_STOP: return .stop
        default: return .none
        }
    }
    #endif
}
