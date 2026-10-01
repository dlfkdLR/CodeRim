import AppKit
import CodeRimShared
import CoreImage.CIFilterBuiltins
import SwiftUI

struct MobileSettingsSection: View {
    @ObservedObject private var connection = MobileConnectionStore.shared
    @State private var showsRelay = false

    /// One spring for everything that appears or leaves, so the card grows instead of jumping.
    private static let motion = Animation.spring(response: 0.5, dampingFraction: 0.86)

    var body: some View {
        VStack(alignment: .leading, spacing: 6) { content }
            .animation(Self.motion, value: connection.pairingLink)
            .animation(Self.motion, value: connection.isConnected)
    }

    @ViewBuilder private var content: some View {
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
            Button("Use a different relay server…") { showsRelay = true }
                .buttonStyle(.link).font(.caption)
                .padding(.horizontal, SettingsMetrics.textInset)
        }
        if let notice = connection.serverNotice { SettingsNote(notice, tint: .orange) }
        if let message = connection.errorMessage { SettingsNote(message, tint: .red) }
    }

    @ViewBuilder private func pairingCode(_ link: MobilePairingLink) -> some View {
        VStack(spacing: 16) {
            if let image = Self.qrImage(link.url.absoluteString) {
                // A white tile with a quiet zone: scanners need the margin, and it sits as one object.
                Image(nsImage: image).interpolation(.none).resizable()
                    .frame(width: 176, height: 176)
                    .padding(14)
                    .background(.white, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                    .shadow(color: .black.opacity(0.18), radius: 10, y: 3)
                    .accessibilityLabel("Pairing QR code")
            }
            VStack(spacing: 5) {
                Text("Scan with the CodeRim iPhone app").font(.headline)
                Text("The code works once and expires in five minutes.")
                    .font(.callout).foregroundStyle(.secondary)
            }
            .multilineTextAlignment(.center)
            if let expiry = connection.pairingExpiresAt, expiry > Date() {
                VStack(spacing: 5) {
                    // The bar and the clock tick on their own; nothing re-renders the QR code.
                    ProgressView(timerInterval: Date()...expiry, countsDown: true) { EmptyView() } currentValueLabel: { EmptyView() }
                        .progressViewStyle(.linear).frame(width: 220).tint(.secondary)
                    Text(timerInterval: Date()...expiry, countsDown: true)
                        .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                }
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 22).padding(.horizontal, SettingsMetrics.rowInset)
        .transition(.opacity.combined(with: .scale(scale: 0.97, anchor: .top)))
    }

    static func qrImage(_ text: String) -> NSImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8); filter.correctionLevel = "M"
        guard let output = filter.outputImage?.transformed(by: CGAffineTransform(scaleX: 8, y: 8)),
              let cgImage = CIContext().createCGImage(output, from: output.extent) else { return nil }
        return NSImage(cgImage: cgImage, size: NSSize(width: output.extent.width / 2, height: output.extent.height / 2))
    }
}
