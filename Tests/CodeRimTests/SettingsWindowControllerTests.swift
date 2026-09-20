import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class SettingsWindowControllerTests: XCTestCase {
    func testScrolledSettingsStayBelowToolbarWithSidebarVisibleAndHidden() async throws {
        _ = NSApplication.shared
        let fixture = try SettingsWindowFixture()
        defer { fixture.remove() }
        let controller = SettingsWindowController()
        controller.configure(environment: fixture.environment)
        defer { controller.closeSettingsForTesting() }
        controller.present(selecting: .category(.notch))
        let host = try XCTUnwrap(controller.settingsContentViewControllerForTesting?.view)
        let window = try XCTUnwrap(host.window)
        for sidebar in [NavigationSplitViewVisibility.all, .detailOnly] {
            controller.settingsSidebarVisibilityForTesting = sidebar
            try await settle(window)
            for scroll in descendants(of: NSScrollView.self, in: host) where !(scroll.documentView is NSOutlineView) {
                scroll.contentView.scroll(to: NSPoint(x: 0, y: 200))
                scroll.reflectScrolledClipView(scroll.contentView)
            }
            try await settle(window)
            XCTAssertFalse(window.titlebarAppearsTransparent)
            let viewport = try XCTUnwrap(host.subviews.first)
            XCTAssertEqual(viewport.layer?.masksToBounds, true)
            let viewportFrame = viewport.convert(viewport.bounds, to: nil)
            XCTAssertTrue(window.contentLayoutRect.insetBy(dx: -1, dy: -1).contains(viewportFrame),
                          "The hard clipping boundary must exclude the toolbar")
            for scroll in descendants(of: NSScrollView.self, in: host) {
                let visibleFrame = scroll.convert(scroll.visibleRect, to: nil)
                XCTAssertLessThanOrEqual(visibleFrame.maxY, window.contentLayoutRect.maxY + 1,
                    "The scrolling viewport reaches the toolbar: \(visibleFrame), content \(window.contentLayoutRect)")
            }
            if let directory = ProcessInfo.processInfo.environment["CODERIM_LAYOUT_CAPTURE_DIR"] {
                let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
                host.cacheDisplay(in: host.bounds, to: bitmap)
                let folder = URL(fileURLWithPath: directory, isDirectory: true)
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                try bitmap.representation(using: .png, properties: [:])?.write(to:
                    folder.appendingPathComponent("notch-scrolled-\(sidebar == .all ? "sidebar" : "detail").png"))
            }
        }
    }

    func testProviderDestinationsSurviveRequestsBeforeAndWithinProviders() {
        let navigation = SettingsNavigation()
        navigation.select(.provider(.claude))
        XCTAssertEqual(navigation.category, .providers)
        XCTAssertEqual(navigation.providerDetailID, "claude")
        navigation.select(.provider(.codex))
        XCTAssertEqual(navigation.providerDetailID, "codex")
        navigation.select(.notchProvider(id: "gemini"))
        XCTAssertEqual(navigation.providerDetailID, "gemini")
        navigation.select(.category(.providers))
        XCTAssertNil(navigation.providerDetailID)
        navigation.select(.provider(.claude))
        navigation.select(.category(.usage))
        XCTAssertNil(navigation.providerDetailID)
    }

    func testEveryCategoryStaysVisibleWhenSwitchingResizingAndReopening() async throws {
        _ = NSApplication.shared
        let fixture = try SettingsWindowFixture()
        defer { fixture.remove() }
        let controller = SettingsWindowController()
        defer { controller.closeSettingsForTesting() }
        controller.configure(environment: fixture.environment)
        controller.present()
        let host = try XCTUnwrap(controller.settingsContentViewControllerForTesting?.view)
        let window = try XCTUnwrap(host.window)

        for dark in [false, true] {
            window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            for size in [NSSize(width: 840, height: 560), NSSize(width: 980, height: 680),
                         NSSize(width: 1200, height: 800)] {
                window.setContentSize(size)
                for category in SettingsCategory.allCases {
                    controller.present(selecting: .category(category))
                    try await settle(window)
                    let context = "\(category.title), \(size), dark=\(dark)"
                    XCTAssertEqual(window.title, category.title, context)
                    let split = try XCTUnwrap(descendants(of: NSSplitView.self, in: host).first)
                    let splitFrame = split.convert(split.bounds, to: host)
                    XCTAssertTrue(host.bounds.insetBy(dx: -1, dy: -1).contains(splitFrame),
                                  "\(context): split \(splitFrame) exceeds viewport \(host.bounds)")
                    let sidebar = try XCTUnwrap(descendants(of: NSOutlineView.self, in: host).first)
                    XCTAssertEqual(sidebar.numberOfRows, SettingsCategory.allCases.count, context)
                    for row in 0..<sidebar.numberOfRows {
                        let frame = sidebar.convert(sidebar.rect(ofRow: row), to: nil)
                        XCTAssertTrue(window.contentLayoutRect.contains(frame),
                                      "\(context): sidebar row \(row) is clipped: \(frame)")
                    }
                    for scroll in descendants(of: NSScrollView.self, in: host) {
                        XCTAssertLessThanOrEqual(scroll.documentView?.bounds.width ?? 0,
                                                 scroll.contentSize.width + 1, context)
                    }
                    if let captureDir = ProcessInfo.processInfo.environment["CODERIM_LAYOUT_CAPTURE_DIR"] {
                        let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
                        host.cacheDisplay(in: host.bounds, to: bitmap)
                        let dir = URL(fileURLWithPath: captureDir, isDirectory: true)
                        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                        try bitmap.representation(using: .png, properties: [:])?.write(to:
                            dir.appendingPathComponent("window-\(category.rawValue)-\(Int(size.width))-\(dark ? "dark" : "light").png"))
                    }
                }
            }
        }

        controller.closeSettingsForTesting()
        controller.present(selecting: .category(.usage))
        try await settle(window)
        XCTAssertTrue(controller.isSettingsWindowVisible)
        XCTAssertTrue(host === controller.settingsContentViewControllerForTesting?.view)
        XCTAssertEqual(window.title, "Usage")
    }

    private func settle(_ window: NSWindow) async throws {
        // Let SwiftUI commit navigation, toolbar and List updates to AppKit.
        for _ in 0..<4 {
            try await Task.sleep(for: .milliseconds(25))
            window.layoutIfNeeded()
            window.contentView?.layoutSubtreeIfNeeded()
        }
    }

    private func descendants<T: NSView>(of type: T.Type, in view: NSView) -> [T] {
        ((view as? T).map { [$0] } ?? []) + view.subviews.flatMap { descendants(of: type, in: $0) }
    }

    private func makeEnvironment() -> SettingsEnvironment {
        SettingsEnvironment(
            limitStore: AccountLimitStore(provider: SettingsTestLimitProvider(), pollingInterval: nil)
        )
    }

    func testSettingsWindowKeepsOneContentControllerAcrossPresentations() {
        _ = NSApplication.shared
        let controller = SettingsWindowController()
        defer { controller.closeSettingsForTesting() }
        controller.configure(environment: makeEnvironment())

        controller.present()
        let first = controller.settingsContentViewControllerForTesting
        controller.present()
        XCTAssertTrue(first === controller.settingsContentViewControllerForTesting)
    }

    func testSettingsWindowTitleHasNoProviderSuffix() {
        _ = NSApplication.shared
        let controller = SettingsWindowController()
        defer { controller.closeSettingsForTesting() }
        controller.configure(environment: makeEnvironment())

        controller.present()
        let window = controller.settingsContentViewControllerForTesting?.view.window
        window?.layoutIfNeeded()
        let title = window?.title ?? ""
        // The provider-scoped "— Claude Code" suffix is gone; the title is either the
        // default or the selected pane name, never a per-provider window.
        XCTAssertFalse(title.contains("—"))
        XCTAssertFalse(title.contains("Claude Code"))
        XCTAssertTrue(["CodeRim Settings", "Usage", "General"].contains(title), title)
    }

    func testSettingsWindowBecomesVisibleAndCanReopen() {
        _ = NSApplication.shared
        let controller = SettingsWindowController()
        defer { controller.closeSettingsForTesting() }
        controller.configure(environment: makeEnvironment())

        controller.present()
        XCTAssertTrue(controller.isSettingsWindowVisible)
        XCTAssertTrue(controller.settingsWindowIsResizableForTesting)
        XCTAssertEqual(
            controller.settingsWindowMinimumContentSizeForTesting,
            NSSize(width: 840, height: 560)
        )
        XCTAssertGreaterThanOrEqual(controller.settingsWindowContentSizeForTesting?.width ?? 0, 840)
        XCTAssertGreaterThanOrEqual(controller.settingsWindowContentSizeForTesting?.height ?? 0, 560)
        let firstContentController = controller.settingsContentViewControllerForTesting

        controller.closeSettingsForTesting()
        XCTAssertFalse(controller.isSettingsWindowVisible)

        controller.present()
        XCTAssertTrue(controller.isSettingsWindowVisible)
        XCTAssertTrue(firstContentController === controller.settingsContentViewControllerForTesting)
    }
}

/// Real window tests run SwiftUI tasks. Give them empty sources and a temporary
/// database so opening Usage cannot import the developer's session history.
@MainActor
private struct SettingsWindowFixture {
    let root: URL
    let suite: String
    let defaults: UserDefaults
    let environment: SettingsEnvironment

    init() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        suite = "CodeRim.SettingsWindowTests.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defaults.set("manual", forKey: "refreshMode")
        defaults.set(false, forKey: "claudeEnabled")
        defaults.set(false, forKey: "profileSyncEnabled")
        let codex = CodexUsageCollector(database: try SQLiteDatabase(url: root.appendingPathComponent("codex.sqlite")), roots: [])
        let claude = CodexUsageCollector(database: try SQLiteDatabase(url: root.appendingPathComponent("claude.sqlite")),
                                        roots: [], provider: .claude)
        environment = SettingsEnvironment(
            codexStore: UsageStore(automaticallyRefresh: false, collector: codex, defaults: defaults),
            claudeStore: UsageStore(provider: .claude, automaticallyRefresh: false, collector: claude, defaults: defaults),
            limitStore: AccountLimitStore(provider: SettingsTestLimitProvider(), defaults: defaults, pollingInterval: nil),
            claude: ClaudeIntegrationStore(defaults: defaults, automaticallyRefresh: false),
            profileStore: ProfileUsageStore(defaults: defaults, allowsAccountTotals: false),
            codexAccounts: AccountLayoutFixture.emptyStore()
        )
    }

    func remove() {
        environment.codexStore.stopAutomaticRefresh()
        environment.claudeStore.stopAutomaticRefresh()
        defaults.removePersistentDomain(forName: suite)
        try? FileManager.default.removeItem(at: root)
    }
}

private struct SettingsTestLimitProvider: AccountLimitProviding {
    func readLimits() async throws -> AccountLimitsSnapshot {
        throw AccountLimitError.trustedAppServerNotFound
    }
}
