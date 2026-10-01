import SwiftUI
import VisionKit

/// Scans the pairing QR code a computer shows. Falls back to the Camera app and
/// pasting when live scanning is unavailable (no camera access, Simulator).
struct MobileQRScanner: View {
    let onLink: (MobilePairingLink) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var pasted = ""
    @State private var invalid = false

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
                } else {
                    ContentUnavailableView("Camera unavailable", systemImage: "camera",
                        description: Text("Scan the code with the iPhone Camera app, or paste the link below."))
                }
                Text("On your computer, open CodeRim → Settings → iPhone and choose Connect iPhone.")
                    .font(.footnote).foregroundStyle(.secondary).multilineTextAlignment(.center)
                HStack {
                    TextField("coderim://pair?…", text: $pasted)
                        .textInputAutocapitalization(.never).autocorrectionDisabled().textFieldStyle(.roundedBorder)
                    Button("Connect") {
                        guard let link = MobilePairingLink(pasted) else { invalid = true; return }
                        dismiss(); onLink(link)
                    }.disabled(pasted.isEmpty)
                }
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
                guard !delivered, let text = code.payloadStringValue, text.hasPrefix("coderim://") else { continue }
                delivered = true; onCode(text); return
            }
        }
    }
}
