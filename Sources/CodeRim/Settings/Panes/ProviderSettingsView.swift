import AppKit
import SwiftUI

struct ProviderSettingsView: View {
    let provider: UsageProvider
    @EnvironmentObject private var env: SettingsEnvironment

    var body: some View {
        ProviderSettingsContent(
            provider: provider,
            store: env.usageStore(for: provider),
            limitStore: env.limitStore,
            claude: env.claude,
            codexAccounts: env.codexAccounts
        )
        // Reset @State (confirmsClear) when switching provider panes.
        .id(provider)
    }
}

private struct ProviderSettingsContent: View {
    let provider: UsageProvider
    @ObservedObject var store: UsageStore
    @ObservedObject var limitStore: AccountLimitStore
    @ObservedObject var claude: ClaudeIntegrationStore
    @ObservedObject var codexAccounts: CodexAccountStore

    @AppStorage("accountLimitsEnabled") private var accountLimitsEnabled = AppPreferences.defaultAccountLimitsEnabled
    @AppStorage("analyticsEnabled") private var analyticsEnabled = AppPreferences.defaultAnalyticsEnabled
    @AppStorage("costEstimatesEnabled") private var costEstimatesEnabled = AppPreferences.defaultCostEstimatesEnabled
    @AppStorage("additionalLimitsEnabled") private var additionalLimitsEnabled = AppPreferences.defaultAdditionalLimitsEnabled
    @AppStorage("resetCreditsEnabled") private var resetCreditsEnabled = AppPreferences.defaultResetCreditsEnabled
    @AppStorage("projectsEnabled") private var projectsEnabled = AppPreferences.defaultProjectsEnabled
    @AppStorage("sessionsEnabled") private var sessionsEnabled = AppPreferences.defaultSessionsEnabled
    @AppStorage("agentDetailsEnabled") private var agentDetailsEnabled = AppPreferences.defaultAgentDetailsEnabled
    @AppStorage("attachmentMetadataEnabled") private var attachmentMetadataEnabled = AppPreferences.defaultAttachmentMetadataEnabled

    @State private var confirmsClear = false

    private var isCodex: Bool { provider == .codex }
    private var isBusy: Bool { store.isMaintainingData || store.isRefreshing || store.isImportingHistory }

    var body: some View {
        SettingsForm {
            headerCard
            accountSection
            if isCodex { limitsSection }
            analyticsSection
            if !isCodex { claudeNoteSection }
            localDataSection
            sourcesSection
            manageDataSection
        }
        .task {
            if isCodex {
                codexAccounts.refreshCurrentPlanType()
            } else if claude.isEnabled {
                await claude.refresh()
            }
        }
        .confirmationDialog(
            "Clear \(provider.title) local history?",
            isPresented: $confirmsClear,
            titleVisibility: .visible
        ) {
            Button("Clear Local History", role: .destructive) {
                Task { await store.clearLocalHistory() }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This deletes only the local \(provider.title) statistics. Session files are not deleted, and records at or before this time will remain excluded.")
        }
    }

    // MARK: Header

    @ViewBuilder private var headerCard: some View {
        if isCodex {
            SettingsProviderCard(provider: .codex, statusLine: "Available", isOn: true) {
                Button {
                    Task { await store.refresh() }
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.borderless)
                .disabled(store.isRefreshing)
                .accessibilityLabel("Refresh Codex")
            }
        } else {
            SettingsProviderCard(provider: .claude, statusLine: claude.statusMessage, isOn: claude.isEnabled) {
                HStack(spacing: 10) {
                    if claude.isEnabled, claude.isConnected {
                        Button {
                            Task { await claude.refresh() }
                        } label: {
                            Image(systemName: "arrow.clockwise")
                        }
                        .buttonStyle(.borderless)
                        .disabled(claude.isRefreshing)
                        .accessibilityLabel("Refresh Claude")
                    }
                    Toggle("", isOn: Binding(
                        get: { claude.isEnabled },
                        set: { enabled in Task { await claude.setEnabled(enabled) } }
                    ))
                    .labelsHidden()
                    .toggleStyle(.switch)
                    .accessibilityLabel("Enable Claude Code")
                }
            }
        }
    }

    // MARK: Account

    @ViewBuilder private var accountSection: some View {
        if isCodex {
            SettingsSection(title: "Account") {
                SettingsValueRow(title: "Account", value: codexAccountValue)
                if let plan = codexAccounts.currentPlanName {
                    SettingsValueRow(title: "Plan", value: plan)
                }
            }
            SettingsNote("Add or switch accounts from the account menu in Settings ▸ Usage.")
        } else if claude.isEnabled {
            if claude.isConnected, let account = claude.account {
                SettingsSection(title: "Account") {
                    SettingsValueRow(title: "Account", value: account.displayName)
                    if let plan = account.planName {
                        SettingsValueRow(title: "Plan", value: plan)
                    }
                    SettingsValueRow(title: "Limits", value: claude.statusMessage)
                    SettingsButtonRow(title: "Manage Accounts…", systemImage: "person.crop.circle") {
                        ClaudeAccountsWindowController.shared.show()
                    }
                    SettingsButtonRow(title: "Disconnect", systemImage: "xmark.circle", role: .destructive) {
                        Task { await claude.disconnect() }
                    }
                }
            } else {
                SettingsSection(title: "Account") {
                    SettingsValueRow(title: "Status", value: claude.statusMessage)
                    if let detected = claude.detectedAccount {
                        SettingsValueRow(title: "Detected account", value: detected.displayName)
                    }
                    SettingsButtonRow(title: "Add Account", systemImage: "person.badge.plus", isEnabled: !claude.isRefreshing) {
                        Task { await claude.addCurrentAccount() }
                    }
                }
                if claude.detectedAccount == nil {
                    SettingsNote("Sign in with the `claude` command in your terminal, then choose Add Account.")
                }
                if claude.isRefreshing {
                    SettingsInfoRow(text: "Checking Claude…", systemImage: "hourglass", tint: .secondary)
                }
            }
        }
    }

    // MARK: Limits (Codex)

    private var limitsSection: some View {
        SettingsSection(title: "Limits") {
            SettingsToggleRow("Show account limits", isOn: Binding(
                get: { accountLimitsEnabled },
                set: { newValue in
                    accountLimitsEnabled = newValue
                    limitStore.synchronizeEnabledPreference()
                }
            ))
            SettingsToggleRow("Show additional limits", isOn: $additionalLimitsEnabled, isEnabled: accountLimitsEnabled)
            SettingsToggleRow("Show reset credits", isOn: $resetCreditsEnabled, isEnabled: accountLimitsEnabled)
        }
    }

    // MARK: Analytics

    @ViewBuilder private var analyticsSection: some View {
        SettingsSection(title: "Usage Analytics") {
            SettingsToggleRow("Show usage analytics", isOn: $analyticsEnabled)
            if provider.supportsCostEstimates {
                SettingsToggleRow("Show estimated API-equivalent cost", isOn: $costEstimatesEnabled, isEnabled: analyticsEnabled)
            }
            SettingsToggleRow("Show projects", isOn: $projectsEnabled, isEnabled: analyticsEnabled)
            SettingsToggleRow("Show sessions", isOn: $sessionsEnabled, isEnabled: analyticsEnabled)
            SettingsToggleRow("Show agent details", isOn: $agentDetailsEnabled, isEnabled: analyticsEnabled && sessionsEnabled)
            SettingsToggleRow("Show attachment metadata", isOn: $attachmentMetadataEnabled,
                              isEnabled: analyticsEnabled && sessionsEnabled && isCodex)
        }
        if provider.supportsCostEstimates {
            SettingsNote("Estimated from current API prices — not a bill or a quota prediction.")
        }
    }

    // MARK: Claude note

    private var claudeNoteSection: some View {
        SettingsNote("Comes from Claude Code session logs on this Mac. Five-hour and weekly limits appear after a connected account completes a response; cost estimates aren't available yet.")
    }

    // MARK: Local data

    private var localDataSection: some View {
        SettingsSection(title: "\(provider.title) Local Data") {
            SettingsValueRow(title: "Status", value: store.sourceStatusText)
            SettingsValueRow(title: "Source files", value: store.sourceCount.formatted())
            SettingsValueRow(title: "Database size", value: formattedBytes(store.dataStatistics.databaseBytes))
            SettingsValueRow(title: "Oldest record", value: formattedDate(store.dataStatistics.oldestRecord))
            SettingsValueRow(title: "Newest record", value: formattedDate(store.dataStatistics.newestRecord))
            SettingsButtonRow(title: "Open Data Folder", systemImage: "folder") { openDataFolder() }
        }
    }

    // MARK: Sources

    @ViewBuilder private var sourcesSection: some View {
        SettingsSection(title: "Sources") {
            SettingsValueRow(title: "Local \(provider.title) sessions", value: store.sourceStatusText)
            if isCodex {
                SettingsValueRow(title: "Account limits", value: accountLimitStatus)
                SettingsValueRow(title: "Pricing catalog", value: "Available · \(PricingCatalog.current.metadata.version)")
            }
        }
        if !isCodex {
            SettingsNote("Reads ~/.claude/projects, or CLAUDE_CONFIG_DIR when set in the app's environment.")
        }
    }

    // MARK: Manage data

    private var manageDataSection: some View {
        SettingsSection(title: "Manage Data") {
            if isBusy {
                SettingsRow(title: store.statusMessage) {
                    ProgressView().controlSize(.small)
                }
            } else if let message = store.dataOperationMessage {
                SettingsInfoRow(
                    text: message,
                    systemImage: store.dataOperationFailed ? "exclamationmark.triangle" : "checkmark.circle",
                    tint: store.dataOperationFailed ? .red : .secondary
                )
            }
            SettingsButtonRow(title: "Rebuild Statistics", systemImage: "hammer", isEnabled: !isBusy) {
                Task { await store.rebuildStatistics() }
            }
            SettingsButtonRow(title: "Clear Local History", systemImage: "trash", role: .destructive, isEnabled: !isBusy) {
                confirmsClear = true
            }
        }
    }

    // MARK: Helpers

    /// Current login metadata is enough for this read-only pane; saved
    /// credentials are loaded only when the account controls need them.
    private var codexAccountValue: String {
        codexAccounts.currentAccountDisplayName ?? "Signed in to the Codex app"
    }

    private var accountLimitStatus: String {
        switch limitStore.status {
        case .disabled: "Disabled"
        case .loading: "Checking…"
        case .ready: "Connected"
        case .stale: "Last known data"
        case .unavailable: "Unavailable"
        }
    }

    private func openDataFolder() {
        do {
            let directory = try AppPaths.prepareApplicationSupportDirectory()
            NSWorkspace.shared.open(directory)
        } catch {
            NSSound.beep()
        }
    }

    private func formattedBytes(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }

    private func formattedDate(_ date: Date?) -> String {
        date?.formatted(date: .abbreviated, time: .shortened) ?? "None"
    }
}
