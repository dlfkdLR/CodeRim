import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

@MainActor
final class UsageProviderSwitcherTests: XCTestCase {
    private var catalogue: [UsageProviderOption] {
        NotchProviderCatalog.all.map {
            UsageProviderOption(id: $0.id, title: $0.name, glyph: NotchProviderCatalog.glyph(for: $0.id))
        }
    }

    private var currentProviders: [UsageProviderOption] {
        [
            UsageProviderOption(id: "codex", title: "Codex", glyph: .openai, shortcut: "1"),
            UsageProviderOption(id: "claude", title: "Claude", glyph: .claude, shortcut: "2"),
        ]
    }

    func testSearchTrimsWhitespaceMatchesNamesAndIDsAndPreservesOrder() {
        let options = [
            UsageProviderOption(id: "ollama-local", title: "Local models", glyph: .ollamaLocal),
            UsageProviderOption(id: "cloud", title: "Ollama Cloud", glyph: .ollama),
            UsageProviderOption(id: "codex", title: "Codex", glyph: .openai),
        ]
        XCTAssertEqual(UsageProviderList.matching(options, query: " \n\t"), options)
        XCTAssertEqual(UsageProviderList.matching(options, query: "  OLLAMA \n").map(\.id),
                       ["ollama-local", "cloud"])
        XCTAssertEqual(UsageProviderList.matching(options, query: "LOCAL").map(\.id), ["ollama-local"])
        XCTAssertEqual(UsageProviderList.matching(options, query: "cloud").map(\.id), ["cloud"])
        XCTAssertTrue(UsageProviderList.matching(options, query: "provider-that-does-not-exist").isEmpty)
        XCTAssertTrue(UsageProviderList.matching([], query: "codex").isEmpty)
    }

    func testEveryCatalogueProviderRemainsReachableByItsID() {
        let options = catalogue
        XCTAssertGreaterThanOrEqual(options.count, 70)
        XCTAssertEqual(Set(options.map(\.id)).count, options.count)
        for option in options {
            XCTAssertTrue(UsageProviderList.matching(options, query: option.id).contains(option), option.id)
        }
    }

    func testCatalogueAndSearchStatesFitInBothAppearances() async throws {
        let options = catalogue
        let longName = UsageProviderOption(id: "long-label",
            title: "An unusually long provider name that must fit without widening the provider list",
            glyph: .openai)
        let scenarios: [(name: String, options: [UsageProviderOption], selectedID: String, query: String)] = [
            ("small", currentProviders, "claude", ""),
            ("catalogue", options, "codex", ""),
            ("search", options, "codex", "  OLLAMA  "),
            ("no-results", options, "codex", "provider-that-does-not-exist"),
            ("long-label", [longName] + currentProviders, longName.id, ""),
        ]
        for dark in [false, true] {
            for scenario in scenarios {
                var selections: [String] = []
                var closeCount = 0
                let root = UsageProviderList(options: scenario.options, selectedID: scenario.selectedID,
                    onSelect: { selections.append($0) }, onClose: { closeCount += 1 }, initialQuery: scenario.query)
                    .environment(\.colorScheme, dark ? .dark : .light)
                let (host, window) = await hostView(root, dark: dark)
                defer { window.close() }
                let context = "provider-switcher-\(scenario.name)-\(dark ? "dark" : "light")"
                XCTAssertEqual(host.bounds.width, 272, accuracy: 1, context)
                XCTAssertGreaterThan(host.bounds.height, 40, context)
                XCTAssertLessThanOrEqual(host.bounds.height, 420, context)
                let scrolls = descendants(of: NSScrollView.self, in: host)
                XCTAssertLessThanOrEqual(scrolls.count, 1, context)
                if scenario.name == "small" || scenario.name == "long-label" {
                    XCTAssertTrue(scrolls.isEmpty, "\(context): short lists need no scroll viewport")
                }
                if scenario.name == "catalogue" {
                    let scroll = try XCTUnwrap(scrolls.first, context)
                    let document = try XCTUnwrap(scroll.documentView, context)
                    XCTAssertGreaterThan(document.bounds.height, scroll.contentSize.height, context)
                }
                for scroll in scrolls {
                    assertBoundedViewport(scroll, in: host, context: context)
                }
                // Rendering and searching must not switch a provider or dismiss the list.
                XCTAssertTrue(selections.isEmpty, context)
                XCTAssertEqual(closeCount, 0, context)
                try captureIfRequested(host, name: context)
            }
        }
    }

    func testAddingASeventhProviderIntroducesABoundedScrollableList() async throws {
        for count in [6, 7] {
            let options = Array(catalogue.prefix(count))
            let root = UsageProviderList(options: options, selectedID: options[0].id,
                                        onSelect: { _ in }, onClose: {})
            let (host, window) = await hostView(root, dark: false)
            defer { window.close() }
            let scrolls = descendants(of: NSScrollView.self, in: host)
            if count == 6 {
                XCTAssertTrue(scrolls.isEmpty, "Six providers should fit without a scrolling list")
            } else {
                XCTAssertEqual(scrolls.count, 1)
                let scroll = try XCTUnwrap(scrolls.first)
                let document = try XCTUnwrap(scroll.documentView)
                assertBoundedViewport(scroll, in: host, context: "seven providers")
                XCTAssertGreaterThan(document.bounds.height, scroll.contentSize.height)
            }
        }
    }

    func testTriggerRendersShortAndLongSelectedNamesInBothAppearances() async throws {
        let longName = UsageProviderOption(id: "long-label",
            title: "A provider with an unusually long display name", glyph: .openai)
        for dark in [false, true] {
            for long in [false, true] {
                let options = long ? [longName] + currentProviders : currentProviders
                let selectedID = long ? longName.id : "claude"
                let root = UsageProviderSwitcher(options: options, selectedID: selectedID, onSelect: { _ in })
                    .frame(width: 288, height: 56, alignment: .leading)
                    .padding(16)
                    .background(Color(nsColor: .windowBackgroundColor))
                    .environment(\.colorScheme, dark ? .dark : .light)
                let (host, window) = await hostView(root, dark: dark)
                defer { window.close() }
                XCTAssertEqual(host.bounds.width, 320, accuracy: 1)
                XCTAssertEqual(host.bounds.height, 88, accuracy: 1)
                for control in descendants(of: NSControl.self, in: host) where !control.isHiddenOrHasHiddenAncestor {
                    let bounds = host.convert(control.bounds, from: control)
                    XCTAssertTrue(host.bounds.insetBy(dx: -1, dy: -1).contains(bounds),
                                  "Provider trigger must fit its toolbar")
                }
                try captureIfRequested(host,
                    name: "provider-trigger-\(long ? "long-label" : "small")-\(dark ? "dark" : "light")")
            }
        }
    }

    func testKeyboardMovesSelectionSearchesAndDismisses() async throws {
        _ = NSApplication.shared
        for many in [false, true] {
            var selections: [String] = []
            var closeCount = 0
            let options = many ? catalogue : currentProviders
            let root = UsageProviderList(options: options, selectedID: "codex",
                onSelect: { selections.append($0) }, onClose: { closeCount += 1 })
            let host = NSHostingView(rootView: root)
            let window = NSWindow(contentRect: NSRect(x: 80, y: 80, width: 272, height: 380),
                                  styleMask: [.titled], backing: .buffered, defer: false)
            window.isReleasedWhenClosed = false
            window.contentView = host
            window.setContentSize(host.fittingSize)
            window.makeKeyAndOrderFront(nil)
            defer { window.close() }
            for _ in 0..<5 { host.layoutSubtreeIfNeeded(); await Task.yield() }
            try await Task.sleep(for: .milliseconds(80))
            if many {
                let editor = try XCTUnwrap(window.firstResponder as? NSTextView,
                                           "Large provider lists focus search on opening")
                editor.insertText("cursor", replacementRange: NSRange(location: NSNotFound, length: 0))
                try await Task.sleep(for: .milliseconds(50))
            } else {
                window.sendEvent(try keyEvent(code: 125, characters: "\u{F701}", window: window))
                await Task.yield()
            }
            window.sendEvent(try keyEvent(code: 36, characters: "\r", window: window))
            try await Task.sleep(for: .milliseconds(50))
            XCTAssertEqual(selections, [many ? "cursor" : "claude"])
            window.sendEvent(try keyEvent(code: 53, characters: "\u{1B}", window: window))
            await Task.yield()
            XCTAssertEqual(closeCount, 1)
        }
    }

    func testSearchRemainsEditableWhenAvailableProvidersShrink() async throws {
        let host = NSHostingView(rootView: UsageProviderList(options: catalogue, selectedID: "codex",
            onSelect: { _ in }, onClose: {}, initialQuery: "missing"))
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 272, height: 200),
                              styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        defer { window.close() }
        for _ in 0..<5 { host.layoutSubtreeIfNeeded(); await Task.yield() }
        host.rootView = UsageProviderList(options: currentProviders, selectedID: "codex",
            onSelect: { _ in }, onClose: {})
        for _ in 0..<5 { host.layoutSubtreeIfNeeded(); await Task.yield() }
        let search = try XCTUnwrap(descendants(of: NSTextField.self, in: host).first { $0.isEditable })
        XCTAssertEqual(search.stringValue, "missing")
        XCTAssertFalse(search.isHiddenOrHasHiddenAncestor)
    }

    func testTriggerOpensNativePopoverAndEscapeDismissesWithoutSelection() async throws {
        _ = NSApplication.shared
        var selections: [String] = []
        let host = NSHostingView(rootView: UsageProviderSwitcher(options: currentProviders,
            selectedID: "claude", onSelect: { selections.append($0) }).padding(16))
        let window = NSWindow(contentRect: NSRect(x: 80, y: 80, width: 174, height: 66),
                              styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.makeKeyAndOrderFront(nil)
        defer { window.close() }
        for _ in 0..<5 { host.layoutSubtreeIfNeeded(); await Task.yield() }
        let existing = Set(NSApp.windows.map(\.windowNumber))
        for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
            window.sendEvent(try XCTUnwrap(NSEvent.mouseEvent(with: type,
                location: NSPoint(x: 70, y: 30), modifierFlags: [],
                timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber,
                context: nil, eventNumber: 1, clickCount: 1, pressure: type == .leftMouseDown ? 1 : 0)))
        }
        for _ in 0..<10 {
            if NSApp.windows.contains(where: { !existing.contains($0.windowNumber) && $0.isVisible }) { break }
            try await Task.sleep(for: .milliseconds(30))
        }
        let popover = try XCTUnwrap(NSApp.windows.first { !existing.contains($0.windowNumber) && $0.isVisible })
        XCTAssertGreaterThanOrEqual(popover.frame.width, 272)
        XCTAssertLessThan(popover.frame.width, 310)
        XCTAssertLessThan(popover.frame.height, 200)
        try await Task.sleep(for: .milliseconds(80))
        popover.sendEvent(try keyEvent(code: 53, characters: "\u{1B}", window: popover))
        for _ in 0..<10 where popover.isVisible { try await Task.sleep(for: .milliseconds(30)) }
        XCTAssertFalse(popover.isVisible)
        XCTAssertTrue(selections.isEmpty)
    }

    private func keyEvent(code: UInt16, characters: String, window: NSWindow) throws -> NSEvent {
        try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [],
            timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber,
            context: nil, characters: characters, charactersIgnoringModifiers: characters,
            isARepeat: false, keyCode: code))
    }

    private func hostView<Content: View>(_ root: Content, dark: Bool)
        async -> (NSHostingView<Content>, NSWindow) {
        _ = NSApplication.shared
        let host = NSHostingView(rootView: root)
        host.sizingOptions = [.intrinsicContentSize]
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 272, height: 400),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        window.contentView = host
        for _ in 0..<4 {
            host.layoutSubtreeIfNeeded()
            await Task.yield()
        }
        let size = host.fittingSize
        window.setContentSize(size)
        host.frame = NSRect(origin: .zero, size: size)
        for _ in 0..<4 {
            host.layoutSubtreeIfNeeded()
            await Task.yield()
        }
        return (host, window)
    }

    private func assertBoundedViewport(_ scroll: NSScrollView, in host: NSView, context: String,
                                       file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertGreaterThan(scroll.contentSize.height, 0, context, file: file, line: line)
        // At most six 40-point rows, five 4-point gaps, and a small drawing inset.
        XCTAssertLessThanOrEqual(scroll.contentSize.height, 264, context, file: file, line: line)
        XCTAssertLessThanOrEqual(scroll.documentView?.bounds.width ?? 0,
                                scroll.contentSize.width + 1, context, file: file, line: line)
        XCTAssertTrue(host.bounds.insetBy(dx: -1, dy: -1).contains(host.convert(scroll.bounds, from: scroll)),
                      context, file: file, line: line)
    }

    private func descendants<T: NSView>(of type: T.Type, in view: NSView) -> [T] {
        ((view as? T).map { [$0] } ?? []) + view.subviews.flatMap { descendants(of: type, in: $0) }
    }

    private func captureIfRequested(_ host: NSView, name: String) throws {
        guard let directory = ProcessInfo.processInfo.environment["CODERIM_SWITCHER_CAPTURE_DIR"] else { return }
        let folder = URL(fileURLWithPath: directory, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
        host.cacheDisplay(in: host.bounds, to: bitmap)
        let data = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
        try data.write(to: folder.appendingPathComponent("\(name).png"))
    }
}
