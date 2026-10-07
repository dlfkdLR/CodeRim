import SwiftUI

/// The Island's own green, used sparingly: live dots, the preview ring and the welcome glow.
private let islandGreen = Color(red: 0, green: 1, blue: 136 / 255)
/// A readable green for text and small marks on light backgrounds.
private let onlineGreen = Color(.systemGreen)

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
        NavigationStack {
            Group {
                if model.signedIn { settings } else { welcome }
            }
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

    // MARK: Welcome

    private var welcome: some View {
        ScrollView {
            VStack(spacing: 24) {
                VStack(spacing: 14) {
                    CodeRimSetupMark()
                        .frame(width: 64, height: 64)
                        .background { Circle().fill(islandGreen.opacity(0.35)).blur(radius: 28).scaleEffect(1.5) }
                        .accessibilityHidden(true)
                    Text("Your work,\na glance away.")
                        .font(.system(.largeTitle, design: .rounded, weight: .bold))
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                    Text("Usage and live tasks from your computers, right in your Island.")
                        .font(.body).foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.top, 4)
                SetupIslandPreview()
                SetupSteps()
                if let error = model.errorMessage {
                    Label(error, systemImage: "exclamationmark.circle.fill")
                        .font(.footnote).foregroundStyle(.red)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(14)
                        .background(Color.red.opacity(0.1), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(.horizontal, 22).padding(.bottom, 24)
        }
        .scrollBounceBehavior(.basedOnSize)
        .background {
            LinearGradient(colors: [islandGreen.opacity(0.14), Color(.systemGroupedBackground)],
                           startPoint: .top, endPoint: UnitPoint(x: 0.5, y: 0.45))
                .ignoresSafeArea()
        }
        .safeAreaInset(edge: .bottom) {
            VStack(spacing: 12) {
                Button { scanning = true } label: {
                    HStack(spacing: 10) {
                        if model.busy { ProgressView().tint(Color(.systemBackground)) }
                        else { Image(systemName: "qrcode.viewfinder") }
                        Text(model.busy ? "Connecting…" : "Scan QR code")
                    }
                    .font(.headline)
                    .frame(maxWidth: .infinity)
                }
                .buttonStyle(PrimaryCapsuleButtonStyle())
                .disabled(model.busy)
                .accessibilityLabel("Scan the QR code on your computer")
                Label("No sign-in. Your AI accounts stay on your computers.", systemImage: "lock.fill")
                    .font(.footnote).foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
            .padding(.horizontal, 22).padding(.top, 14).padding(.bottom, 8)
            .background(.bar)
        }
        .toolbar(.hidden, for: .navigationBar)
    }

    // MARK: Settings

    private var settings: some View {
        Form {
            Section {
                ConnectionHero(devices: model.devices, server: model.serverAddress)
            } footer: {
                if let notice = model.serverNotice {
                    Label(notice, systemImage: "hourglass").foregroundStyle(.orange)
                }
            }

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
                        Task { if model.activityActive { await model.stopActivity() } else { await model.startActivity() } }
                    }
                    .buttonStyle(.bordered).buttonBorderShape(.capsule).controlSize(.small)
                    .tint(model.activityActive ? .secondary : .accentColor)
                    .disabled(model.busy || isSettingsPreview)
                }
            } header: { Text("Dynamic Island") } footer: {
                Text("Touch and hold the Island to see usage and tasks, also shown on the Lock Screen. Tap the service or computer name there to switch.")
            }

            Section {
                HStack(alignment: .top, spacing: 14) {
                    SettingsIcon(symbol: "lock.shield.fill", tint: .blue)
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Private by design").font(.subheadline.weight(.semibold))
                        Text("Conversations and AI service credentials never leave your computers. Task titles are shared only if you turn on “Share task titles” on a computer.")
                            .font(.footnote).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }.padding(.vertical, 4)
            } header: { Text("Privacy") }

            Section {
                Button("Disconnect this iPhone", role: .destructive) { confirmDelete = true }
                    .frame(maxWidth: .infinity)
                    .disabled(model.busy || isSettingsPreview)
            }
            if model.busy { Section { HStack { ProgressView(); Text("Checking connection…").foregroundStyle(.secondary) } } }
            if let error = model.errorMessage { Section { Label(error, systemImage: "exclamationmark.circle").foregroundStyle(.red).font(.footnote) } }
            if let status = model.statusMessage { Section { Text(status).font(.footnote).foregroundStyle(.secondary) } }
        }
        .navigationTitle("Settings")
        .navigationBarTitleDisplayMode(.inline)
    }
}

// MARK: - Pieces

private struct CodeRimSetupMark: View {
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

/// A slim filled capsule; `.borderedProminent` adds its own padding and reads too heavy here.
private struct PrimaryCapsuleButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .foregroundStyle(Color(.systemBackground))
            .frame(height: 50)
            .background(Color.primary.opacity(isEnabled ? 1 : 0.4), in: Capsule())
            .opacity(configuration.isPressed ? 0.75 : 1)
            .scaleEffect(configuration.isPressed ? 0.98 : 1)
            .animation(.easeOut(duration: 0.15), value: configuration.isPressed)
    }
}

/// An iOS Settings-style rounded icon tile.
private struct SettingsIcon: View {
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
private struct LiveDot: View {
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

private struct ConnectionHero: View {
    let devices: [MobileDevice]
    let server: String
    private var online: Int { devices.filter(\.online).count }
    private var title: String {
        if devices.isEmpty { return "Almost there" }
        return online > 0 ? "You're all set" : "Waiting for your computer"
    }
    private var subtitle: String {
        if devices.isEmpty { return "Add a computer to fill your Island." }
        let count = devices.count == 1 ? "1 computer" : "\(devices.count) computers"
        return "\(count) · \(online) online"
    }
    var body: some View {
        HStack(spacing: 16) {
            CodeRimSetupMark().frame(width: 52, height: 52).accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 5) {
                Text(title).font(.system(.title3, design: .rounded, weight: .bold))
                HStack(spacing: 7) {
                    if online > 0 { LiveDot() }
                    Text(subtitle).font(.subheadline).foregroundStyle(.secondary)
                }
                if let host = URL(string: server)?.host {
                    Text("via \(host)").font(.caption).foregroundStyle(.tertiary).lineLimit(1)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 8)
        .accessibilityElement(children: .combine)
    }
}

private struct DeviceRow: View {
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

/// Three short steps instead of one paragraph of directions.
private struct SetupSteps: View {
    private let steps: [(String, String)] = [
        ("Open CodeRim on your computer", "Settings → iPhone, on a Mac or Windows PC"),
        ("Choose Connect iPhone", "A QR code appears for five minutes."),
        ("Scan it with this iPhone", "The server is in the code. Nothing to type."),
    ]
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(Array(steps.enumerated()), id: \.offset) { index, step in
                HStack(alignment: .top, spacing: 14) {
                    Text("\(index + 1)")
                        .font(.system(.subheadline, design: .rounded, weight: .bold))
                        .frame(width: 28, height: 28)
                        .background(Color(.tertiarySystemFill), in: Circle())
                    VStack(alignment: .leading, spacing: 3) {
                        Text(step.0).font(.subheadline.weight(.semibold))
                        Text(step.1).font(.footnote).foregroundStyle(.secondary)
                    }
                    .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 0)
                }
                .padding(.vertical, 12)
                .accessibilityElement(children: .combine)
                if index < steps.count - 1 { Divider().padding(.leading, 42) }
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 4)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 20, style: .continuous))
    }
}

/// An explicitly labelled illustration, never connected to live state or navigation.
private struct SetupIslandPreview: View {
    @State private var progress = 0.0
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    var body: some View {
        VStack(spacing: 10) {
            HStack(spacing: 16) {
                ZStack {
                    Circle().strokeBorder(Color(white: 0.19), lineWidth: 6)
                    Circle().inset(by: 3).trim(from: 0, to: progress)
                        .stroke(islandGreen, style: StrokeStyle(lineWidth: 3, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                    IslandProviderMark(id: "codex", name: "Codex").frame(width: 21, height: 21).foregroundStyle(.white)
                }.frame(width: 50, height: 50)
                VStack(alignment: .leading, spacing: 6) {
                    HStack(spacing: 6) {
                        Text("Codex").font(.caption.weight(.semibold)).foregroundStyle(.white.opacity(0.65))
                        LiveDot(color: islandGreen)
                    }
                    Text("Making progress").font(.subheadline.weight(.medium)).foregroundStyle(.white)
                    Text("68% left  ·  284K tokens today").font(.caption2).foregroundStyle(.white.opacity(0.65))
                }.fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
            }.padding(22).frame(maxWidth: .infinity, alignment: .leading)
                .background(.black, in: RoundedRectangle(cornerRadius: 32, style: .continuous))
                // Keeps the black preview distinct from a black background in Dark Mode.
                .overlay(RoundedRectangle(cornerRadius: 32, style: .continuous).strokeBorder(.white.opacity(0.12)))
                .shadow(color: .black.opacity(0.18), radius: 18, y: 10)
                .accessibilityHidden(true)
            Text("Island preview · Sample data").font(.caption2).foregroundStyle(.secondary)
        }.accessibilityElement(children: .ignore)
            .accessibilityLabel("Sample Island preview. Usage and live tasks appear here after setup.")
            .onAppear {
                guard !reduceMotion else { progress = 0.68; return }
                withAnimation(.spring(duration: 1.4).delay(0.25)) { progress = 0.68 }
            }
    }
}
