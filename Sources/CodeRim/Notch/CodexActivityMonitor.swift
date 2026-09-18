import Combine
import Foundation

/// Turn boundaries keep activity visible through silent reasoning and long tools.
/// Catalogue timestamps are metadata updates, not evidence of a running turn.
@MainActor
final class CodexActivityMonitor: ObservableObject, AgentActivityMonitor {
    @Published private(set) var sessions: [AgentSession] = []
    var sessionsPublisher: AnyPublisher<[AgentSession], Never> { $sessions.eraseToAnyPublisher() }

    private let stateStore: URL
    private let desktopStore: URL
    private let profile: CodexProfile
    private let interval: TimeInterval
    private let reader = CodexActivityReader()
    private var timer: Timer?
    private var scan: Task<Void, Never>?

    init(
        profile: CodexProfile = .default(),
        stateStore: URL? = nil,
        desktopStore: URL? = nil,
        interval: TimeInterval = 2
    ) {
        self.profile = profile
        self.stateStore = stateStore ?? profile.stateURL
        self.desktopStore = desktopStore ?? profile.desktopStoreURL
        self.interval = interval
    }

    func start() {
        stop()
        rescan()
        let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.rescan() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        scan?.cancel()
        scan = nil
    }

    private func rescan() {
        guard scan == nil else { return }
        scan = Task { [weak self] in
            guard let self else { return }
            let found = await reader.read(stateStore: stateStore, desktopStore: desktopStore,
                                          profile: profile)
            guard !Task.isCancelled else { return }
            if found != sessions { sessions = found }
            scan = nil
        }
    }
}

actor CodexActivityReader {
    private var cache: [String: CodexTurnActivity.Reader] = [:]

    func read(stateStore: URL, desktopStore: URL, profile: CodexProfile = .default(),
              now: Date = Date()) -> [AgentSession] {
        let threads = CodexStore.threads(in: stateStore, desktopStore: desktopStore, limit: 64)
        let paths = Set(threads.map { $0.rollout.path })
        cache = cache.filter { paths.contains($0.key) }
        return threads.compactMap { thread in
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: thread.rollout.path),
                  let modified = attributes[.modificationDate] as? Date,
                  // Bound orphaned turns after a client crash with no end event.
                  now.timeIntervalSince(modified) < 6 * 60 * 60 else { return nil }
            var reader = cache[thread.rollout.path] ?? CodexTurnActivity.Reader()
            let event = reader.read(thread.rollout)
            cache[thread.rollout.path] = reader
            guard let event else { return nil }
            // Keep ended sessions briefly so completion detection can observe busy -> idle.
            guard event.isRunning || now.timeIntervalSince(event.since) < 90 else { return nil }
            return AgentSession(id: "\(profile.id).\(thread.id)", name: thread.projectName,
                                detail: thread.displayTitle, state: event.isRunning ? .busy : .idle,
                                waitingFor: nil, since: event.since, codexThreadID: thread.id,
                                parentThread: thread.parentThread)
        }
    }
}
