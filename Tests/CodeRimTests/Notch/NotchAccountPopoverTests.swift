import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class NotchAccountPopoverTests: XCTestCase {
    private var options: [NotchAccountOption] {
        [NotchAccountOption(id: "codex", title: "Codex", glyph: .openai,
            account: "long-account-name@example.com", plan: "Pro 20x"),
         NotchAccountOption(id: "claude", title: "Claude Code", glyph: .claude,
            account: nil, plan: nil)]
    }

    func testOnlyAddedProvidersAppearEvenWhenOtherAccountsAreDetected() {
        let summaries = [summary("codex"), summary("claude"), summary("cursor")]
        XCTAssertEqual(NotchAccountOption.addedProviders(selectedIDs: ["cursor"],
            summaries: summaries, order: []).map(\.id), ["cursor"])
        XCTAssertEqual(NotchAccountOption.addedProviders(selectedIDs: ["codex"],
            summaries: summaries, order: []).map(\.id), ["codex"])
        XCTAssertEqual(NotchAccountOption.addedProviders(selectedIDs: ["claude"],
            summaries: summaries, order: []).map(\.id), ["claude"])
        XCTAssertTrue(NotchAccountOption.addedProviders(selectedIDs: [],
            summaries: summaries, order: []).isEmpty)
    }

    func testAccountMenuPreservesWorkspaceQualifiedDisplayNames() throws {
        let options = NotchAccountOption.addedProviders(selectedIDs: ["codex", "cursor"],
            summaries: [summary("codex"), summary("cursor")], order: [],
            accountDisplayNames: ["codex": "alex@example.com · research"])
        XCTAssertEqual(options.first?.account, "alex@example.com · research")
        XCTAssertEqual(options.last?.account, "cursor@example.com")
    }

    func testEveryAddedAccountProviderHasADestinationBeforeItsFirstReading() {
        let ids = Set(NotchProviderCatalog.all.map(\.id))
        let options = NotchAccountOption.addedProviders(selectedIDs: ids,
            summaries: [], order: [])
        XCTAssertEqual(Set(options.map(\.id)), ids.subtracting(["ollama-local"]))
        XCTAssertEqual(Set(options.map(\.id)).count, options.count)
        for option in options {
            switch option.id {
            case "codex": XCTAssertEqual(option.destination, .codex)
            case "claude": XCTAssertEqual(option.destination, .claude)
            default: XCTAssertEqual(option.destination, .providerSettings(option.id))
            }
        }
    }

    func testAddedOrderAndCurrentIdentitySurviveMissingAndDuplicateStoredIDs() throws {
        let selected: Set<String> = ["codex", "cursor", "openrouter", "ollama-local", "unknown"]
        let options = NotchAccountOption.addedProviders(selectedIDs: selected,
            summaries: [summary("cursor")], order: ["missing", "cursor", "cursor", "openrouter"])
        XCTAssertEqual(options.map(\.id), ["cursor", "openrouter", "codex"])
        let cursor = try XCTUnwrap(options.first)
        XCTAssertEqual(cursor.account, "cursor@example.com")
        XCTAssertEqual(cursor.plan, "Pro")
        XCTAssertEqual(cursor.actionTitle, "Switch account in Cursor")
        XCTAssertNil(options.last?.account)
        let removed = NotchAccountOption.addedProviders(selectedIDs: selected.subtracting(["cursor"]),
            summaries: [summary("cursor")], order: ["cursor", "openrouter"])
        XCTAssertEqual(removed.map(\.id), ["openrouter", "codex"])
    }

    func testEmptyNativePopoverStillOpensAndDismisses() throws {
        let controller = NotchWindowController()
        controller.accountOptions = { [] }
        controller.show()
        defer { controller.stop(); controller.apply(.hidden) }
        controller.model.onOpenAccountMenu?()
        let popover = try XCTUnwrap(controller.accountPopoverForTesting)
        XCTAssertTrue(popover.isShown)
        popover.animates = false
        popover.close()
    }

    func testEmptySingleAndMixedListsFitWithoutInventingAccounts() throws {
        let fixtures: [(String, Set<String>)] = [
            ("empty", []), ("codex-only", ["codex"]),
            ("mixed", ["claude", "cursor", "openrouter"])
        ]
        for (name, selected) in fixtures {
            let options = NotchAccountOption.addedProviders(selectedIDs: selected,
                summaries: [summary("cursor"), summary("openrouter")], order: ["cursor"])
            let renderer = ImageRenderer(content: NotchAccountPopover(options: options,
                onSelect: { _ in }, onClose: {}).background(Color(nsColor: .windowBackgroundColor))
                .environment(\.colorScheme, .dark))
            renderer.scale = 2
            let cg = try XCTUnwrap(renderer.cgImage)
            XCTAssertEqual(cg.width, 640)
            XCTAssertLessThan(cg.height, 800)
            try capture(NSBitmapImageRep(cgImage: cg), name: name)
        }
    }

    func testLongListScrollsWithinPopover() async throws {
        _ = NSApplication.shared
        let options = NotchAccountOption.addedProviders(
            selectedIDs: Set(NotchProviderCatalog.all.map(\.id)), summaries: [], order: [])
        let host = NSHostingView(rootView: NotchAccountPopover(options: options,
            onSelect: { _ in }, onClose: {}).background(Color(nsColor: .windowBackgroundColor)))
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 320, height: 430),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        defer { window.close() }
        window.appearance = NSAppearance(named: .darkAqua)
        window.orderFrontRegardless()
        for _ in 0..<5 {
            window.setContentSize(host.fittingSize)
            host.layoutSubtreeIfNeeded()
            await Task.yield()
        }
        XCTAssertEqual(host.bounds.width, 320)
        XCTAssertLessThanOrEqual(host.bounds.height, 450)
        func descendants(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap(descendants) }
        let scroll = try XCTUnwrap(descendants(host).compactMap { $0 as? NSScrollView }.first)
        XCTAssertGreaterThan(scroll.documentView?.bounds.height ?? 0, scroll.contentSize.height)
        XCTAssertLessThanOrEqual(scroll.documentView?.bounds.width ?? 0, scroll.contentSize.width + 1)
        let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
        host.cacheDisplay(in: host.bounds, to: bitmap)
        try capture(bitmap, name: "many-providers")
    }

    private func summary(_ id: String) -> ProviderSummary {
        ProviderSummary(id: id, name: id.capitalized, glyph: NotchProviderCatalog.glyph(for: id),
            account: ProviderAccount(label: "\(id)@example.com", plan: "Pro", source: "Fixture", manageURL: nil),
            signIn: id == "cursor" ? .openApp(bundleID: "fixture.cursor", name: "Cursor") : .guidance("Fixture"))
    }

    private func capture(_ bitmap: NSBitmapImageRep, name: String) throws {
        guard let directory = ProcessInfo.processInfo.environment["CODERIM_NOTCH_CAPTURE_DIR"] else { return }
        let folder = URL(fileURLWithPath: directory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            .write(to: folder.appendingPathComponent("account-\(name).png"))
    }

    func testNativePopoverDismissalReleasesTheHoverHold() async throws {
        let controller = NotchWindowController()
        controller.accountOptions = { self.options }
        controller.show()
        defer { controller.stop(); controller.apply(.hidden) }
        controller.model.isExpanded = true
        controller.model.onOpenAccountMenu?()
        let popover = try XCTUnwrap(controller.accountPopoverForTesting)
        XCTAssertTrue(popover.isShown)
        XCTAssertTrue(controller.model.staysOpen)
        XCTAssertFalse(controller.model.isPinned)
        XCTAssertEqual(popover.behavior, .transient)
        popover.animates = false
        popover.close()
        for _ in 0..<10 where controller.model.isPresentingAccountMenu {
            try await Task.sleep(for: .milliseconds(20))
        }
        XCTAssertFalse(controller.model.isPresentingAccountMenu)
        XCTAssertNil(controller.accountPopoverForTesting)
        XCTAssertFalse(controller.model.staysOpen)
    }

    func testAccountOverviewFitsLongAndMissingIdentity() throws {
        let renderer = ImageRenderer(content: NotchAccountPopover(options: options, onSelect: { _ in }, onClose: {})
            .background(Color(nsColor: .windowBackgroundColor)).environment(\.colorScheme, .dark))
        renderer.scale = 2
        let cg = try XCTUnwrap(renderer.cgImage)
        XCTAssertEqual(cg.width, 640)
        XCTAssertLessThan(cg.height, 600)
        if let directory = ProcessInfo.processInfo.environment["CODERIM_NOTCH_CAPTURE_DIR"] {
            let folder = URL(fileURLWithPath: directory)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let bitmap = NSBitmapImageRep(cgImage: cg)
            try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                .write(to: folder.appendingPathComponent("account-popover.png"))
        }
    }
}
