import Combine
import Foundation

/// Owns the estimate for every working session: feeds the coordinator the
/// monitors' sessions, keeps what it learns on disk, and seeds a fresh install
/// from the agents' own logs.
@MainActor
final class TurnETAStore: ObservableObject {
    /// Keyed `"<provider>\u{1}<session id>"`.
    @Published private(set) var snapshots: [String: TurnETATracker.Snapshot] = [:]

    private var coordinator: TurnETACoordinator
    private let file: URL
    private var compacting: Set<String> = []
    private var pendingSave: DispatchWorkItem?
    private var importTask: Task<Void, Never>?

    init(file: URL = TurnETAStore.defaultFile) {
        self.file = file
        let saved = (try? Data(contentsOf: file))
            .flatMap { try? JSONDecoder.eta.decode(TurnHistory.self, from: $0) }
        coordinator = TurnETACoordinator(history: saved ?? TurnHistory())
    }

    static var defaultFile: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)
            .first ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("CodeRim/TurnHistory.json")
    }

    /// Feed the monitors' current view. Called whenever any list changes.
    func absorb(_ sessions: [String: [AgentSession]], now: Date = Date()) {
        let result = coordinator.absorb(sessions, compacting: compacting, now: now)
        if result.snapshots != snapshots { snapshots = result.snapshots }
        if result.learned { scheduleSave() }
    }

    /// Sessions known to be compacting, from whatever signal reports it.
    func setCompacting(_ keys: Set<String>) { compacting = keys }

    /// Reads past turn lengths from the agents' logs once per launch, off the
    /// main thread, then merges them in. Safe to call again; duplicates are
    /// ignored by `TurnHistory`.
    func importPastTurns(claudeProjects: URL, codexSessions: URL) {
        guard importTask == nil else { return }
        importTask = Task.detached(priority: .utility) { [weak self] in
            let claude = TurnHistoryImporter.claudeSamples(projects: claudeProjects)
            let codex = TurnHistoryImporter.codexSamples(sessions: codexSessions)
            await MainActor.run {
                guard let self else { return }
                self.coordinator.merge(imported: claude, agent: "claude")
                self.coordinator.merge(imported: codex, agent: "codex")
                self.scheduleSave()
            }
        }
    }

    // MARK: - Persistence

    /// Coalesced: a turn ending and an import finishing should not each hit disk.
    private func scheduleSave() {
        pendingSave?.cancel()
        let history = coordinator.history
        let file = file
        let work = DispatchWorkItem {
            Task.detached(priority: .utility) {
                do {
                    try FileManager.default.createDirectory(
                        at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
                    try JSONEncoder.eta.encode(history).write(to: file, options: .atomic)
                } catch {
                    NotchLog.sessions.debug("turn history not saved: \(error.localizedDescription, privacy: .public)")
                }
            }
        }
        pendingSave = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 2, execute: work)
    }
}

private extension JSONEncoder {
    static let eta: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .secondsSince1970
        return encoder
    }()
}

private extension JSONDecoder {
    static let eta: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .secondsSince1970
        return decoder
    }()
}
