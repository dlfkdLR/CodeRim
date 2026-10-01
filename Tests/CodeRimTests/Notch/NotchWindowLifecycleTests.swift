import XCTest
@testable import CodeRim

@MainActor
final class NotchWindowLifecycleTests: XCTestCase {
    func testShowingAgainReplacesObserversInsteadOfStackingThem() {
        let controller = NotchWindowController()
        controller.show()
        let first = controller.liveObserverCountForTesting
        XCTAssertTrue(first.cursorPoll)

        controller.show()
        controller.show()
        let again = controller.liveObserverCountForTesting
        XCTAssertEqual(again.monitors, first.monitors)
        XCTAssertEqual(again.subscriptions, first.subscriptions)

        controller.stop()
        let stopped = controller.liveObserverCountForTesting
        XCTAssertEqual(stopped.monitors, 0)
        XCTAssertEqual(stopped.subscriptions, 0)
        XCTAssertFalse(stopped.cursorPoll)
    }
}
