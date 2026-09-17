import AppKit
import os

/// The one always-present entry point once the menu-bar popover is gone: a
/// small status-bar item whose menu reaches Settings, the Usage window, and the
/// notch toggle. Everything the popover's footer used to offer, minus the
/// popover itself — the readings live in the notch and the Usage pane now.
///
/// Deliberately a plain `NSMenu`, not a second `MenuBarExtra`: a menu costs
/// almost nothing when closed, and the notch is where a glanceable reading is
/// meant to be.
@MainActor
final class StatusItemController: NSObject, NSMenuDelegate {
    static let shared = StatusItemController()

    private static let log = Logger(subsystem: "dev.codexmeter.CodexMeter", category: "statusitem")
    private var statusItem: NSStatusItem?

    private override init() { super.init() }

    /// Called from `AppDelegate` (both `willFinishLaunching` and
    /// `didFinishLaunching`, since a `Settings`-only SwiftUI app is not
    /// guaranteed to deliver the second). Idempotent.
    func install() {
        guard statusItem == nil else { return }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)

        guard let button = item.button else {
            // Extremely rare, but a status item with no button draws nothing —
            // give it up rather than sit invisible.
            Self.log.error("status item has no button; removing")
            NSStatusBar.system.removeStatusItem(item)
            return
        }

        button.image = CodeRimMark.image()
        button.setAccessibilityLabel("CodeRim")
        button.imagePosition = .imageOnly
        button.toolTip = "CodeRim"

        let menu = NSMenu()
        // Manage `isEnabled` directly (the updates item) rather than through
        // menu validation — there is no responder chain to validate against.
        menu.autoenablesItems = false
        menu.delegate = self
        item.menu = menu
        statusItem = item
        Self.log.info("status item installed (image: \(button.image != nil, privacy: .public))")
    }

    // MARK: - Menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.autoenablesItems = false
        menu.removeAllItems()

        // First and by name: token totals and where they went are what
        // CodeRim is for.
        let usageItem = NSMenuItem(title: "Token Usage…", action: #selector(openUsage), keyEquivalent: "u")
        usageItem.target = self
        menu.addItem(usageItem)

        menu.addItem(.separator())

        let notchItem = NSMenuItem(
            title: "Show Notch",
            action: #selector(toggleNotch),
            keyEquivalent: ""
        )
        notchItem.target = self
        notchItem.state = notchIsOn ? .on : .off
        menu.addItem(notchItem)

        let settingsItem = NSMenuItem(title: "Settings…", action: #selector(openSettings), keyEquivalent: ",")
        settingsItem.target = self
        menu.addItem(settingsItem)

        let updatesItem = NSMenuItem(
            title: "Check for Updates…",
            action: #selector(checkForUpdates),
            keyEquivalent: ""
        )
        updatesItem.target = self
        updatesItem.isEnabled = UpdateService.shared.isAvailable
        menu.addItem(updatesItem)

        menu.addItem(.separator())

        let quitItem = NSMenuItem(
            title: "Quit CodeRim",
            action: #selector(NSApplication.terminate(_:)),
            keyEquivalent: "q"
        )
        menu.addItem(quitItem)
    }

    private var notchIsOn: Bool {
        UserDefaults.standard.object(forKey: "showEdgeNotch") as? Bool
            ?? AppPreferences.defaultShowEdgeNotch
    }

    @objc private func toggleNotch() {
        let next = !notchIsOn
        UserDefaults.standard.set(next, forKey: "showEdgeNotch")
        NotchController.shared.setVisible(next)
    }

    @objc private func openUsage() {
        SettingsWindowController.shared.present(selecting: .category(.usage))
    }

    @objc private func openSettings() {
        SettingsWindowController.shared.present()
    }

    @objc private func checkForUpdates() {
        UpdateService.shared.checkForUpdates()
    }
}
