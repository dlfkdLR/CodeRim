import AppKit
import CodeRimShared
import CoreImage.CIFilterBuiltins
import SwiftUI

struct MobileSettingsSection: View {
    @ObservedObject private var connection = MobileConnectionStore.shared
    @State private var showsRelay = false

    var body: some View {
        SettingsSection(title: "iPhone · Dynamic Island") {
            SettingsValueRow(title: "Status", value: connection.status)
            if connection.isConnected {
                SettingsToggleRow("Share task titles", isOn: $connection.shareTaskTitles)
                SettingsButtonRow(title: "Disconnect iPhone", systemImage: "iphone.slash") {
                    Task { await connection.disconnect() }
                }
            } else if let link = connection.pairingLink {
                pairingCode(link)
                SettingsButtonRow(title: "Cancel", systemImage: "xmark.circle") { connection.cancelPairing() }
            } else {
                SettingsRow(title: "Device name") {
                    TextField("Mac", text: $connection.deviceName).textFieldStyle(.roundedBorder).frame(maxWidth: 240)
                }
                if showsRelay || connection.relayAddress.isEmpty {
                    SettingsRow(title: "Relay server") {
                        TextField("https://…", text: $connection.relayAddress).textFieldStyle(.roundedBorder)
                            .frame(maxWidth: 240).accessibilityLabel("iPhone relay server")
                    }
                }
                SettingsButtonRow(title: connection.isBusy ? "Connecting…" : "Connect iPhone", systemImage: "qrcode") {
                    connection.startPairing()
                }
            }
        }
        .disabled(connection.isBusy)
        SettingsNote("Open the CodeRim iPhone app and scan the QR code. Usage and task states sync while this Mac is awake, and the Island appears on its own while a task is running. Task titles are off by default.")
        if !connection.isConnected, connection.pairingLink == nil, !connection.relayAddress.isEmpty, !showsRelay {
            Button("Use a different relay server…") { showsRelay = true }.buttonStyle(.link).font(.caption)
        }
        if let notice = connection.serverNotice { SettingsNote(notice, tint: .orange) }
        if let message = connection.errorMessage { SettingsNote(message, tint: .red) }
    }

    @ViewBuilder private func pairingCode(_ link: MobilePairingLink) -> some View {
        HStack(alignment: .center, spacing: 18) {
            if let image = Self.qrImage(link.url.absoluteString) {
                Image(nsImage: image).interpolation(.none).resizable()
                    .frame(width: 168, height: 168)
                    .padding(8).background(.white, in: RoundedRectangle(cornerRadius: 10))
                    .accessibilityLabel("Pairing QR code")
            }
            VStack(alignment: .leading, spacing: 6) {
                Text("Scan with the CodeRim iPhone app").font(.headline)
                Text("The code works once and expires in five minutes.").foregroundStyle(.secondary)
                if let expiry = connection.pairingExpiresAt {
                    Text(timerInterval: Date()...max(Date(), expiry), countsDown: true)
                        .monospacedDigit().foregroundStyle(.secondary)
                }
            }
        }
        .padding(.vertical, 8)
    }

    static func qrImage(_ text: String) -> NSImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8); filter.correctionLevel = "M"
        guard let output = filter.outputImage?.transformed(by: CGAffineTransform(scaleX: 8, y: 8)),
              let cgImage = CIContext().createCGImage(output, from: output.extent) else { return nil }
        return NSImage(cgImage: cgImage, size: NSSize(width: output.extent.width / 2, height: output.extent.height / 2))
    }
}
