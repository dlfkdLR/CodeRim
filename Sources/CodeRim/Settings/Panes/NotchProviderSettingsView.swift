import AppKit
import SwiftUI

/// One notch provider that CodeRim reads but does not meter locally — the
/// borrowed-credential rings (Copilot, Cursor, Grok, OpenCode, Command Code,
/// GLM, Ollama, Antigravity). Modelled on Codenotch's account row: whose
/// credential it borrows, its live limit windows (the "usage"), where to sign
/// in, and a per-provider alert mute. Codex and Claude keep their richer
/// `ProviderSettingsView` — they are metered locally.
struct NotchProviderSettingsView: View {
    let providerID: String

    @State private var accountActionMessage: String?
    @ObservedObject private var notch = NotchController.shared
    @AppStorage("notchThresholdAlerts") private var thresholdAlerts = AppPreferences.defaultNotchThresholdAlerts
    @AppStorage(AppPreferences.mutedAlertProvidersKey) private var mutedAlerts = ""

    private var name: String {
        notch.summary(for: providerID)?.name
            ?? NotchProviderCatalog.all.first { $0.id == providerID }?.name
            ?? providerID.capitalized
    }
    private var glyph: ProviderGlyph { notch.glyph(for: providerID) }
    private var snapshot: ProviderSnapshot? { notch.snapshot(for: providerID) }
    private var summary: ProviderSummary? { notch.summary(for: providerID) }
    private var account: ProviderAccount? { summary?.account }
    private var route: SignInRoute? { summary?.signIn }
    private var isConnected: Bool { snapshot?.status == .ok || snapshot?.hasReading == true }
    private var wasRefused: Bool { summary?.wasRefusedAccess == true }

    var body: some View {
        SettingsForm {
            headerCard

            if let block = snapshot?.block {
                SettingsInfoRow(
                    text: block.summary(),
                    systemImage: "exclamationmark.octagon",
                    tint: .orange
                )
            }

            usageSection
            accountSection
            if ExtendedProviderCatalog.isExtended(providerID),
               let descriptor = ExtendedProviderCatalog.descriptor(for: providerID) {
                SettingsNote("To use another account, save its credentials below, or switch the imported browser session and refresh.")
                SettingsSection(title: "Switch account") {
                    SettingsButtonRow(title: "Refresh account", systemImage: "arrow.clockwise") {
                        notch.providerConfigurationDidChange(providerID)
                    }
                }
                ExtendedProviderSettingsView(descriptor: descriptor)
            } else {
                connectionSection
            }
            alertsSection

            if !ExtendedProviderCatalog.isExtended(providerID) {
            SettingsNote("CodeRim never signs in to \(name). Each reading is borrowed from the tool that already holds the account — signing out or switching accounts is done in that tool, and the notch follows.")
            }
        }
        .onAppear { notch.refreshProvidersForSettings() }
    }

    // MARK: Header

    private var headerCard: some View {
        HStack(spacing: 12) {
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(Color.secondary.gradient)
                .frame(width: 30, height: 30)
                .overlay(
                    ProviderGlyphView(glyph: glyph, size: 17)
                        .foregroundStyle(.white)
                )
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(name).font(.headline)
                Text(statusText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
            Spacer(minLength: 8)
            HStack(spacing: 10) {
                if isConnected {
                    Button {
                        AppPreferences.setAlertMuted(!isMuted, for: providerID)
                    } label: {
                        Image(systemName: isMuted ? "bell.slash" : "bell")
                    }
                    .buttonStyle(.borderless)
                    .disabled(!thresholdAlerts)
                    .help(isMuted ? "Alerts muted for \(name). Click to unmute."
                                  : "Alert when \(name) crosses 80% and 100% of a limit.")
                    .accessibilityLabel(isMuted ? "Unmute \(name) alerts" : "Mute \(name) alerts")
                }
                Button {
                    notch.refresh(providerID: providerID)
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.borderless)
                .accessibilityLabel("Refresh \(name)")
            }
        }
        .padding(14)
        .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .padding(.horizontal, 20)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(name). \(statusText)")
    }

    // MARK: Usage

    @ViewBuilder
    private var usageSection: some View {
        if let windows = snapshot?.windows, !windows.isEmpty {
            SettingsSection(title: "Usage") {
                ForEach(windows) { window in
                    NotchUsageRow(window: window)
                }
                if let tokens = snapshot?.todaysTokens {
                    SettingsValueRow(
                        title: "Today on this Mac",
                        value: "\(tokens.formatted()) tokens"
                    )
                }
            }
            if let reset = nearestReset {
                SettingsNote("Nearest window resets \(reset).")
            }
        } else if let message = snapshot?.statusMessage {
            SettingsNote(message)
        }
    }

    // MARK: Account

    @ViewBuilder
    private var accountSection: some View {
        if let account {
            SettingsSection(title: "Account") {
                if let label = account.label {
                    SettingsValueRow(title: "Signed in as", value: label)
                }
                if let plan = account.plan {
                    SettingsValueRow(title: "Plan", value: plan.capitalized)
                }
                SettingsValueRow(title: "Credential from", value: account.source)
                if let manage = account.manageURL {
                    SettingsLinkRow(title: "Open usage page", systemImage: "safari", destination: manage)
                }
            }
        }
    }

    // MARK: Connection

    @ViewBuilder
    private var connectionSection: some View {
        if wasRefused {
            SettingsSection(title: "Connection") {
                SettingsButtonRow(title: "Allow access…", systemImage: "lock.open") {
                    notch.refresh(providerID: providerID)
                }
            }
            SettingsNote("macOS refused \(name)'s saved login. Choose Always Allow when it asks again and it will stop prompting.")
        } else if let route, providerID != "ollama-local" {
            SettingsSection(title: isConnected || account != nil ? "Switch account" : "Connection") {
                routeControl(route)
                SettingsButtonRow(title: "Refresh account", systemImage: "arrow.clockwise") {
                    notch.providerConfigurationDidChange(providerID)
                }
            }
            SettingsNote(route.switchHint)
            if case .guidance = route { SettingsNote(route.explanation) }
            if let accountActionMessage { SettingsNote(accountActionMessage) }
        } else if !isConnected, let route {
            SettingsSection(title: "Connection") { routeControl(route) }
            SettingsNote(route.explanation)
        }
    }

    // MARK: Alerts

    private var alertsSection: some View {
        Group {
            SettingsSection(title: "Alerts") {
                SettingsToggleRow(
                    "Notify at 80% and 100%",
                    get: { _ = mutedAlerts; return !AppPreferences.isAlertMuted(providerID) },
                    set: { AppPreferences.setAlertMuted(!$0, for: providerID) }
                )
                .disabled(!thresholdAlerts)
            }
            if !thresholdAlerts {
                SettingsNote("Limit alerts are off for every provider — turn them on in the Notch pane.")
            }
        }
    }

    @ViewBuilder
    private func routeControl(_ route: SignInRoute) -> some View {
        switch route {
        case let .openApp(_, appName):
            SettingsButtonRow(title: "Open \(appName)", systemImage: "arrow.up.forward.app") {
                accountActionMessage = notch.openAccountSource(providerID: providerID)
                    ? nil : "Install or open \(appName) to change its account, then choose Refresh account."
            }
        case let .modal(appName):
            SettingsButtonRow(title: "Sign in to \(appName)", systemImage: "person.badge.key") {
                accountActionMessage = notch.openAccountSource(providerID: providerID)
                    ? nil : "The sign-in window could not be opened. Try again."
            }
        case .guidance:
            EmptyView()
        }
    }

    private var isMuted: Bool { _ = mutedAlerts; return AppPreferences.isAlertMuted(providerID) }

    private var statusText: String {
        guard let snapshot else { return "Not connected" }
        if wasRefused { return "Keychain access was denied" }
        switch snapshot.status {
        case .ok:                 return snapshot.hasReading ? "Connected" : "Connected — no reading yet"
        case .stale(let since):   return since == .distantPast ? "Checking…" : "Last read \(ElapsedCopy.ago(since: since))"
        case .needsAuth:          return "Not connected"
        case .accessDenied:       return "Keychain access was denied"
        case .unsupported(let why): return why
        case .error(let message): return message
        }
    }

    private var nearestReset: String? {
        let now = Date()
        guard let soonest = snapshot?.windows.compactMap(\.resetsAt).filter({ $0 > now }).min() else {
            return nil
        }
        return ResetCopy.text(for: soonest, now: now)
    }
}

/// One limit window as a labelled bar — the Settings-side echo of the notch
/// tooltip's rows, drawn in the system design rather than the black notch one.
private struct NotchUsageRow: View {
    let window: LimitWindow

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(window.label)
                Spacer(minLength: 8)
                Text(window.summary)
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            if let fraction = window.usedFraction {
                ProgressView(value: min(max(fraction, 0), 1))
                    .progressViewStyle(.linear)
                    .tint(barColor(for: fraction))
            }
            if let resetsAt = window.resetsAt, resetsAt > Date() {
                Text("Resets \(ResetCopy.text(for: resetsAt))")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 9)
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(window.label), \(window.summary)")
    }

    private func barColor(for fraction: Double) -> Color {
        switch UsageBand.band(for: fraction) {
        case .ample:                return .green
        case .watch:                return .yellow
        case .critical, .exhausted: return .orange
        }
    }
}
