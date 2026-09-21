import SwiftUI
import CodexBarCore

struct ExtendedProviderSettingsView: View {
    let descriptor: ProviderDescriptor
    @State private var configuration: ExtendedProviderConfiguration
    @State private var message: String?
    @State private var loaded = false

    init(descriptor: ProviderDescriptor) {
        self.descriptor = descriptor
        _configuration = State(initialValue: .init(providerID: descriptor.id))
    }

    private var guide: ExtendedProviderGuide? { ExtendedProviderGuides.all[descriptor.id.rawValue] }
    private var localID: String { ExtendedProviderCatalog.localID(descriptor.id) }
    private var hasCookies: Bool { descriptor.id != .stepfun && (descriptor.metadata.browserCookieOrder != nil || descriptor.fetchPlan.sourceModes.contains(.web)) }

    var body: some View {
        SettingsSection(title: "Connection settings") {
            VStack(alignment: .leading, spacing: 12) {
                if let guide {
                    Text(guide.summary).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    Link("Connection instructions", destination: guide.url)
                }
                Picker("Source", selection: sourceBinding) {
                    ForEach(ProviderSourceMode.allCases.filter { descriptor.fetchPlan.sourceModes.contains($0) }, id: \.rawValue) {
                        Text($0.rawValue.capitalized).tag($0)
                    }
                }
                if descriptor.credentials?.supportsAPIKeyOverride == true {
                    SecureField(apiKeyLabel, text: field(\.apiKey))
                        .accessibilityIdentifier("provider.\(localID).apiKey")
                }
                if descriptor.credentials?.usesSecretKey == true {
                    SecureField(descriptor.id == .stepfun ? "Password" : "Secret key", text: field(\.secretKey))
                }
                if descriptor.id == .stepfun {
                    Text("Use STEPFUN_USERNAME and STEPFUN_PASSWORD in Additional provider settings, or paste an Oasis-Token below.")
                        .font(.caption).foregroundStyle(.secondary)
                    SecureField("Oasis-Token (optional)", text: Binding(
                        get: { configuration.provider.cookieHeader ?? "" },
                        set: {
                            configuration.provider.cookieHeader = $0.isEmpty ? nil : $0
                            configuration.provider.cookieSource = $0.isEmpty ? .auto : .manual
                        }))
                }
                if hasCookies {
                    Toggle("Import this provider's browser session", isOn: Binding(
                        get: { configuration.provider.cookieSource == .auto },
                        set: { configuration.provider.cookieSource = $0 ? .auto : .manual }))
                    if configuration.provider.cookieSource != .auto {
                        SecureField("Session token or Cookie header", text: field(\.cookieHeader))
                    }
                }
                if descriptor.credentials?.usesRegion == true {
                    TextField(descriptor.id == .stepfun ? "Username" : "Region", text: field(\.region))
                }
                if descriptor.config.workspaceIDValidationOrder != nil || descriptor.id == .azureopenai || descriptor.id == .openai {
                    TextField(descriptor.id == .azureopenai ? "Deployment name" : "Workspace / project ID", text: field(\.workspaceID))
                }
                if descriptor.config.supportsEnterpriseHost {
                    TextField("Endpoint / base URL", text: field(\.enterpriseHost))
                }
                if let keys = guide?.environmentKeys, !keys.isEmpty {
                    DisclosureGroup("Additional provider settings") {
                        VStack(alignment: .leading, spacing: 10) {
                            ForEach(keys, id: \.self) { key in
                                VStack(alignment: .leading, spacing: 4) {
                                    Text(key).font(.caption).textSelection(.enabled)
                                    SecureField("Optional value", text: Binding(
                                        get: { configuration.environment[key] ?? "" },
                                        set: { configuration.environment[key] = configuration.environmentInput($0, for: key) }))
                                        .accessibilityLabel(key)
                                }
                            }
                        }.padding(.top, 8)
                    }
                }
                if let notice = ExtendedProviderConfiguration.billableNotice(for: descriptor.id) {
                    Text(notice).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    Toggle("Allow potentially billed monitoring requests", isOn: $configuration.allowBillableRequests)
                }
                HStack {
                    Button("Save and refresh") { save() }.disabled(!loaded)
                        .accessibilityIdentifier("provider.\(localID).save")
                    if let destination = descriptor.metadata.dashboardURL.flatMap(URL.init(string:)) {
                        Link("Open provider dashboard", destination: destination)
                    }
                }
                if let message { Text(message).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
                Text("Saved in this Mac's Keychain. Only added providers are refreshed; browser import is optional for each provider.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            .textFieldStyle(.roundedBorder)
            .padding(.horizontal, 20).padding(.vertical, 12)
        }
        .task(id: localID) {
            do { configuration = try ExtendedProviderConfigurationStore.load(descriptor, interactive: true); loaded = true }
            catch { message = error.localizedDescription; loaded = false }
        }
    }

    private var apiKeyLabel: String { descriptor.credentials?.apiKeyDebugLabel ?? "API key or access token" }
    private var sourceBinding: Binding<ProviderSourceMode> {
        Binding(get: { configuration.provider.source ?? .auto }, set: { configuration.provider.source = $0 })
    }
    private func field(_ path: WritableKeyPath<ProviderConfig, String?>) -> Binding<String> {
        Binding(get: { configuration.provider[keyPath: path] ?? "" }, set: {
            let trimmed = $0.trimmingCharacters(in: .whitespacesAndNewlines)
            configuration.provider[keyPath: path] = trimmed.isEmpty ? nil : trimmed
        })
    }
    private func save() {
        do {
            let issues = descriptor.credentials?.validateConfig(configuration.provider) ?? []
            guard !issues.contains(where: { $0.severity == .error }) else {
                message = "Check the endpoint, region and required fields in the connection instructions."
                return
            }
            try ExtendedProviderConfigurationStore.save(configuration)
            NotchController.shared.providerConfigurationDidChange(localID)
            message = "Saved. Refreshing this provider…"
        } catch { message = error.localizedDescription }
    }
}
