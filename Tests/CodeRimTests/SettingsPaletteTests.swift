import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

/// The settings panes must layer like the Usage pane: cards a touch lighter than the ground,
/// never a light field around dark cards.
@MainActor
final class SettingsPaletteTests: XCTestCase {
    private let size = CGSize(width: 640, height: 260)

    private func render<V: View>(_ view: V, dark: Bool) throws -> NSBitmapImageRep {
        let window = NSWindow(contentRect: CGRect(origin: .zero, size: size), styleMask: [.titled], backing: .buffered, defer: false)
        window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let host = NSHostingView(rootView: view.frame(width: size.width, height: size.height))
        host.frame = CGRect(origin: .zero, size: size)
        window.contentView = host
        host.layoutSubtreeIfNeeded()
        let rep = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
        host.cacheDisplay(in: host.bounds, to: rep)
        return rep
    }

    /// Luminance at a point in view coordinates (the bitmap may be drawn at 2x).
    private func luminance(_ rep: NSBitmapImageRep, _ x: Double, _ y: Double) -> Double {
        let scale = Double(rep.pixelsWide) / size.width
        let c = rep.colorAt(x: Int(x * scale), y: Int(y * scale))!.usingColorSpace(.sRGB)!
        return Double(0.299 * c.redComponent + 0.587 * c.greenComponent + 0.114 * c.blueComponent) * 255
    }

    private var pane: some View {
        SettingsForm {
            SettingsSection(title: "Startup") {
                SettingsToggleRow("Launch at Login", isOn: .constant(false))
                SettingsToggleRow("Another", isOn: .constant(true))
            }
        }
    }

    func testCardsSitLighterThanTheGroundInDarkMode() throws {
        let usageGround = try render(Color.clear.background(.background), dark: true)
        let rep = try render(pane, dark: true)
        let ground = luminance(rep, 300, 4)      // above the section title: pane only
        let card = luminance(rep, 300, 70)       // inside the first row
        XCTAssertEqual(ground, luminance(usageGround, 300, 70), accuracy: 2, "the pane ground is the Usage pane's ground")
        XCTAssertGreaterThan(card, ground + 6, "a card is lighter than the ground it sits on")
        XCTAssertLessThan(card, ground + 20, "…but only a touch lighter, like the Usage card")
    }

    func testCardsStayReadableInLightMode() throws {
        let rep = try render(pane, dark: false)
        let ground = luminance(rep, 300, 4), card = luminance(rep, 300, 70)
        XCTAssertGreaterThan(ground, 200, "light ground")
        XCTAssertLessThan(abs(card - ground), 30, "a card is a gentle step from the ground, not a hole in it")
    }
}
