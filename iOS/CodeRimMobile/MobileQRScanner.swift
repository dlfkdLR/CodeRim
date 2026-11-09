import AVFoundation
import SwiftUI
import VisionKit

/// Reads pairing capabilities only inside the app, through its scanner or explicit paste action.
struct MobileQRScanner: View {
    let onLink: (MobilePairingLink) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var invalid = false
    private var cameraDenied: Bool {
        [.denied, .restricted].contains(AVCaptureDevice.authorizationStatus(for: .video))
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 18) {
                if DataScannerViewController.isSupported, DataScannerViewController.isAvailable {
                    LiveScanner { text in
                        guard let link = MobilePairingLink(text) else { invalid = true; return }
                        dismiss(); onLink(link)
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 20, style: .continuous))
                    .frame(maxHeight: 420)
                } else if cameraDenied {
                    // Say why, and give the way out: the switch lives in Settings, not here.
                    ContentUnavailableView {
                        Label("Camera access is off", systemImage: "camera.fill")
                    } description: {
                        Text("Allow the camera for CodeRim in Settings, or copy the pairing code from your computer and paste it here.")
                    } actions: {
                        Button("Open Settings") {
                            if let url = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(url) }
                        }
                        .buttonStyle(.borderedProminent)
                        .accessibilityIdentifier("openCameraSettings")
                    }
                } else {
                    ContentUnavailableView("Camera unavailable", systemImage: "camera",
                        description: Text("Copy the pairing code from CodeRim on your computer and paste it here."))
                }
                Text("On your computer, open CodeRim → Settings → iPhone and choose Connect iPhone. The code includes its server, so there is nothing to type.")
                    .font(.footnote).foregroundStyle(.secondary).multilineTextAlignment(.center)
                // Pasting is an explicit enrollment action, just like opening this scanner.
                PasteButton(payloadType: String.self) { strings in
                    Task { @MainActor in
                        guard let text = strings.first, let link = MobilePairingLink(text) else { invalid = true; return }
                        dismiss(); onLink(link)
                    }
                }
                .accessibilityIdentifier("pastePairingLink")
                if invalid { Text("That is not a CodeRim pairing code.").font(.footnote).foregroundStyle(.red) }
            }
            .padding()
            .navigationTitle("Scan QR code").navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } } }
        }
    }
}

private struct LiveScanner: UIViewControllerRepresentable {
    let onCode: (String) -> Void
    func makeUIViewController(context: Context) -> DataScannerViewController {
        let scanner = DataScannerViewController(recognizedDataTypes: [.barcode(symbologies: [.qr])],
            qualityLevel: .balanced, isHighlightingEnabled: true)
        scanner.delegate = context.coordinator
        try? scanner.startScanning()
        return scanner
    }
    func updateUIViewController(_ controller: DataScannerViewController, context: Context) {}
    func makeCoordinator() -> Coordinator { Coordinator(onCode: onCode) }
    static func dismantleUIViewController(_ controller: DataScannerViewController, coordinator: Coordinator) { controller.stopScanning() }

    final class Coordinator: NSObject, DataScannerViewControllerDelegate {
        let onCode: (String) -> Void
        private var delivered = false
        init(onCode: @escaping (String) -> Void) { self.onCode = onCode }
        func dataScanner(_ scanner: DataScannerViewController, didAdd items: [RecognizedItem], allItems: [RecognizedItem]) {
            for case .barcode(let code) in items {
                guard !delivered, let text = code.payloadStringValue, MobilePairingLink(text) != nil else { continue }
                delivered = true; onCode(text); return
            }
        }
    }
}
