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
        tasks.removeValue(forKey: id)?.cancel()
        states.removeValue(forKey: id)
    }

    func begin(_ id: String) {
        guard let provider = provider(id) else { return }
        tasks[id]?.cancel()
        states[id] = .checking
        tasks[id] = Task { [weak self] in
            guard let self else { return }
            defer { if !Task.isCancelled { self.tasks.removeValue(forKey: id) } }
            if await self.connects(id) { self.states[id] = .connected; return }
            guard !Task.isCancelled else { return }
            let route = provider.signInRoute
            switch route {
            case .guided(let guided) where guided.opensSettings:
                self.states[id] = .needsKey(guided.note); return
            case .guidance(let text):
                self.states[id] = .failed(text); return
            case .modal:
                provider.presentSignIn()
            case .guided(let guided):
                if case .inApp(let kind) = guided.action {
                    self.states[id] = .waiting(route.explanation)
                    let outcome = await self.runInApp(kind) { note in
                        if !Task.isCancelled { self.states[id] = .waiting(note) }
                    }
                    guard !Task.isCancelled else { return }
                    if case .failed(let reason) = outcome { self.states[id] = .failed(reason); return }
                    if await self.connects(id) { self.states[id] = .connected; return }
                    break
                }
                if guided.importsBrowserSession { self.allowBrowserSession(id) }
                fallthrough
            default:
                if let problem = self.preflight(route) {
                    self.openInstallPage(route)
                    self.states[id] = .failed(problem); return
                }
                guard self.launch(route) else {
                    self.states[id] = .failed("\(route.explanation) It could not be opened automatically."); return
                }
            }
            if case .waiting = self.states[id] {} else { self.states[id] = .waiting(route.explanation) }
            let deadline = Date().addingTimeInterval(self.patience)
            while !Task.isCancelled, Date() < deadline {
                try? await Task.sleep(for: self.pollInterval)
                guard !Task.isCancelled else { return }
                if await self.connects(id) { self.states[id] = .connected; return }
                if let reason = self.blocker(id) { self.states[id] = .failed(reason); return }
            }
            guard !Task.isCancelled else { return }
            self.states[id] = .failed("Sign-in was not detected. Choose Sign in to try again.")
        }
    }
}
