import Combine
import Foundation

/// Adding a provider is connecting it. Read what is already there; otherwise start the
/// provider's sign-in once and keep watching until the account appears, so there is no
/// second "Set up" step. Nothing here reads or stores a credential.
@MainActor
final class ProviderConnector: ObservableObject {
    enum State: Equatable {
        case checking
        /// A sign-in was opened; the connector is watching for the account to appear.
        case waiting(String)
        /// The provider needs an API key pasted in its settings.
        case needsKey(String)
        case connected
        case failed(String)
    }

    @Published private(set) var states: [String: State] = [:]
    private var tasks: [String: Task<Void, Never>] = [:]
    private let provider: (String) -> NotchProvider?
    private let connects: (String) async -> Bool
    private let launch: (SignInRoute) -> Bool
    private let preflight: (SignInRoute) -> String?
    private let runInApp: (InAppSignIn, @escaping @MainActor (String) -> Void) async -> InAppSignInRunner.Outcome
    private let allowBrowserSession: (String) -> Void
    /// A reason the provider cannot be read even though it may be signed in — a refused
    /// Keychain or browser prompt, a missing plan, a rejected key. Waiting would never end.
    private let blocker: (String) -> String?
    private let openInstallPage: (SignInRoute) -> Void
    private let pollInterval: Duration
    private let patience: TimeInterval

    init(provider: @escaping (String) -> NotchProvider?,
         connects: @escaping (String) async -> Bool,
         launch: @escaping (SignInRoute) -> Bool = { SignInLauncher.perform($0) },
         preflight: @escaping (SignInRoute) -> String? = { SignInLauncher.problem(with: $0) },
         openInstallPage: @escaping (SignInRoute) -> Void = { SignInLauncher.openInstallPage(for: $0) },
         runInApp: @escaping (InAppSignIn, @escaping @MainActor (String) -> Void) async -> InAppSignInRunner.Outcome = { await InAppSignInRunner.run($0, update: $1) },
         allowBrowserSession: @escaping (String) -> Void = { ProviderConnector.enableBrowserSession(for: $0) },
         blocker: @escaping (String) -> String? = { _ in nil },
         pollInterval: Duration = .seconds(4), patience: TimeInterval = 600) {
        self.provider = provider; self.connects = connects; self.launch = launch
        self.preflight = preflight; self.openInstallPage = openInstallPage
        self.runInApp = runInApp; self.allowBrowserSession = allowBrowserSession; self.blocker = blocker
        self.pollInterval = pollInterval; self.patience = patience
    }

    /// Turns on browser-session import for the provider's CodeRim settings, once the user chose
    /// to sign in on its website.
    static func enableBrowserSession(for id: String) {
        guard let descriptor = ExtendedProviderCatalog.descriptor(for: id) else { return }
        try? InAppSignInRunner.store(for: descriptor.id) { $0.provider.cookieSource = .auto }
    }

    func cancel(_ id: String) {
        runs.removeValue(forKey: id)
        tasks.removeValue(forKey: id)?.cancel()
        states.removeValue(forKey: id)
    }

    /// Which run owns each provider's state. A cancelled or superseded run can still be inside an
    /// await when it resumes; without this it would write "Connected" after the user pressed
    /// Cancel, or overwrite the newer run's result.
    private var runs: [String: UUID] = [:]

    func begin(_ id: String) {
        guard let provider = provider(id) else { return }
        tasks[id]?.cancel()
        let run = UUID()
        runs[id] = run
        states[id] = .checking
        tasks[id] = Task { [weak self] in
            guard let self else { return }
            let current: @MainActor () -> Bool = { !Task.isCancelled && self.runs[id] == run }
            let set: @MainActor (State) -> Void = { state in if current() { self.states[id] = state } }
            defer { if self.runs[id] == run { self.tasks.removeValue(forKey: id) } }
            if await self.connects(id) { set(.connected); return }
            guard current() else { return }
            let route = provider.signInRoute
            switch route {
            case .guided(let guided) where guided.opensSettings:
                set(.needsKey(guided.note)); return
            case .guidance(let text):
                set(.failed(text)); return
            case .modal:
                provider.presentSignIn()
            case .guided(let guided):
                if case .inApp(let kind) = guided.action {
                    set(.waiting(route.explanation))
                    let outcome = await self.runInApp(kind) { note in set(.waiting(note)) }
                    guard current() else { return }
                    if case .failed(let reason) = outcome { set(.failed(reason)); return }
                    if await self.connects(id) { set(.connected); return }
                    break
                }
                if guided.importsBrowserSession { self.allowBrowserSession(id) }
                fallthrough
            default:
                if let problem = self.preflight(route) {
                    self.openInstallPage(route)
                    set(.failed(problem)); return
                }
                guard self.launch(route) else {
                    set(.failed("\(route.explanation) It could not be opened automatically.")); return
                }
            }
            guard current() else { return }
            if case .waiting = self.states[id] {} else { set(.waiting(route.explanation)) }
            let deadline = Date().addingTimeInterval(self.patience)
            while current(), Date() < deadline {
                try? await Task.sleep(for: self.pollInterval)
                guard current() else { return }
                if await self.connects(id) { set(.connected); return }
                guard current() else { return }
                if let reason = self.blocker(id) { set(.failed(reason)); return }
            }
            set(.failed("Sign-in was not detected. Choose Sign in to try again."))
        }
    }
}
