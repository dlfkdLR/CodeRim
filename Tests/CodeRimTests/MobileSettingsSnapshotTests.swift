import AppKit
import SwiftUI
import XCTest
@testable import CodeRim

/// Writes the iPhone section to disk when CODERIM_SETTINGS_CAPTURE_DIR is set, so its layout can
/// be looked at. Without the variable it only checks that the section renders.
@MainActor
final class MobileSettingsSnapshotTests: XCTestCase {
    func testTheIPhoneSectionRenders() throws {
        let size = CGSize(width: 780, height: 520)
        let view = SettingsForm { MobileSettingsSection() }.frame(width: size.width, height: size.height)
        let window = NSWindow(contentRect: CGRect(origin: .zero, size: size), styleMask: [.titled], backing: .buffered, defer: false)
        window.appearance = NSAppearance(named: .darkAqua)
        let host = NSHostingView(rootView: view)
        host.frame = CGRect(origin: .zero, size: size); window.contentView = host
        host.layoutSubtreeIfNeeded()
        let rep = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
        host.cacheDisplay(in: host.bounds, to: rep)
        XCTAssertGreaterThan(rep.pixelsWide, 0)
        if let directory = ProcessInfo.processInfo.environment["CODERIM_SETTINGS_CAPTURE_DIR"] {
            try FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true)
            try rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: directory + "/iphone-idle.png"))
        }
    }
}
