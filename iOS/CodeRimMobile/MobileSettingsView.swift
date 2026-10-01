import SwiftUI

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
            Form {
                Section {
                    if model.signedIn {
                        HStack(spacing: 14) {
                            CodeRimSetupMark().frame(width: 46, height: 46)
                            VStack(alignment: .leading, spacing: 4) {
                                Text("Make yourself at home.").font(.headline)
                                Text("Your computers. Your Island.").font(.subheadline).foregroundStyle(.secondary)
                            }
                        }.padding(.vertical, 6)
                    } else {
                        welcome
                    }
                }.listRowBackground(Color.clear)
                    .listRowInsets(EdgeInsets(top: 12, leading: 4, bottom: 8, trailing: 4))
                if !model.signedIn {
                    Section {
                        Button { scanning = true } label: {
                            HStack {
                                Label("Scan the QR code on your computer", systemImage: "qrcode.viewfinder")
                                Spacer()
                                Image(systemName: "arrow.right")
                            }.font(.body.weight(.semibold)).padding(.vertical, 6)
                        }
                        .buttonStyle(.borderedProminent).tint(.primary)
                        .foregroundStyle(Color(.systemBackground))
                        .disabled(model.busy)
                        .listRowSeparator(.hidden)
                        if let error = model.errorMessage {
                            Label(error, systemImage: "exclamationmark.circle")
                                .foregroundStyle(.red).font(.footnote)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    } header: { Text("Let's get connected") } footer: {
                        Text("On your Mac or Windows PC, open CodeRim → Settings → iPhone and choose Connect iPhone. No sign-in is needed, and your AI service accounts stay on your computers.")
                    }
                } else {
                    Section {
                        Label("Connected", systemImage: "checkmark.seal.fill")
                        LabeledContent("Relay server", value: model.serverAddress).font(.footnote)
                    } header: { Text("Connection") }
                    if let notice = model.serverNotice {
                        Section { Label(notice, systemImage: "hourglass").font(.footnote).foregroundStyle(.orange) }
                    }

                    Section {
                        if model.devices.isEmpty {
                            Label {
                                VStack(alignment: .leading, spacing: 5) {
                                    Text("Bring your computer along.").font(.subheadline.weight(.semibold))
                                    Text("Pair a Mac or Windows PC to get started.").font(.footnote).foregroundStyle(.secondary)
                                }
                            } icon: {
                                Image(systemName: "laptopcomputer").font(.title2).foregroundStyle(.secondary)
                            }.padding(.vertical, 8)
                        }
                        ForEach(model.devices) { device in
                            HStack {
                                Label(device.name, systemImage: device.platform == "windows" ? "pc" : "laptopcomputer")
                                Spacer()
                                Text(device.online ? "Online" : "Offline").font(.caption).foregroundStyle(.secondary)
                                Button(role: .destructive) { deviceToRemove = device } label: { Image(systemName: "minus.circle") }
                                    .buttonStyle(.borderless).accessibilityLabel("Disconnect \(device.name)")
                            }
                        }
                        Button("Add a computer") { scanning = true }.disabled(model.busy)
                    } header: { Text("Connected devices · \(model.devices.count)") } footer: {
                        Text("On your Mac or Windows PC, open CodeRim → Settings → iPhone and scan its QR code. Connect over the internet, even on different Wi-Fi networks. Each computer must stay on.")
                    }

                    Section {
                        NavigationLink {
                            MobileIslandProviderPicker(model: model)
                        } label: {
                            LabeledContent("Showing", value: model.state.providers.first?.name ?? "Choose a service")
                        }.accessibilityIdentifier("island-showing")
                        NavigationLink {
                            MobileProviderSelection(model: model)
                        } label: {
                            LabeledContent("Providers", value: model.preferences.providerIDs.isEmpty ? "All" : "\(model.preferences.providerIDs.count) selected")
                        }
                    } header: { Text("Island display") } footer: {
                        Text("In the expanded Island, tap the service name for pinned and recent services. Use All to browse by name, and the pin to keep the current service in Quick access. Tap the computer name to switch devices. Your last choice is remembered on this iPhone.")
                    }
                    Section {
                        LabeledContent("Live Activity", value: model.activityStatus)
                        Button(model.activityActive ? "Stop showing" : "Show now") {
                            Task { if model.activityActive { await model.stopActivity() } else { await model.startActivity() } }
                        }.disabled(model.busy || isSettingsPreview)
                    } header: { Text("Dynamic Island") } footer: {
                        Text("The Island appears on its own when a task starts on a connected computer and leaves a couple of minutes after the work stops. Touch and hold it to see usage and task status, also shown on the Lock Screen.")
                    }
                    Section {
                        Label("Full conversation transcripts and AI service credentials are not shared.", systemImage: "lock.shield")
                            .font(.footnote)
                        Text("To show task titles, enable “Share task titles” on each computer. Titles are sent to the relay server and shown on the Lock Screen.")
                            .font(.footnote).foregroundStyle(.secondary)
                    } header: { Text("Shared information") }
                    Section {
                        Button("Disconnect this iPhone", role: .destructive) { confirmDelete = true }.disabled(model.busy || isSettingsPreview)
                    }
                }
                if model.busy { Section { HStack { ProgressView(); Text("Checking connection…").foregroundStyle(.secondary) } } }
                if model.signedIn, let error = model.errorMessage { Section { Label(error, systemImage: "exclamationmark.circle").foregroundStyle(.red).font(.footnote) } }
                if let status = model.statusMessage { Section { Text(status).font(.footnote).foregroundStyle(.secondary) } }
            }
            .navigationTitle(model.signedIn ? "Settings" : "CodeRim")
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

    private var welcome: some View {
        VStack(alignment: .leading, spacing: 18) {
            CodeRimSetupMark().frame(width: 46, height: 46).accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 10) {
                Text("Your work,\na glance away.")
                    .font(.system(.largeTitle, design: .rounded, weight: .semibold))
                    .fixedSize(horizontal: false, vertical: true)
                Text("Usage and live tasks, right in your Island.")
                    .font(.subheadline).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            SetupIslandPreview()
        }
    }

}


private struct CodeRimSetupMark: View {
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 13, style: .continuous).fill(.primary)
            Circle().trim(from: 0.1, to: 0.9)
                .stroke(Color(.systemBackground), style: StrokeStyle(lineWidth: 4, lineCap: .round))
                .rotationEffect(.degrees(36)).padding(11)
        }
    }
}

/// An explicitly labelled illustration, never connected to live state or navigation.
private struct SetupIslandPreview: View {
    var body: some View {
        VStack(spacing: 9) {
            HStack(spacing: 16) {
                ZStack {
                    Circle().strokeBorder(Color(white: 0.19), lineWidth: 6)
                    Circle().inset(by: 3).trim(from: 0, to: 0.68)
                        .stroke(Color(red: 0, green: 1, blue: 136 / 255), style: StrokeStyle(lineWidth: 3, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                    IslandProviderMark(id: "codex", name: "Codex").frame(width: 21, height: 21).foregroundStyle(.white)
                }.frame(width: 50, height: 50)
                VStack(alignment: .leading, spacing: 7) {
                    Text("Codex").font(.caption.weight(.semibold)).foregroundStyle(.white.opacity(0.65))
                    Text("Making progress").font(.subheadline.weight(.medium)).foregroundStyle(.white)
                    Text("68% left  ·  284K tokens today").font(.caption2).foregroundStyle(.white.opacity(0.65))
                }.fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
            }.padding(22).frame(maxWidth: .infinity, alignment: .leading)
                .background(.black, in: RoundedRectangle(cornerRadius: 30, style: .continuous))
                .accessibilityHidden(true)
            Text("Island preview · Sample data").font(.caption2).foregroundStyle(.secondary)
        }.accessibilityElement(children: .ignore)
            .accessibilityLabel("Sample Island preview. Usage and live tasks appear here after setup.")
    }
}
