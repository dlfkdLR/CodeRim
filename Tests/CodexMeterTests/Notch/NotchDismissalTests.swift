import AppKit
import XCTest
@testable import CodexMeter

@MainActor
final class NotchDismissalTests: XCTestCase {
    private final class Cursor {
        var location = CGPoint(x: -100_000, y: -100_000)
        func leave() { location = CGPoint(x: -100_000, y: -100_000) }
    }

    private func makeController(edge: NotchEdge = .right) throws -> (NotchWindowController, Cursor) {
        _ = NSApplication.shared
        let cursor = Cursor()
        let controller = NotchWindowController(mouseLocation: { cursor.location })
        controller.model.edge = edge
        controller.model.snapshots = [ProviderSnapshot(
            id: "codex", displayName: "Codex", glyph: .openai,
            fidelity: .official, status: .ok,
            windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.4)]
        )]
        controller.show()
        controller.apply(.onHover)
        XCTAssertNotNil(controller.panelFrameForTesting)
        return (controller, cursor)
    }

    private func move(_ cursor: Cursor, to controller: NotchWindowController,
                      along: CGFloat, across: CGFloat) throws {
        let frame = try XCTUnwrap(controller.panelFrameForTesting)
        let placement = NotchPlacement(edge: controller.model.edge, panelSize: frame.size)
        let point = placement.point(along: controller.model.slack + along * controller.model.sizeScale,
                                    across: across * controller.model.sizeScale)
        cursor.location = CGPoint(x: frame.minX + point.x, y: frame.maxY - point.y)
    }

    private func pump(_ seconds: TimeInterval) {
        RunLoop.current.run(until: Date().addingTimeInterval(seconds))
    }

    private func enter(_ cursor: Cursor, controller: NotchWindowController) throws {
        // Wait for the real cursor timer rather than assuming an idle CI host.
        // Placement can settle during the first poll, so keep the pointer on it.
        let deadline = Date().addingTimeInterval(2)
        repeat {
            try move(cursor, to: controller, along: controller.model.shapeLength / 2, across: 1)
            pump(0.05)
        } while !controller.model.isExpanded && Date() < deadline
        XCTAssertTrue(controller.model.isExpanded, "The \(controller.model.edge) notch did not open on hover")
    }

    func testClickingAnOpenNotchMarginStillFoldsOnEveryEdge() throws {
        for edge in NotchEdge.allCases {
            let (controller, cursor) = try makeController(edge: edge)
            defer { controller.retire() }
            try enter(cursor, controller: controller)

            // The curved margin is chrome but neither a ring nor a settings control.
            try move(cursor, to: controller, along: 2, across: 1)
            controller.handleClick()
            XCTAssertFalse(controller.model.isPinned, "A plain click pinned the \(edge) notch")
            cursor.leave()
            pump(0.9)
            XCTAssertFalse(controller.model.isExpanded, "The \(edge) notch stayed open after leaving")
        }
    }

    func testTooltipClickDoesNotPinAndRingClickStillRefreshes() throws {
        let (controller, cursor) = try makeController()
        defer { controller.retire() }
        try enter(cursor, controller: controller)
        var refreshed: [String] = []
        controller.onRefreshProvider = { refreshed.append($0) }
        try move(cursor, to: controller, along: controller.model.ringCenter(index: 0), across: 10)
        pump(0.4)
        controller.handleClick()
        XCTAssertEqual(refreshed, ["codex"])
        XCTAssertEqual(controller.model.hoveredIndex, 0)

        try move(cursor, to: controller, along: controller.model.ringCenter(index: 0),
                 across: controller.model.notchDepth + 50)
        controller.handleClick()
        pump(0.6)
        XCTAssertTrue(controller.model.isExpanded, "Reading a tooltip must keep the notch open")
        XCTAssertFalse(controller.model.isPinned, "Clicking tooltip copy must not pin the notch")
        cursor.leave()
        pump(0.9)
        XCTAssertFalse(controller.model.isExpanded)
        XCTAssertNil(controller.model.hoveredIndex)
    }

    func testReturningBeforeTheFoldDeadlineCancelsItAndNextExitStillCloses() throws {
        let (controller, cursor) = try makeController()
        defer { controller.retire() }
        try enter(cursor, controller: controller)
        cursor.leave()
        pump(0.35)
        try enter(cursor, controller: controller)
        pump(0.6)
        XCTAssertTrue(controller.model.isExpanded)
        cursor.leave()
        pump(0.9)
        XCTAssertFalse(controller.model.isExpanded)
    }

    func testExplicitKeepOpenAndAlwaysShowSurviveLeaving() throws {
        let (controller, cursor) = try makeController()
        defer { controller.retire() }
        controller.togglePinned() // Explicit context-menu action.
        cursor.leave()
        pump(0.9)
        XCTAssertTrue(controller.model.isExpanded)
        controller.togglePinned()
        pump(0.9)
        XCTAssertFalse(controller.model.isExpanded)

        controller.apply(.alwaysShow)
        pump(0.9)
        XCTAssertTrue(controller.model.isExpanded)
        XCTAssertFalse(controller.model.isPinned)
    }
}
