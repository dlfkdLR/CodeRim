import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class ProviderPickerTests: XCTestCase {
    private var rows: [ProviderRowModel] {
        NotchProviderCatalog.all.map {
            ProviderRowModel(id: $0.id, name: $0.name, glyph: NotchProviderCatalog.glyph(for: $0.id),
                connected: $0.id == "codex", statusLine: "", accountLine: nil,
                wasRefused: false, primary: .details)
        }
    }

    func testSearchMatchesNamesAndDescriptionsWithoutChangingCatalogueOrder() {
        XCTAssertEqual(ProviderPickerView.matching(rows, query: " \n").map(\.id), rows.map(\.id))
        XCTAssertEqual(ProviderPickerView.matching(rows, query: "  CURSOR  ").map(\.id), ["cursor"])
        XCTAssertEqual(ProviderPickerView.matching(rows, query: "locally").map(\.id), ["ollama-local"])
        XCTAssertTrue(ProviderPickerView.matching(rows, query: "missing-provider").isEmpty)
    }

    func testCatalogueFitsItsSheetAndScrollsVertically() async throws {
        _ = NSApplication.shared
        let host = NSHostingView(rootView: ProviderPickerView(rows: rows, selectedIDs: ["codex", "claude"],
            onAdd: { _ in }, onConfigure: { _ in }, onClose: {}))
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 600, height: 520),
                              styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        defer { window.close() }
        for dark in [false, true] {
            window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            window.orderFrontRegardless()
            try await Task.sleep(for: .milliseconds(100))
            host.layoutSubtreeIfNeeded()
            let scroll = try XCTUnwrap(descendants(in: host).compactMap { $0 as? NSScrollView }.first)
            XCTAssertLessThanOrEqual(scroll.documentView?.bounds.width ?? 0, scroll.contentSize.width + 1)
            XCTAssertGreaterThan(scroll.documentView?.bounds.height ?? 0, scroll.contentSize.height)
            XCTAssertTrue(host.bounds.insetBy(dx: -1, dy: -1).contains(scroll.convert(scroll.bounds, to: host)))
            if let directory = ProcessInfo.processInfo.environment["CODERIM_PICKER_CAPTURE_DIR"] {
                let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
                host.cacheDisplay(in: host.bounds, to: bitmap)
                let folder = URL(fileURLWithPath: directory, isDirectory: true)
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                try bitmap.representation(using: .png, properties: [:])?.write(to:
                    folder.appendingPathComponent("provider-picker-\(dark ? "dark" : "light").png"))
            }
        }
    }

    private func descendants(in view: NSView) -> [NSView] {
        [view] + view.subviews.flatMap { descendants(in: $0) }
    }
}
