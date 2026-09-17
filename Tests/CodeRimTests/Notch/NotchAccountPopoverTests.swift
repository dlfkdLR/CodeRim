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
        XCTAssertLessThan(cg.height, 520)
        if let directory = ProcessInfo.processInfo.environment["CODERIM_NOTCH_CAPTURE_DIR"] {
            let folder = URL(fileURLWithPath: directory)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let bitmap = NSBitmapImageRep(cgImage: cg)
            try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                .write(to: folder.appendingPathComponent("account-popover.png"))
        }
    }
}
