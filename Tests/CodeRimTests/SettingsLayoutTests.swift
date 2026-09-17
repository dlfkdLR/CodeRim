import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class SettingsLayoutTests: XCTestCase {
    // The minimum window width minus the widest sidebar and its divider.
    private let size = NSSize(width: 579, height: 560)

    func testEverySettingsPaneFitsWithoutHorizontalClippingInBothAppearances() throws {
        _ = NSApplication.shared
        let captureDir = ProcessInfo.processInfo.environment["CODERIM_LAYOUT_CAPTURE_DIR"]

        for dark in [false, true] {
            for pane in SettingsPane.allCases {
                let name = "settings-\(pane.id)-\(dark ? "dark" : "light")"
                let host = NSHostingView(rootView:
                    SettingsPaneHarness(pane: pane)
                        .frame(width: size.width, height: size.height)
                        .environmentObject(makeEnvironment(claudeConnected: true))
                        .environmentObject(makeConnectedClaude())
                        .environment(\.colorScheme, dark ? .dark : .light)
                )
                host.sizingOptions = []
                let window = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                                      styleMask: [.borderless], backing: .buffered, defer: false)
                window.contentView = host
                window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                window.backgroundColor = .windowBackgroundColor
                host.frame = NSRect(origin: .zero, size: size)
                for _ in 0..<6 { host.layoutSubtreeIfNeeded() }

                for scroll in descendants(of: NSScrollView.self, in: host) {
                    let doc = scroll.documentView?.bounds.width ?? 0
                    XCTAssertLessThanOrEqual(doc, scroll.contentSize.width + 1,
                                             "\(name): content scrolls horizontally")
                }
                // The Usage pane hosts the menu-bar analytics view verbatim, which
                // keeps the popover's own controls (a segmented chart-metric
                // picker among them). The no-segmented-control rule is for the
                // native settings panes.
                if pane != .category(.usage) {
                    XCTAssertTrue(descendants(of: NSSegmentedControl.self, in: host).isEmpty,
                                  "\(name): settings must not use a segmented control")
                }

                if let captureDir {
                    let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
                    host.cacheDisplay(in: host.bounds, to: bitmap)
                    let dir = URL(fileURLWithPath: captureDir, isDirectory: true)
                    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                    try bitmap.representation(using: .png, properties: [:])?
                        .write(to: dir.appendingPathComponent("\(name).png"))
                }
            }
        }
    }

    func testFullSettingsWindowSidebarRenders() throws {
        _ = NSApplication.shared
        guard let captureDir = ProcessInfo.processInfo.environment["CODERIM_LAYOUT_CAPTURE_DIR"] else {
            throw XCTSkip("set CODERIM_LAYOUT_CAPTURE_DIR to capture")
        }
        for dark in [false, true] {
            let winSize = NSSize(width: 980, height: 640)
            let host = NSHostingView(rootView:
                SettingsView()
                    .frame(width: winSize.width, height: winSize.height)
                    .environmentObject(makeEnvironment(claudeConnected: true))
                    .environmentObject(makeConnectedClaude())
                    .environment(\.colorScheme, dark ? .dark : .light)
            )
            host.sizingOptions = []
            let window = NSWindow(contentRect: NSRect(origin: .zero, size: winSize),
                                  styleMask: [.titled], backing: .buffered, defer: false)
            window.contentView = host
            window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            host.frame = NSRect(origin: .zero, size: winSize)
            for _ in 0..<8 { host.layoutSubtreeIfNeeded() }
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
            host.cacheDisplay(in: host.bounds, to: bitmap)
            let dir = URL(fileURLWithPath: captureDir, isDirectory: true)
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try bitmap.representation(using: .png, properties: [:])?
                .write(to: dir.appendingPathComponent("settings-window-\(dark ? "dark" : "light").png"))
        }
    }

    func testSidebarChipListRenders() throws {
        _ = NSApplication.shared
        guard let captureDir = ProcessInfo.processInfo.environment["CODERIM_LAYOUT_CAPTURE_DIR"] else {
            throw XCTSkip("set CODERIM_LAYOUT_CAPTURE_DIR to capture")
        }
        for dark in [false, true] {
            let winSize = NSSize(width: 260, height: 520)
            let host = NSHostingView(rootView:
                List(SettingsCategory.allCases, id: \.self) { category in
                    Label {
                        Text(category.title)
                    } icon: {
                        SidebarIcon(systemName: category.systemImage, tint: category.chipTint)
                    }
                    .padding(.vertical, 3)
                }
                .listStyle(.sidebar)
                .frame(width: winSize.width, height: winSize.height)
                .environment(\.colorScheme, dark ? .dark : .light)
            )
            host.sizingOptions = []
            let window = NSWindow(contentRect: NSRect(origin: .zero, size: winSize),
                                  styleMask: [.titled], backing: .buffered, defer: false)
            window.contentView = host
            window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            window.backgroundColor = .windowBackgroundColor
            host.frame = NSRect(origin: .zero, size: winSize)
            for _ in 0..<8 { host.layoutSubtreeIfNeeded() }
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
            host.cacheDisplay(in: host.bounds, to: bitmap)
            let dir = URL(fileURLWithPath: captureDir, isDirectory: true)
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try bitmap.representation(using: .png, properties: [:])?
                .write(to: dir.appendingPathComponent("settings-sidebar-\(dark ? "dark" : "light").png"))
        }
    }

    // MARK: fixtures

    private func makeEnvironment(claudeConnected: Bool) -> SettingsEnvironment {
        SettingsEnvironment(
            codexStore: UsageStore(automaticallyRefresh: false),
            claudeStore: UsageStore(provider: .claude, automaticallyRefresh: false),
            limitStore: AccountLimitStore(provider: SettingsLayoutTestLimitProvider(), pollingInterval: nil),
            claude: makeConnectedClaude()
        )
    }

    private func makeConnectedClaude() -> ClaudeIntegrationStore {
        ClaudeIntegrationStore(automaticallyRefresh: false)
    }

    private func descendants<T: NSView>(of type: T.Type, in view: NSView) -> [T] {
        ((view as? T).map { [$0] } ?? []) + view.subviews.flatMap { descendants(of: type, in: $0) }
    }
}

/// Renders one pane's detail body directly (no `NavigationSplitView`) so a
/// single window shows exactly the pane under test.
private struct SettingsPaneHarness: View {
    let pane: SettingsPane
    var body: some View {
        switch pane {
        case .category(.general): GeneralSettingsView()
        case .category(.usage): UsageSettingsView()
        case .category(.providers): ProvidersSettingsView()
        case .category(.notch): NotchSettingsView()
        case .category(.advanced): AdvancedSettingsView()
        case .category(.about): AboutSettingsView()
        case .provider(let provider): ProviderSettingsView(provider: provider)
        case .notchProvider(let id): NotchProviderSettingsView(providerID: id)
        }
    }
}

private struct SettingsLayoutTestLimitProvider: AccountLimitProviding {
    func readLimits() async throws -> AccountLimitsSnapshot {
        throw AccountLimitError.trustedAppServerNotFound
    }
}
