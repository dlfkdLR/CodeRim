import AppKit
import os
import SwiftUI

/*
 THESIS: Codex and Claude usage should read like a quiet macOS instrument welded to a screen edge, not a web dashboard squeezed into the menu bar.
 OWN-WORLD: A floating notch of concentric usage rings; system materials elsewhere; semantic labels, hairline separators, tabular numerals.
 STORY: Glance at the edge for how much limit is left and which agent is running; open Settings ▸ Usage for the token history.
 FIRST VIEWPORT: The notch at rest is a small pill; on hover it unfolds one ring per provider with the tightest limit and a tooltip card.
 FORM: Native macOS accessory app — a status-bar menu for the way in, the notch for the reading, a Settings window for everything else.
 FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
 HISTORY: through 1.x this was a MenuBarExtra popover with a diamond meter; 2.0 replaced it with the notch (ported from the MIT-licensed vinzdg/codenotch).
*/
@main
struct CodexMeterApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var store: UsageStore
    @StateObject private var claudeStore: UsageStore
    @StateObject private var profileStore: ProfileUsageStore
    @StateObject private var accountLimitStore: AccountLimitStore
    @StateObject private var claudeIntegrationStore: ClaudeIntegrationStore
    @StateObject private var settingsEnvironment: SettingsEnvironment

    init() {
        AppPreferences.registerDefaults()
        let store = UsageStore()
        let claudeStore = UsageStore(provider: .claude, automaticallyRefresh: false)
        let profileStore = ProfileUsageStore(allowsAccountTotals: false)
        let accountLimitStore = AccountLimitStore()
        let claudeIntegrationStore = ClaudeIntegrationStore(automaticallyRefresh: false)
        _store = StateObject(wrappedValue: store)
        _claudeStore = StateObject(wrappedValue: claudeStore)
        _profileStore = StateObject(wrappedValue: profileStore)
        _accountLimitStore = StateObject(wrappedValue: accountLimitStore)
        _claudeIntegrationStore = StateObject(wrappedValue: claudeIntegrationStore)
        let settingsEnvironment = SettingsEnvironment(
            codexStore: store,
            claudeStore: claudeStore,
            limitStore: accountLimitStore,
            claude: claudeIntegrationStore,
            profileStore: profileStore,
            codexAccounts: .shared
        )
        _settingsEnvironment = StateObject(wrappedValue: settingsEnvironment)
        SettingsWindowController.shared.configure(environment: settingsEnvironment)
        NotchController.shared.configure(
            codexLimits: accountLimitStore,
            claudeIntegration: claudeIntegrationStore,
            codexAccounts: .shared,
            codexUsage: store,
            claudeUsage: claudeStore
        )
        claudeIntegrationStore.onAvailabilityChanged = { [weak claudeStore] available in
            guard let claudeStore else { return }
            if available {
                claudeStore.startAutomaticRefresh()
                Task { @MainActor in await claudeStore.refresh() }
            } else {
                claudeStore.stopAutomaticRefresh()
            }
        }
        claudeIntegrationStore.startAutomaticRefresh()
        CodexAccountStore.shared.onAccountWillChange = { [weak profileStore, weak accountLimitStore] in
            profileStore?.clearForAccountSwitch()
            accountLimitStore?.clearForAccountSwitch()
        }
        CodexAccountStore.shared.onAccountOperationFinished = { [weak profileStore, weak accountLimitStore] in
            Task {
                // An old read-only app-server may still be winding down; its result is
                // discarded by the generation guard before requesting the new account.
                while accountLimitStore?.isRefreshing == true || profileStore?.isRefreshing == true {
                    try? await Task.sleep(for: .milliseconds(100))
                }
                await accountLimitStore?.refresh()
                await profileStore?.refresh(weekStart: Self.selectedWeekStart)
            }
        }
        Task { @MainActor [weak store] in
            await store?.refresh()
        }
        Task { @MainActor [weak profileStore] in
            profileStore?.synchronizeEnabledPreference()
            await profileStore?.refresh(weekStart: Self.selectedWeekStart)
        }
        Task { @MainActor [weak accountLimitStore] in
            await accountLimitStore?.refresh()
        }
    }

    var body: some Scene {
        Settings {
            SettingsView()
                .environmentObject(settingsEnvironment)
                .environmentObject(claudeIntegrationStore)
        }
        .commands {
            // Use the same resizable window as the status item and notch.
            // SwiftUI's default command otherwise creates a second Settings
            // window with independent navigation and sizing behavior.
            CommandGroup(replacing: .appSettings) {
                Button("Settings…") {
                    SettingsWindowController.shared.present()
                }
                .keyboardShortcut(",", modifiers: .command)
            }
        }
    }

    private static var selectedWeekStart: WeekStart {
        let defaults = UserDefaults.standard
        let rawValue = defaults.object(forKey: "weekStart") == nil
            ? WeekStart.monday.rawValue
            : defaults.integer(forKey: "weekStart")
        return WeekStart(rawValue: rawValue) ?? .monday
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private static let log = Logger(subsystem: "dev.codexmeter.CodexMeter", category: "lifecycle")

    /// A SwiftUI `App` whose only scene is `Settings` does not reliably deliver
    /// `applicationDidFinishLaunching` — the status item has to go up here, the
    /// one callback that always fires, or the app launches with no visible
    /// surface at all.
    func applicationWillFinishLaunching(_ notification: Notification) {
        Self.log.info("willFinishLaunching")
        NSApplication.shared.setActivationPolicy(.accessory)
        StatusItemController.shared.install()
        applyNotchVisibility()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        Self.log.info("didFinishLaunching")
        NSApplication.shared.setActivationPolicy(.accessory)
        StatusItemController.shared.install()
        UpdateService.shared.start()
        applyNotchVisibility()
    }

    private func applyNotchVisibility() {
        // A Settings-only app may not deliver didFinishLaunching. The notch
        // shares the status item's early startup path; setVisible is idempotent.
        NotchController.shared.setVisible(
            UserDefaults.standard.object(forKey: "showEdgeNotch") as? Bool
                ?? AppPreferences.defaultShowEdgeNotch
        )
    }

    /// With no windows of its own, an accessory app clicked in the Finder or
    /// the Dock would do nothing visible. Send those to the token-usage view —
    /// that is what the app is for.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        if !hasVisibleWindows {
            SettingsWindowController.shared.present(selecting: .category(.usage))
        }
        return true
    }
}
