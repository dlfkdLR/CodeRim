import SwiftUI

struct MobileProviderSelection: View {
    @ObservedObject var model: MobileAppModel
    @State private var query = ""
    private var options: [MobileProviderOption] {
        model.selectionOptions.filter { query.isEmpty || $0.name.localizedCaseInsensitiveContains(query) || $0.id.localizedCaseInsensitiveContains(query) }
    }
    var body: some View {
        List {
            Section {
                Button {
                    guard !model.preferences.providerIDs.isEmpty else { return }
                    Task { await model.showAllProviders() }
                } label: {
                    HStack(spacing: 12) {
                        Image(systemName: "square.stack.3d.up").frame(width: 24)
                        VStack(alignment: .leading, spacing: 4) {
                            Text("All connected services").font(.body.weight(.medium))
                            Text("New services join automatically").font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer(minLength: 4)
                        if model.preferences.providerIDs.isEmpty { Image(systemName: "checkmark").font(.body.weight(.semibold)) }
                    }.padding(.vertical, 3)
                }.tint(.primary)
                    .disabled(model.busy)
                    .accessibilityAddTraits(model.preferences.providerIDs.isEmpty ? .isSelected : [])
            } footer: {
                Text("Select services available in the Island picker. Choose All to automatically include new services added on your computers.")
            }
            Section {
                if options.isEmpty {
                    if query.isEmpty {
                        ContentUnavailableView("No services yet", systemImage: "square.stack.3d.up",
                            description: Text(model.devices.isEmpty ? "Connect a computer to see its services here." : "Open CodeRim on a connected computer and check its services."))
                    } else {
                        ContentUnavailableView.search(text: query)
                    }
                }
                ForEach(options) { provider in
                    Toggle(isOn: Binding(get: {
                        model.preferences.providerIDs.isEmpty || model.preferences.providerIDs.contains(provider.id)
                    }, set: { enabled in Task { await model.selectProvider(provider.id, enabled: enabled) } })) {
                        HStack(spacing: 12) {
                            IslandProviderMark(id: provider.id, name: provider.name)
                                .frame(width: 22, height: 22).padding(8)
                                .background(.quaternary, in: RoundedRectangle(cornerRadius: 11, style: .continuous))
                                .accessibilityHidden(true)
                            Text(provider.name).font(.body).fixedSize(horizontal: false, vertical: true)
                        }.padding(.vertical, 2)
                    }.disabled(model.busy)
                        .accessibilityLabel(provider.name)
                        .accessibilityIdentifier("provider-toggle-" + provider.id)
                }
            } header: { Text("Services · \(options.count)") }
            if let error = model.errorMessage { Text(error).foregroundStyle(.red).font(.footnote) }
        }.navigationTitle("Providers").navigationBarTitleDisplayMode(.inline).searchable(text: $query, prompt: "Search services")
    }
}

/// Focus is separate from the set of providers included in the Island rotation.
struct MobileIslandProviderPicker: View {
    @ObservedObject var model: MobileAppModel
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    private var filtered: [MobileProviderOption] {
        model.displayProviders.filter { query.isEmpty || $0.name.localizedCaseInsensitiveContains(query) || $0.id.localizedCaseInsensitiveContains(query) }
    }
    var body: some View {
        List {
            Section {
                if filtered.isEmpty {
                    if model.displayProviders.isEmpty {
                        ContentUnavailableView("No services to show", systemImage: "circle.dotted",
                            description: Text(model.devices.isEmpty ? "Connect a computer to choose your first service." : "Check your provider selection and the services on this computer."))
                    } else {
                        ContentUnavailableView.search(text: query)
                    }
                }
                ForEach(filtered) { provider in
                    Button {
                        Task { if await model.showProvider(provider.id) { dismiss() } }
                    } label: {
                        HStack(spacing: 12) {
                            IslandProviderMark(id: provider.id, name: provider.name)
                                .frame(width: 22, height: 22).padding(8)
                                .background(.quaternary, in: RoundedRectangle(cornerRadius: 11))
                                .accessibilityHidden(true)
                            Text(provider.name).foregroundStyle(.primary)
                            Spacer()
                            if model.state.providers.first?.id == provider.id {
                                Image(systemName: "checkmark").font(.body.weight(.semibold)).foregroundStyle(.tint).accessibilityHidden(true)
                            }
                        }.frame(minHeight: 44).contentShape(Rectangle())
                    }.buttonStyle(.plain).disabled(model.busy)
                        .accessibilityLabel(provider.name)
                        .accessibilityIdentifier("show-provider-" + provider.id)
                        .accessibilityAddTraits(model.state.providers.first?.id == provider.id ? .isSelected : [])
                }
            } header: {
                Text(model.state.focus?.deviceName ?? "Current computer")
            } footer: {
                Text("Pick a service here, or tap the service name in the expanded Island to choose directly without opening this app.")
            }
            if let error = model.errorMessage {
                Section { Label(error, systemImage: "exclamationmark.circle").font(.footnote).foregroundStyle(.red) }
            }
            Section {
                NavigationLink("Manage providers") { MobileProviderSelection(model: model) }
            } footer: {
                Text("Only included services on this computer appear here. Manage providers to add services back to the rotation.")
            }
        }
        .navigationTitle("Show in Island").navigationBarTitleDisplayMode(.inline)
        .searchable(text: $query, prompt: "Find a service")
        .task { await model.refresh() }
    }
}
