import SwiftUI

/// The Island's own green, used sparingly: live dots, the preview ring and the welcome glow.
let islandGreen = Color(red: 0, green: 1, blue: 136 / 255)
/// A readable green for text and small marks on light backgrounds.
let onlineGreen = Color(.systemGreen)

/// Computers, Island display and the connection itself, one tap away from the dashboard.
struct MobileSettingsView: View {
    @ObservedObject var model: MobileAppModel
    @State private var confirmDelete = false
    @State private var scanning = false
    @State private var deviceToRemove: MobileDevice?
    private var isSettingsPreview: Bool {
        #if DEBUG
        ProcessInfo.processInfo.arguments.contains("--ui-settings")
        #else
        false
        #endif
    }

    var body: some View {
        Form {
            Section {
                if model.devices.isEmpty {
                    HStack(spacing: 14) {
                        SettingsIcon(symbol: "laptopcomputer", tint: .gray)
                        VStack(alignment: .leading, spacing: 3) {
                            Text("Bring your computer along.").font(.subheadline.weight(.semibold))
                            Text("Pair a Mac or Windows PC to get started.").font(.footnote).foregroundStyle(.secondary)
                        }
                    }.padding(.vertical, 4)
                }
                ForEach(model.devices) { device in
                    DeviceRow(device: device)
                        .swipeActions {
                            Button("Disconnect", role: .destructive) { deviceToRemove = device }
                        }
                        .contextMenu {
                            Button("Disconnect \(device.name)", systemImage: "minus.circle", role: .destructive) { deviceToRemove = device }
                        }
                        .accessibilityAction(named: "Disconnect \(device.name)") { deviceToRemove = device }
                }
                Button { scanning = true } label: {
                    Label("Add a computer", systemImage: "plus.circle.fill").font(.body.weight(.medium))
                }.disabled(model.busy)
            } header: { Text("Computers") } footer: {
                Text(model.devices.isEmpty
                     ? "In CodeRim on your computer, open Settings → iPhone and scan its QR code. Works across different Wi‑Fi networks."
                     : "Swipe left on a computer to disconnect it. Each computer shares while it is awake.")
            }

            Section {
                NavigationLink {
                    MobileIslandProviderPicker(model: model)
                } label: {
                    HStack(spacing: 14) {
                        SettingsIcon(symbol: "capsule.fill", tint: .orange)
                        LabeledContent("Showing", value: model.state.providers.first?.name ?? "Choose a service")
                    }
                }.accessibilityIdentifier("island-showing")
                NavigationLink {
                    MobileProviderSelection(model: model)
                } label: {
                    HStack(spacing: 14) {
                        SettingsIcon(symbol: "square.stack.3d.up.fill", tint: .indigo)
                        LabeledContent("Providers", value: model.preferences.providerIDs.isEmpty ? "All" : "\(model.preferences.providerIDs.count) selected")
                    }
                }
                HStack(spacing: 14) {
                    SettingsIcon(symbol: "dot.radiowaves.left.and.right", tint: .green)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Live Activity")
                        Text(model.activityStatus).font(.footnote).foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 8)
                    Button(model.activityActive ? "Stop showing" : "Show now") {
                        Task { if model.activityActive { await model.stopShowing() } else { await model.startActivity() } }
                    }
                    .buttonStyle(.bordered).buttonBorderShape(.capsule).controlSize(.small)
                    .tint(model.activityActive ? .secondary : .accentColor)
                    .disabled(model.busy || isSettingsPreview)
                }
            } header: { Text("Dynamic Island") } footer: {
                Text("The Island appears on its own while a task runs and this app is open. Touch and hold it to see usage and tasks; tap it to open CodeRim.")
            }

            Section {
                LabeledContent("Relay server", value: URL(string: model.serverAddress)?.host ?? model.serverAddress)
                    .font(.footnote)
                Button("Disconnect this iPhone", role: .destructive) { confirmDelete = true }
                    .disabled(model.busy || isSettingsPreview)
            } header: { Text("Connection") } footer: {
                if let notice = model.serverNotice { Label(notice, systemImage: "hourglass").foregroundStyle(.orange) }
            }
            if model.busy { Section { HStack { ProgressView(); Text("Checking connection…").foregroundStyle(.secondary) } } }
            if let error = model.errorMessage { Section { Label(error, systemImage: "exclamationmark.circle").foregroundStyle(.red).font(.footnote) } }
            if let status = model.statusMessage { Section { Text(status).font(.footnote).foregroundStyle(.secondary) } }
        }
        .navigationTitle("Settings")
        .navigationBarTitleDisplayMode(.inline)
        .confirmationDialog("Disconnect this device?", isPresented: Binding(get: { deviceToRemove != nil }, set: { if !$0 { deviceToRemove = nil } }), titleVisibility: .visible) {
            Button("Disconnect", role: .destructive) { if let device = deviceToRemove { Task { await model.removeDevice(device) } }; deviceToRemove = nil }
            Button("Cancel", role: .cancel) { deviceToRemove = nil }
        }
        .sheet(isPresented: $scanning) { MobileQRScanner { link in Task { await model.connect(link) } } }
        .confirmationDialog("Disconnect all computers and delete saved usage?", isPresented: $confirmDelete, titleVisibility: .visible) {
            Button("Disconnect this iPhone", role: .destructive) { Task { await model.signOut(deleteAccount: true) } }
            Button("Cancel", role: .cancel) {}
        }
    }
}

// MARK: - Pieces

struct CodeRimSetupMark: View {
    var body: some View {
        GeometryReader { proxy in
            let side = min(proxy.size.width, proxy.size.height)
            ZStack {
                RoundedRectangle(cornerRadius: side * 0.28, style: .continuous).fill(.primary)
                Circle().trim(from: 0.1, to: 0.9)
                    .stroke(Color(.systemBackground), style: StrokeStyle(lineWidth: side * 0.087, lineCap: .round))
                    .rotationEffect(.degrees(36)).padding(side * 0.24)
            }
        }
    }
}


/// An iOS Settings-style rounded icon tile.
struct SettingsIcon: View {
    let symbol: String
    let tint: Color
    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: 14, weight: .semibold))
            .foregroundStyle(.white)
            .frame(width: 30, height: 30)
            .background(tint.gradient, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            .accessibilityHidden(true)
    }
}

/// A pulsing dot that reads as "live" without drawing attention.
struct LiveDot: View {
    var color: Color = onlineGreen
    @State private var pulse = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    var body: some View {
        Circle().fill(color).frame(width: 8, height: 8)
            .background {
                Circle().fill(color.opacity(0.4))
                    .scaleEffect(pulse ? 2.4 : 1).opacity(pulse ? 0 : 1)
            }
            .onAppear {
                guard !reduceMotion else { return }
                withAnimation(.easeOut(duration: 1.6).repeatForever(autoreverses: false)) { pulse = true }
            }
            .accessibilityHidden(true)
    }
}

struct DeviceRow: View {
    let device: MobileDevice
    var body: some View {
        HStack(spacing: 14) {
            SettingsIcon(symbol: device.platform == "windows" ? "pc" : "laptopcomputer",
                         tint: device.online ? .blue : .gray)
            VStack(alignment: .leading, spacing: 3) {
                Text(device.name)
                HStack(spacing: 6) {
                    if device.online {
                        Circle().fill(onlineGreen).frame(width: 7, height: 7)
                        Text("Online").foregroundStyle(onlineGreen)
                    } else if device.lastSeen > 0 {
                        Text("Last seen \(Date(timeIntervalSince1970: device.lastSeen), format: .relative(presentation: .named))")
                            .foregroundStyle(.secondary)
                    } else {
                        Text("Offline").foregroundStyle(.secondary)
                    }
                }.font(.footnote)
            }
        }
        .padding(.vertical, 2)
    }
}
