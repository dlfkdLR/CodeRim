import AppKit
import XCTest
@testable import CodeRim

@MainActor
final class StatusItemControllerTests: XCTestCase {
    private var menu: NSMenu {
        let menu = NSMenu()
        StatusItemController.shared.menuNeedsUpdate(menu)
        return menu
    }

    func testMenuLeadsWithTokenUsageAndOffersTheWayBackIn() {
        let titles = menu.items.map(\.title)
        XCTAssertEqual(
            titles,
            ["Token Usage…", "", "Show Notch", "Settings…", "Check for Updates…", "", "Quit CodeRim"]
        )
    }

    func testShowNotchReflectsThePreference() {
        let defaults = UserDefaults.standard
        let original = defaults.object(forKey: "showEdgeNotch")
        defer {
            if let original { defaults.set(original, forKey: "showEdgeNotch") }
            else { defaults.removeObject(forKey: "showEdgeNotch") }
        }

        func showNotchItem() -> NSMenuItem? { menu.items.first { $0.title == "Show Notch" } }

        defaults.set(false, forKey: "showEdgeNotch")
        XCTAssertEqual(showNotchItem()?.state, .off)

        defaults.set(true, forKey: "showEdgeNotch")
        XCTAssertEqual(showNotchItem()?.state, .on)
    }

    func testUpdatesItemTracksUpdaterAvailability() throws {
        let item = try XCTUnwrap(menu.items.first { $0.title == "Check for Updates…" })
        XCTAssertEqual(item.isEnabled, UpdateService.shared.isAvailable)
    }
}
