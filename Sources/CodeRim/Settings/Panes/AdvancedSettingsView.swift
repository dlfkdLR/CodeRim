import AppKit
import SwiftUI

struct AdvancedSettingsView: View {
    @AppStorage("debugLogging") private var debugLogging = false

    @State private var cliInstallMessage: String?

    var body: some View {
        SettingsForm {
            SettingsSection(title: "CLI & Widgets") {
                SettingsButtonRow(title: "Install CLI", systemImage: "terminal") {
                    do {
                        let path = try CLIInstaller.install()
                        cliInstallMessage = "Installed at \(path.path). Add ~/.local/bin to PATH, then run coderim."
                    } catch { cliInstallMessage = error.localizedDescription }
                }
            }
            SettingsNote(cliInstallMessage ?? "Use coderim in Terminal. Add CodeRim widgets from the desktop or Notification Center's Edit Widgets gallery; each widget can show any supported provider.")

            SettingsSection(title: "Diagnostics") {
                SettingsToggleRow("Enable debug logging", isOn: $debugLogging)
                SettingsButtonRow(title: "Open Log Folder", systemImage: "folder") { openLogFolder() }
            }
            SettingsNote("Never includes prompts, responses, source code, terminal output, or authentication tokens.")

            SettingsSection(title: "Codex Account Limit Source") {
                SettingsValueRow(title: "Mode", value: "Automatic")
                SettingsValueRow(title: "Provider", value: "Signed Codex app-server")
            }
            SettingsNote("Read-only local RPC request — no reset or purchase actions.")
        }
    }

    private func openLogFolder() {
        do {
            try FileManager.default.createDirectory(
                at: AppPaths.logDirectory,
                withIntermediateDirectories: true
            )
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o700],
                ofItemAtPath: AppPaths.logDirectory.path
            )
            NSWorkspace.shared.open(AppPaths.logDirectory)
        } catch {
            NSSound.beep()
        }
    }
}
