import AppKit
import Combine
import SwiftUI

@MainActor
final class SettingsWindowController: NSObject, NSWindowDelegate, NSToolbarDelegate {
    static let shared = SettingsWindowController()

    private static let frameAutosaveName = "CodexMeterSettingsWindowV2"
    private static let minimumContentSize = NSSize(width: 840, height: 560)
    private static let defaultContentSize = NSSize(width: 980, height: 680)

    private var settingsWindow: NSWindow?
    private var environment: SettingsEnvironment?
    private let navigation = SettingsNavigation()
    private var navigationObservers = Set<AnyCancellable>()
    private static let sidebarItemID = NSToolbarItem.Identifier("CodeRim.Settings.Sidebar")

    var isSettingsWindowVisible: Bool {
        settingsWindow?.isVisible == true
    }

    /// Wired once at launch from `CodeRimApp` with the app's real stores.
    func configure(environment: SettingsEnvironment) {
        self.environment = environment
    }

    func present() {
        navigation.columnVisibility = .all
        let environment = self.environment ?? SettingsEnvironment()
        let window = settingsWindow ?? makeWindow(environment: environment)
        // An accessory app is restricted from activating and compositing its
        // own windows, which is how "Settings…" can look like it did nothing.
        // Be a regular app for as long as the window is up; `windowWillClose`
        // puts the policy back.
        NSApplication.shared.setActivationPolicy(.regular)
        NSApplication.shared.activate(ignoringOtherApps: true)
        // Off any connected screen — a display reconfigured since it was last
        // placed — is the other way it opens invisibly.
        if !NSScreen.screens.contains(where: { $0.frame.intersects(window.frame) }) {
            window.center()
        }
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
    }

    func windowWillClose(_ notification: Notification) {
        NSApplication.shared.setActivationPolicy(.accessory)
    }

    func present(selecting pane: SettingsPane) {
        navigation.select(pane)
        present()
    }

    private func makeWindow(environment: SettingsEnvironment) -> NSWindow {
        let window = NSWindow(
            contentRect: NSRect(origin: .zero, size: Self.defaultContentSize),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "CodeRim Settings"
        window.titlebarAppearsTransparent = false
        window.toolbarStyle = .unified
        let toolbar = NSToolbar(identifier: "CodeRim.Settings.Toolbar")
        toolbar.delegate = self
        toolbar.displayMode = .iconOnly
        window.toolbar = toolbar
        navigation.$category.sink { [weak window] category in
            window?.title = category?.title ?? "Settings"
        }.store(in: &navigationObservers)
        navigation.$columnVisibility.sink { [weak toolbar] visibility in
            let label = visibility == .detailOnly ? "Show Sidebar" : "Hide Sidebar"
            toolbar?.items.first?.label = label
            toolbar?.items.first?.toolTip = label
        }.store(in: &navigationObservers)
        window.contentMinSize = Self.minimumContentSize
        window.isReleasedWhenClosed = false
        let hosting = NSHostingController(
            rootView: SettingsView(navigation: navigation)
                .environmentObject(environment)
                .environmentObject(environment.claude)
        )
        // macOS can extend a SwiftUI scroll view behind the titlebar even
        // when its logical viewport respects the safe area. Clip the entire
        // hosted hierarchy at the actual AppKit content-layout boundary.
        let container = NSViewController()
        container.view = NSView()
        window.contentViewController = container
        container.addChild(hosting)
        let viewport = NSView()
        viewport.wantsLayer = true
        viewport.layer?.masksToBounds = true
        viewport.translatesAutoresizingMaskIntoConstraints = false
        container.view.addSubview(viewport)
        hosting.view.translatesAutoresizingMaskIntoConstraints = false
        viewport.addSubview(hosting.view)
        let layout = window.contentLayoutGuide as! NSLayoutGuide
        NSLayoutConstraint.activate([
            viewport.leadingAnchor.constraint(equalTo: layout.leadingAnchor),
            viewport.trailingAnchor.constraint(equalTo: layout.trailingAnchor),
            viewport.topAnchor.constraint(equalTo: layout.topAnchor),
            viewport.bottomAnchor.constraint(equalTo: layout.bottomAnchor),
            hosting.view.leadingAnchor.constraint(equalTo: viewport.leadingAnchor),
            hosting.view.trailingAnchor.constraint(equalTo: viewport.trailingAnchor),
            hosting.view.topAnchor.constraint(equalTo: viewport.topAnchor),
            hosting.view.bottomAnchor.constraint(equalTo: viewport.bottomAnchor),
        ])
        window.delegate = self
        if !window.setFrameUsingName(Self.frameAutosaveName) {
            window.center()
        }
        window.setFrameAutosaveName(Self.frameAutosaveName)
        settingsWindow = window
        return window
    }

    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [Self.sidebarItemID, .flexibleSpace]
    }

    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [Self.sidebarItemID, .flexibleSpace]
    }

    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier itemIdentifier: NSToolbarItem.Identifier,
                 willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        guard itemIdentifier == Self.sidebarItemID else { return nil }
        let item = NSToolbarItem(itemIdentifier: itemIdentifier)
        item.label = "Hide Sidebar"
        item.toolTip = item.label
        item.image = NSImage(systemSymbolName: "sidebar.left", accessibilityDescription: item.label)
        item.target = self
        item.action = #selector(toggleSidebar(_:))
        return item
    }

    @objc private func toggleSidebar(_ sender: Any?) {
        navigation.columnVisibility = navigation.columnVisibility == .detailOnly ? .all : .detailOnly
    }

#if DEBUG
    var settingsSidebarVisibilityForTesting: NavigationSplitViewVisibility {
        get { navigation.columnVisibility }
        set { navigation.columnVisibility = newValue }
    }

    var settingsWindowContentSizeForTesting: NSSize? {
        settingsWindow?.contentView?.bounds.size
    }

    var settingsWindowIsResizableForTesting: Bool {
        settingsWindow?.styleMask.contains(.resizable) ?? false
    }

    var settingsWindowMinimumContentSizeForTesting: NSSize? {
        settingsWindow?.contentMinSize
    }

    var settingsContentViewControllerForTesting: NSViewController? {
        settingsWindow?.contentViewController
    }

    func closeSettingsForTesting() {
        settingsWindow?.close()
    }
#endif
}
