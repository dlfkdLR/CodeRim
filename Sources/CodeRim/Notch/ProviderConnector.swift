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
    private let pollInterval: Duration
    private let patience: TimeInterval

    init(provider: @escaping (String) -> NotchProvider?,
         connects: @escaping (String) async -> Bool,
         launch: @escaping (SignInRoute) -> Bool = { SignInLauncher.perform($0) },
         pollInterval: Duration = .seconds(4), patience: TimeInterval = 600) {
        self.provider = provider; self.connects = connects; self.launch = launch
        self.pollInterval = pollInterval; self.patience = patience
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
            default:
                guard self.launch(route) else {
                    self.states[id] = .failed("\(route.explanation) It could not be opened automatically."); return
                }
            }
            self.states[id] = .waiting(route.explanation)
            let deadline = Date().addingTimeInterval(self.patience)
            while !Task.isCancelled, Date() < deadline {
                try? await Task.sleep(for: self.pollInterval)
                guard !Task.isCancelled else { return }
                if await self.connects(id) { self.states[id] = .connected; return }
            }
            guard !Task.isCancelled else { return }
            self.states[id] = .failed("Sign-in was not detected. Choose Sign in to try again.")
        }
    }
}
