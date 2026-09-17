import ServiceManagement
import SwiftUI

@MainActor
final class LaunchAtLoginService: ObservableObject {
    @Published private(set) var status = SMAppService.mainApp.status
    @Published private(set) var errorMessage: String?

    var isEnabled: Bool { status == .enabled }

    var statusText: String {
        switch status {
        case .enabled: "Enabled"
        case .notRegistered: "Off"
        case .requiresApproval: "Approval required"
        case .notFound: "Unavailable"
        @unknown default: "Unknown"
        }
    }

    func refresh() {
        status = SMAppService.mainApp.status
    }

    func setEnabled(_ enabled: Bool) {
        errorMessage = nil
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
        } catch {
            errorMessage = error.localizedDescription
        }
        refresh()
    }

    func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}
