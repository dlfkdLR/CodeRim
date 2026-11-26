import AppKit
import SwiftUI
import XCTest
import CryptoKit
@testable import CodeRim

@MainActor final class ClockedNotchProbe: XCTestCase {
    final class Pointer {
        var screen = CGPoint(x: -100_000, y: -100_000)
    }

    func testOwnedNotchFrames() throws {
        _ = NSApplication.shared
        let environment = ProcessInfo.processInfo.environment
        let output = URL(fileURLWithPath: try XCTUnwrap(environment["CODERIM_OWNED_NOTCH_OUTPUT"]))
        let p = Pointer()
        let c = NotchWindowController(mouseLocation: { p.screen }, reduceMotion: { true })
        c.model.snapshots = [ProviderSnapshot(id: "qa", displayName: "QA", glyph: .openai,
                                              fidelity: .official, status: .ok, windows: [])]
        c.show()
        c.apply(.onHover)
        defer { c.retire() }
        let container = try XCTUnwrap(c.panelContentViewForTesting)
        let panel = try XCTUnwrap(container.window as? NotchPanel)
        let host = try XCTUnwrap(container.subviews.compactMap { $0 as? NSHostingView<NotchRootView> }.first)
        func runUntil(_ condition: () -> Bool, _ name: String) throws {
            let timeout = Date().addingTimeInterval(3)
            while !condition(), Date() < timeout {
                RunLoop.main.run(mode: .default, before: Date().addingTimeInterval(0.001))
            }
            XCTAssertTrue(condition(), name)
            guard condition() else { throw NSError(domain: name, code: 1) }
        }
        func position(_ along: CGFloat, _ across: CGFloat) {
            let frame = panel.frame, m = c.model
            let v = NotchPlacement(edge: m.edge, panelSize: frame.size)
                .point(along: m.slack + along * m.sizeScale, across: across * m.sizeScale)
            p.screen = CGPoint(x: frame.minX + v.x, y: frame.maxY - v.y)
        }
        func keepOpen() throws {
            let menu = try XCTUnwrap(panel.contextMenuProvider?())
            let item = try XCTUnwrap(menu.item(withTitle: "Keep open"))
            let action = try XCTUnwrap(item.action)
            XCTAssertTrue(item.isEnabled)
            XCTAssertTrue(NSApp.sendAction(action, to: item.target, from: item))
        }
        var results = [[String: Any]]()
        let frames = [0, 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 30, 36, 45, 60, 90]
        func sequence(_ name: String) throws {
            var previous = 0
            for f in frames {
                host._renderForTest(interval: Double(f - previous) / 60)
                previous = f
                container.layoutSubtreeIfNeeded()
                let rep = try XCTUnwrap(container.bitmapImageRepForCachingDisplay(in: container.bounds))
                container.cacheDisplay(in: container.bounds, to: rep)
                let bytes = Data(bytes: try XCTUnwrap(rep.bitmapData), count: rep.bytesPerRow * rep.pixelsHigh)
                let filename = String(format: "%@-%03d.png", name, f)
                try XCTUnwrap(rep.representation(using: .png, properties: [:])).write(to: output.appendingPathComponent(filename))
                let digest = SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
                results.append(["phase": name, "frame60Hz": f, "width": rep.pixelsWide, "height": rep.pixelsHigh,
                                "storageSHA256": digest, "expanded": c.model.isExpanded,
                                "pinned": c.model.isPinned, "hover": c.model.hoveredIndex ?? -1])
            }
        }
        RunLoop.current.run(until: Date().addingTimeInterval(0.5))
        host._renderForTest(interval: 0)
        host._renderForTest(interval: 2)
        try sequence("initial")

        position(c.model.shapeLength / 2, c.model.restingDepth / 2)
        try runUntil({ c.model.isExpanded }, "entered")
        try sequence("entered")

        position(c.model.ringCenter(index: 0), NotchLayout.bodyDepth(for: c.model.edge) / 2)
        try runUntil({ c.model.hoveredIndex == 0 }, "hover-ring")
        host._renderForTest(interval: 0)
        host._renderForTest(interval: 2)
        try keepOpen()
        XCTAssertTrue(c.model.isPinned)
        p.screen = CGPoint(x: -100_000, y: -100_000)
        try runUntil({ c.model.hoveredIndex == nil }, "hover-cleared")
        try sequence("menu-pin")

        try keepOpen()
        XCTAssertFalse(c.model.isPinned)
        try runUntil({ !c.model.isExpanded }, "folded")
        try sequence("unpin-fold")

        c.peek(for: 0.18, focusing: nil)
        c.apply(.alwaysShow)
        XCTAssertTrue(c.model.isExpanded)
        host._renderForTest(interval: 0)
        host._renderForTest(interval: 2)
        try sequence("always-outranks-peek")
        try JSONSerialization.data(withJSONObject: results, options: [.sortedKeys])
            .write(to: output.appendingPathComponent("frames.json"))
    }
}
