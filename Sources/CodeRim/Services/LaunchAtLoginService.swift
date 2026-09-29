import ServiceManagement
import SwiftUI

/// Injectable synchronous operations, executed only by the background worker.
struct LaunchAtLoginClient: Sendable {
    var status: @Sendable () -> SMAppService.Status
    var setEnabled: @Sendable (Bool) throws -> Void

    static let system = Self(
        status: { SMAppService.mainApp.status },
        setEnabled: { enabled in
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
        }
    )
}

/// ServiceManagement performs synchronous IPC, including for `status`. Keep it
/// off the main actor and serialize mutations with their follow-up status read.
private actor LaunchAtLoginWorker {
    let client: LaunchAtLoginClient

    init(client: LaunchAtLoginClient) { self.client = client }

    func readStatus() -> SMAppService.Status { client.status() }

    func setEnabled(_ enabled: Bool) -> (status: SMAppService.Status, error: String?) {
        var message: String?
        do {
            try client.setEnabled(enabled)
        } catch {
            message = error.localizedDescription
        }
        return (client.status(), message)
    }
}

@MainActor
final class LaunchAtLoginService: ObservableObject {
    /// Retain the last result across pane switches without querying during init.
    static let shared = LaunchAtLoginService()

    @Published private(set) var status: SMAppService.Status?
    @Published private(set) var errorMessage: String?
    @Published private(set) var isBusy = false

    private let worker: LaunchAtLoginWorker
    private var operation: Task<Void, Never>?

    init(client: LaunchAtLoginClient = .system) {
        worker = LaunchAtLoginWorker(client: client)
    }

    var isEnabled: Bool { status == .enabled }
    var canSetEnabled: Bool { status != nil && !isBusy }

    var statusText: String {
        guard let status else { return "Checking…" }
        switch status {
        case .enabled: return "Enabled"
        case .notRegistered: return "Off"
        case .requiresApproval: return "Approval required"
        case .notFound: return "Unavailable"
        @unknown default: return "Unknown"
        }
    }

    @discardableResult
    func refresh() -> Task<Void, Never> {
        // Re-entry and activation notifications share the in-flight operation.
        if let operation { return operation }
        isBusy = true
        let task = Task {
            status = await worker.readStatus()
            isBusy = false
            operation = nil
        }
        operation = task
        return task
    }

    @discardableResult
    func setEnabled(_ enabled: Bool) -> Task<Void, Never>? {
        guard canSetEnabled else { return nil }
        errorMessage = nil
        isBusy = true
        let task = Task {
            let result = await worker.setEnabled(enabled)
            status = result.status
            errorMessage = result.error
            isBusy = false
            operation = nil
        }
        operation = task
        return task
    }

    func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}
