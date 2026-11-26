import AppKit
import Foundation
import SwiftUI
import XCTest

@testable import CodeRim

@MainActor
final class SyntheticNotchUICaptureTests: XCTestCase {
  func testInMemoryAccountsSettings() throws {
    _ = NSApplication.shared
    let output = URL(
      fileURLWithPath: try XCTUnwrap(
        ProcessInfo.processInfo.environment["CODERIM_LAYOUT_CAPTURE_DIR"]), isDirectory: true)
    try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
    let fixture = try AccountLayoutFixture(state: .populated)
    XCTAssertEqual(fixture.store.accounts.count, 4)
    let size = NSSize(width: 579, height: 560)
    for dark in [false, true] {
      let view = CodexAccountsView(accounts: fixture.store)
        .background(Color(nsColor: .windowBackgroundColor))
        .environment(\.colorScheme, dark ? .dark : .light)
        .environment(\.displayScale, 2)
      let host = NSHostingView(rootView: view)
      host.sizingOptions = []
      host.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
      let display = try XCTUnwrap(NSScreen.screens.first { $0.frame.origin == .zero })
      let window = NSWindow(
        contentRect: NSRect(origin: .zero, size: size), styleMask: [.borderless],
        backing: .buffered, defer: false, screen: display)
      window.setFrameOrigin(
        NSPoint(x: display.frame.midX - size.width / 2, y: display.frame.midY - size.height / 2))
      window.isReleasedWhenClosed = false
      window.appearance = host.appearance
      window.contentView = host
      host.frame = NSRect(origin: .zero, size: size)
      for _ in 0..<8 { host.layoutSubtreeIfNeeded() }
      XCTAssertEqual(window.screen, display)
      let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
      host.cacheDisplay(in: host.bounds, to: bitmap)
      let name = dark ? "accounts-settings-dark.png" : "accounts-settings-light.png"
      try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(
        to: output.appendingPathComponent(name))
      window.contentView = nil
    }
  }

  func testReadingsAndProviderHover() throws {
    _ = NSApplication.shared
    let output = URL(
      fileURLWithPath: try XCTUnwrap(
        ProcessInfo.processInfo.environment["CODERIM_LAYOUT_CAPTURE_DIR"]), isDirectory: true)
    try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
    let now = Date(timeIntervalSince1970: 1_788_055_200)
    let model = NotchViewModel(defaults: UserDefaults(suiteName: "dev.coderim.fixture.synthetic")!)
    model.edge = .right
    model.now = now
    model.isExpanded = true
    model.snapshots = [
      ProviderSnapshot(
        id: "codex", displayName: "Codex", glyph: .openai, fidelity: .official, status: .ok,
        windows: [
          LimitWindow(
            id: "short", label: "Session", usedFraction: 0.37,
            resetsAt: now.addingTimeInterval(3_600), duration: 18_000),
          LimitWindow(
            id: "week", label: "Weekly", usedFraction: 0.72,
            resetsAt: now.addingTimeInterval(345_600), duration: 604_800),
        ],
        headlineID: "short", todaysTokens: 123_456, accountPlan: "team",
        accountIdentity: "fixture-a"),
      ProviderSnapshot(
        id: "claude", displayName: "Claude", glyph: .claude, fidelity: .official, status: .ok,
        windows: [
          LimitWindow(
            id: "short", label: "Session", usedFraction: 0.81,
            resetsAt: now.addingTimeInterval(7_200), duration: 18_000)
        ],
        headlineID: "short", todaysTokens: 78_910, accountPlan: "pro", accountIdentity: "fixture-b"),
    ]
    model.sessions = [
      "codex": [
        AgentSession(
          id: "fixture-review", name: "Review usage totals", detail: "Fixture workspace",
          state: .waiting, waitingFor: "Review requested", since: now.addingTimeInterval(-180)),
        AgentSession(
          id: "fixture-build", name: "Compile demo", detail: "Fixture workspace", state: .busy,
          waitingFor: nil, since: now.addingTimeInterval(-60)),
      ]
    ]
    model.sessionTokenTotals = ["codex": ["fixture-review": 3456, "fixture-build": 9900]]
    struct Display: Codable {
      let frame: CGRect?
      let scale: CGFloat?
      let width: CGFloat
      let height: CGFloat
    }
    let size = model.panelSize
    let display = try XCTUnwrap(NSScreen.screens.first { $0.frame.origin == .zero })
    let metadata = Display(
      frame: display.frame, scale: display.backingScaleFactor, width: size.width,
      height: size.height)
    try JSONEncoder().encode(metadata).write(to: output.appendingPathComponent("display.json"))
    let view = NotchRootView(model: model)
      .frame(width: size.width, height: size.height)
      .environment(\.locale, Locale(identifier: "en_US"))
      .environment(\.timeZone, TimeZone(secondsFromGMT: 0)!)
      .environment(\.colorScheme, .dark)
    for (name, index) in [
      ("notch-readings", nil), ("notch-codex-card", 0), ("notch-claude-card", 1),
      ("notch-codex-idle-card", 0),
    ] as [(String, Int?)] {
      // This is the observed model selection that the real AppKit mouse handler updates.
      if name == "notch-codex-idle-card" {
        model.sessions["codex"] = [
          AgentSession(
            id: "fixture-review", name: "Review usage totals", detail: "Fixture workspace",
            state: .waiting, waitingFor: "Review requested", since: now.addingTimeInterval(-180)),
          AgentSession(
            id: "fixture-build", name: "Compile demo", detail: "Fixture workspace", state: .idle,
            waitingFor: nil, since: now.addingTimeInterval(-60)),
        ]
      }
      model.hoveredIndex = index
      XCTAssertEqual(model.hoveredSnapshot?.id, index.map { model.snapshots[$0].id })
      let renderer = ImageRenderer(content: view)
      renderer.scale = display.backingScaleFactor
      let image = try XCTUnwrap(renderer.cgImage)
      let bitmap = NSBitmapImageRep(cgImage: image)
      try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(
        to: output.appendingPathComponent(name + ".png"))
    }
  }
}
