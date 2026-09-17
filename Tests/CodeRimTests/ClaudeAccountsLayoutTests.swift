import AppKit
import SwiftUI
import Vision
import XCTest
@testable import CodeRim

@MainActor
final class ClaudeAccountsLayoutTests: XCTestCase {
    func testAccountActionsFitSmallAndDefaultWindowsInBothAppearances() async throws {
        _ = NSApplication.shared
        for state in ["empty", "populated", "long", "busy", "error", "maximum"] {
            for size in [NSSize(width: 500, height: 300), NSSize(width: 560, height: 400)] {
                for dark in [false, true] {
                    let fixture = try ClaudeAccountFixture()
                    if state != "empty" {
                        fixture.vault.accounts = try [ClaudeAccountFixture.saved("one"),
                            ClaudeAccountFixture.saved(state == "long" ? String(repeating: "long-account-name", count: 6) : "two",
                                                       plan: "max", tier: "default_claude_max_20x")]
                    }
                    if state == "maximum" { fixture.vault.accounts = try (0..<12).map { try ClaudeAccountFixture.saved("person\($0)") } }
                    fixture.store.load()
                    if state == "error" { fixture.runtime.policyBlocked = true; await fixture.store.saveCurrent() }
                    if state == "busy" { fixture.runtime.holdSignIn = true; fixture.store.addAccount() }
                    defer { fixture.store.cancelSignIn() }
                    let host = NSHostingView(rootView: ClaudeAccountsView(accounts: fixture.store)
                        .background(Color(nsColor: .windowBackgroundColor))
                        .environment(\.colorScheme, dark ? .dark : .light)
                        .environment(\.displayScale, 2))
                    host.sizingOptions = []
                    host.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                    let window = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                                          styleMask: [.borderless], backing: .buffered, defer: false)
                    window.isReleasedWhenClosed = false
                    window.contentView = host
                    defer { window.contentView = nil }
                    for _ in 0..<8 {
                        window.setContentSize(size); host.setFrameSize(size)
                        host.layoutSubtreeIfNeeded(); await Task.yield()
                    }
                    let bitmap = try XCTUnwrap(NSBitmapImageRep(bitmapDataPlanes: nil,
                        pixelsWide: Int(size.width * 2), pixelsHigh: Int(size.height * 2), bitsPerSample: 8,
                        samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                        bytesPerRow: 0, bitsPerPixel: 0))
                    bitmap.size = size
                    host.cacheDisplay(in: host.bounds, to: bitmap)
                    let name = "claude-accounts-\(state)-\(dark ? "dark" : "light")-\(Int(size.width))x\(Int(size.height))"
                    if let path = ProcessInfo.processInfo.environment["CODERIM_ACCOUNT_CAPTURE_DIR"] {
                        let directory = URL(fileURLWithPath: path)
                        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                        try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                            .write(to: directory.appendingPathComponent(name + ".png"))
                    }
                    let request = VNRecognizeTextRequest()
                    request.recognitionLevel = .accurate
                    request.recognitionLanguages = ["en-US"]
                    try VNImageRequestHandler(cgImage: XCTUnwrap(bitmap.cgImage)).perform([request])
                    let lines = (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
                    let text = lines.joined(separator: " ")
                    for action in ["Save Current Account", "Add Account"] + (state == "busy" ? ["Cancel"] : []) {
                        XCTAssertTrue(text.contains(action), "\(name): missing \(action), \(lines)")
                    }
                    XCTAssertTrue(text.contains("Claude Accounts"), name)
                    XCTAssertTrue(text.contains("before switching"), "\(name): footer clipped: \(lines)")
                    if state == "error" { XCTAssertTrue(text.contains("external authentication"), name) }
                    for scroll in descendants(host).compactMap({ $0 as? NSScrollView }) {
                        let frame = host.convert(scroll.bounds, from: scroll)
                        XCTAssertTrue(host.bounds.insetBy(dx: -1, dy: -1).contains(frame), name)
                        XCTAssertGreaterThan(frame.height, 0, name)
                        if let document = scroll.documentView {
                            XCTAssertLessThanOrEqual(document.bounds.width, scroll.contentView.bounds.width + 1, name)
                        }
                    }
                    XCTAssertEqual(fixture.login.writes, 0)
                    XCTAssertEqual(fixture.vault.writes, 0)
                }
            }
        }
    }

    private func descendants(_ view: NSView) -> [NSView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }
}
