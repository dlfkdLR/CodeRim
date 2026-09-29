import SwiftUI

struct MobileSettingsSection: View {
    @ObservedObject private var connection = MobileConnectionStore.shared
    @State private var server = ""
    @State private var pairingCode = ""

    var body: some View {
        SettingsSection(title: "iPhone · Dynamic Island") {
            SettingsValueRow(title: "Status", value: connection.status)
            if connection.isConnected {
                SettingsValueRow(title: "Server", value: connection.serverAddress)
                SettingsToggleRow("Share task titles", isOn: $connection.shareTaskTitles)
                SettingsButtonRow(title: "Disconnect iPhone", systemImage: "iphone.slash") {
                    Task { await connection.disconnect() }
                }
            } else {
                SettingsRow(title: "Device name") {
                    TextField("Mac", text: $connection.deviceName).textFieldStyle(.roundedBorder).frame(maxWidth: 240)
                }
                SettingsRow(title: "Relay server") {
                    TextField("https://…", text: $server).textFieldStyle(.roundedBorder)
                        .frame(maxWidth: 240).accessibilityLabel("iPhone relay server")
                }
                SettingsRow(title: "Connection code") {
                    TextField("XXXXXXXX", text: $pairingCode).textFieldStyle(.roundedBorder)
                        .frame(maxWidth: 160).accessibilityLabel("iPhone connection code")
                }
                SettingsButtonRow(title: connection.isBusy ? "Connecting…" : "Connect iPhone", systemImage: "iphone") {
                    Task { await connection.pair(server: server, code: pairingCode); if connection.isConnected { pairingCode = "" } }
                }
            }
        }
        .disabled(connection.isBusy)
        SettingsNote("Sign in with Apple in the iPhone app, then enter its connection code here. Usage and task states sync while this Mac is awake. Task titles are off by default.")
        if let message = connection.errorMessage { SettingsNote(message, tint: .red) }
    }
}
