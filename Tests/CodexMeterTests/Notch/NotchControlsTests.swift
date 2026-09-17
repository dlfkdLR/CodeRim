import AppKit
import SwiftUI
import XCTest
@testable import CodexMeter

@MainActor
final class NotchControlsTests: XCTestCase {
    func testAccountControlRevealsAfterSettingsAndSurvivesTheGapAndMenu() throws {
        let model = NotchViewModel()
        model.isExpanded = true
        model.updateControlHover(overSettings: false, overAccount: true, insideControls: true)
        XCTAssertFalse(model.showsAccountControl, "An invisible account button must not activate on its own")
        model.updateControlHover(overSettings: true, overAccount: false, insideControls: true)
        XCTAssertTrue(model.showsAccountControl)
        model.updateControlHover(overSettings: false, overAccount: false, insideControls: true)
        XCTAssertTrue(model.showsAccountControl, "Crossing the gap must not hide the next button")
        model.updateControlHover(overSettings: false, overAccount: true, insideControls: true)
        XCTAssertTrue(model.isHoveringSettings)
        XCTAssertTrue(model.isHoveringAccountSwitch)
        model.isPresentingAccountMenu = true
        model.updateControlHover(overSettings: false, overAccount: false, insideControls: false)
        XCTAssertTrue(model.showsAccountControl)
        model.isPresentingAccountMenu = false
        model.updateControlHover(overSettings: false, overAccount: false, insideControls: false)
        XCTAssertFalse(model.showsAccountControl)
        XCTAssertFalse(model.isHoveringSettings)

        if let directory = ProcessInfo.processInfo.environment["CODEXMETER_NOTCH_CAPTURE_DIR"] {
            model.snapshots = [ProviderSnapshot(id: "codex", displayName: "Codex", glyph: .openai,
                fidelity: .official, status: .ok, windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.6)])]
            let folder = URL(fileURLWithPath: directory, isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            for revealed in [false, true] {
                model.updateControlHover(overSettings: revealed, overAccount: false, insideControls: revealed)
                let size = model.panelSize
                let renderer = ImageRenderer(content: NotchRootView(model: model)
                    .frame(width: size.width, height: size.height)
                    .frame(width: 120, alignment: .trailing).clipped()
                    .background(Color(red: 0.14, green: 0.16, blue: 0.19)))
                renderer.scale = 2
                let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
                let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                try png.write(to: folder.appendingPathComponent("controls-\(revealed ? "revealed" : "resting").png"))
            }
        }
    }

    func testAccountButtonFollowsSettingsAndFitsEveryEdgeAndSize() {
        let model = NotchViewModel()
        model.screenSize = CGSize(width: 1440, height: 900)
        model.snapshots = [ProviderSnapshot(id: "codex", displayName: "Codex", glyph: .openai,
            fidelity: .official, status: .ok, windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.59)])]
        for edge in NotchEdge.allCases {
            for size in NotchSize.allCases {
                model.edge = edge
                model.sizeScale = size.scale
                let panel = CGRect(origin: .zero, size: model.panelSize)
                XCTAssertTrue(panel.contains(model.accountOrbRect), "\(edge) \(size): \(model.accountOrbRect) exceeds \(panel)")
                XCTAssertGreaterThan(model.accountOrbAlong - NotchLayout.controlDiameter / 2,
                                     model.orbAlong + NotchLayout.orbHotZone / 2)
                XCTAssertGreaterThanOrEqual(model.trailingExtent,
                    model.accountOrbAlong + NotchLayout.controlDiameter / 2 - model.shapeLength)
            }
        }
    }

    func testSideControlsFlipOnlyWhenThereIsInsufficientRoomBelow() {
        let model = NotchViewModel()
        model.screenSize = CGSize(width: 1440, height: 900)
        model.snapshots = controlSnapshots
        for edge in [NotchEdge.left, .right] {
            for size in NotchSize.allCases {
                model.edge = edge
                model.sizeScale = size.scale
                model.alongOffset = 0
                XCTAssertFalse(model.controlsAtStart)
                let threshold = (model.screenSize.height - model.shapeLength * size.scale) / 2
                    - model.trailingExtent * size.scale - 8
                model.alongOffset = threshold - 1
                XCTAssertFalse(model.controlsAtStart)
                model.alongOffset = threshold + 1
                XCTAssertTrue(model.controlsAtStart)
                XCTAssertEqual(model.orbAlong, 0)
                XCTAssertLessThan(model.accountOrbAlong, model.orbAlong)
                XCTAssertEqual(model.trailingExtent, 0)
                XCTAssertGreaterThan(model.leadingExtent, 0)
                XCTAssertTrue(model.isOnOrbHandle(along: model.orbAlong, across: model.orbInset))
                XCTAssertFalse(model.isOnOrbHandle(along: model.shapeLength, across: model.orbInset))
                model.alongOffset = threshold - 1
                XCTAssertFalse(model.controlsAtStart, "Moving back up restores the lower controls")
            }
        }
        for edge in [NotchEdge.top, .bottom] {
            model.edge = edge
            model.alongOffset = 10_000
            XCTAssertFalse(model.controlsAtStart)
        }
    }

    func testBothControlEndsStayOnScreenAcrossSizesOffsetsAndDisplayOrigins() {
        struct Screen: ScreenDescribing {
            let frameValue: CGRect
            var visibleFrameValue: CGRect { frameValue }
        }
        let model = NotchViewModel()
        for screenFrame in [CGRect(x: 0, y: 0, width: 1440, height: 900),
                            CGRect(x: -1920, y: -1080, width: 1920, height: 1080),
                            CGRect(x: 1440, y: 200, width: 1280, height: 720)] {
            let screen = Screen(frameValue: screenFrame)
            for edge in [NotchEdge.left, .right] {
                model.edge = edge
                model.adopt(screen: screen)
                for size in NotchSize.allCases {
                    model.sizeScale = size.scale
                    for count in [1, 2, 4] {
                        model.snapshots = (0..<count).map { controlSnapshots[$0 % 2] }
                        for offset: CGFloat in [-10_000, 0, 150, 350, 10_000] {
                            model.alongOffset = offset
                            let frame = NotchGeometry.panelFrame(for: screen, panelSize: model.panelSize,
                                edge: edge, alongOffset: offset, slack: model.slack,
                                trailingExtent: model.trailingExtent * size.scale,
                                leadingExtent: model.leadingExtent * size.scale)
                            let placement = NotchPlacement(edge: edge, panelSize: frame.size)
                            let account = placement.point(along: model.slack + model.accountOrbAlong * size.scale,
                                                          across: model.orbInset * size.scale)
                            let settings = placement.point(along: model.slack + model.orbAlong * size.scale,
                                                           across: model.orbInset * size.scale)
                            for (point, diameter) in [(account, NotchLayout.controlDiameter),
                                                       (settings, NotchLayout.orbHotZone)] {
                                let y = frame.maxY - point.y
                                let radius = diameter * size.scale / 2
                                XCTAssertGreaterThanOrEqual(y - radius, screenFrame.minY - 1)
                                XCTAssertLessThanOrEqual(y + radius, screenFrame.maxY + 1)
                            }
                            XCTAssertTrue(CGRect(origin: .zero, size: model.panelSize).contains(model.accountOrbRect))
                        }
                    }
                }
            }
        }
    }

    func testUpperControlsRenderAndKeepHoverAcrossTheGap() throws {
        let model = NotchViewModel()
        model.snapshots = controlSnapshots
        model.screenSize = CGSize(width: 1440, height: 900)
        model.isExpanded = true
        for edge in [NotchEdge.left, .right] {
            model.edge = edge
            for offset: CGFloat in [0, 350] {
                model.alongOffset = offset
                for revealed in [false, true] {
                    model.updateControlHover(overSettings: revealed, overAccount: false, insideControls: revealed)
                    if revealed {
                        model.updateControlHover(overSettings: false, overAccount: false, insideControls: true)
                        XCTAssertTrue(model.showsAccountControl)
                        model.updateControlHover(overSettings: false, overAccount: true, insideControls: true)
                        XCTAssertTrue(model.isHoveringAccountSwitch)
                    }
                    let renderer = ImageRenderer(content: NotchRootView(model: model)
                        .frame(width: model.panelSize.width, height: model.panelSize.height)
                        .frame(width: 130, alignment: edge == .right ? .trailing : .leading).clipped()
                        .background(Color(red: 0.14, green: 0.16, blue: 0.19)))
                    renderer.scale = 2
                    let image = try XCTUnwrap(renderer.cgImage)
                    if let directory = ProcessInfo.processInfo.environment["CODEXMETER_NOTCH_CAPTURE_DIR"] {
                        let folder = URL(fileURLWithPath: directory, isDirectory: true)
                        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                        let bitmap = NSBitmapImageRep(cgImage: image)
                        let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                        try png.write(to: folder.appendingPathComponent(
                            "controls-\(edge)-\(offset == 0 ? "lower" : "upper")-\(revealed ? "revealed" : "resting").png"))
                    }
                }
            }
        }
        XCTAssertEqual(SettingsOrb.restingTrim(for: .right, convex: false, atStart: true), 0...0.25)
        XCTAssertEqual(SettingsOrb.restingTrim(for: .left, convex: false, atStart: true), 0.25...0.5)
    }

    func testManualControlsPositionOverridesAutoAndPersists() throws {
        let suite = "CodexMeter.ControlsPosition.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        XCTAssertEqual(NotchControlsPosition.stored(in: defaults), .automatic)
        let model = NotchViewModel()
        model.screenSize = CGSize(width: 1440, height: 900)
        model.snapshots = controlSnapshots
        for position in NotchControlsPosition.allCases {
            defaults.set(position.rawValue, forKey: NotchControlsPosition.preferenceKey)
            model.controlsPosition = .stored(in: defaults)
            XCTAssertEqual(model.controlsPosition, position)
            for edge in [NotchEdge.left, .right] {
                model.edge = edge
                for offset: CGFloat in [0, 10_000] {
                    model.alongOffset = offset
                    XCTAssertEqual(model.controlsAtStart, position == .above || (position == .automatic && offset > 0))
                    XCTAssertEqual(model.leadingExtent > 0, model.controlsAtStart)
                    XCTAssertEqual(model.trailingExtent > 0, !model.controlsAtStart)
                }
            }
            for edge in [NotchEdge.top, .bottom] {
                model.edge = edge
                XCTAssertFalse(model.controlsAtStart)
            }
        }
        defaults.set("unknown", forKey: NotchControlsPosition.preferenceKey)
        XCTAssertEqual(NotchControlsPosition.stored(in: defaults), .automatic)
    }

    func testControlRelocationRendersIntermediateFramesInBothDirections() throws {
        try XCTSkipIf(NSWorkspace.shared.accessibilityDisplayShouldReduceMotion,
                      "System Reduce Motion intentionally disables this animation")
        for revealed in [false, true] {
            let model = NotchViewModel()
            model.screenSize = CGSize(width: 1440, height: 900)
            model.snapshots = controlSnapshots
            model.isExpanded = true
            model.isHoveringSettings = revealed
            let content = NotchRootView(model: model)
                .frame(width: model.panelSize.width, height: model.panelSize.height)
                .frame(width: 140, height: 700, alignment: .topTrailing).clipped()
                .background(Color(red: 0.14, green: 0.16, blue: 0.19))
            let hosting = NSHostingView(rootView: content)
            let window = NSWindow(contentRect: NSRect(x: 120, y: 120, width: 140, height: 700),
                                  styleMask: [.borderless], backing: .buffered, defer: false)
            window.isReleasedWhenClosed = false
            window.contentView = hosting
            window.orderFrontRegardless()
            defer { window.close() }

            func pump(_ seconds: TimeInterval) {
                RunLoop.current.run(until: Date().addingTimeInterval(seconds))
                hosting.layoutSubtreeIfNeeded()
                hosting.displayIfNeeded()
            }
            var capturedReadings: [Data] = []
            func capture(_ name: String) throws -> Data {
                let bitmap = try XCTUnwrap(hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds))
                hosting.cacheDisplay(in: hosting.bounds, to: bitmap)
                let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                let scale = CGFloat(bitmap.pixelsHigh) / hosting.bounds.height
                let top = model.slack + model.ringCenter(index: 0) - NotchLayout.ringDiameter / 2
                let bottom = model.slack + model.ringCenter(index: 1) + NotchLayout.ringDiameter / 2 + 28
                let crop = CGRect(x: 0, y: top * scale, width: CGFloat(bitmap.pixelsWide),
                                  height: (bottom - top) * scale).integral
                let readings = NSBitmapImageRep(cgImage: try XCTUnwrap(bitmap.cgImage?.cropping(to: crop)))
                capturedReadings.append(try XCTUnwrap(readings.representation(using: .png, properties: [:])))
                if let directory = ProcessInfo.processInfo.environment["CODEXMETER_NOTCH_CAPTURE_DIR"] {
                    let folder = URL(fileURLWithPath: directory, isDirectory: true)
                    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                    try png.write(to: folder.appendingPathComponent(
                        "motion-\(revealed ? "revealed" : "resting")-\(name).png"))
                }
                return png
            }

            pump(0.2)
            let lower = try capture("lower")
            model.controlsPosition = .above
            pump(0.12)
            let movingUp = try capture("moving-up")
            pump(0.9)
            let upper = try capture("upper")
            XCTAssertNotEqual(lower, upper)
            XCTAssertNotEqual(movingUp, lower)
            XCTAssertNotEqual(movingUp, upper, "Controls must visibly interpolate rather than jump")
            model.controlsPosition = .below
            pump(0.12)
            let movingDown = try capture("moving-down")
            pump(0.9)
            let returned = try capture("returned")
            XCTAssertEqual(returned, lower)
            XCTAssertNotEqual(movingDown, lower)
            XCTAssertNotEqual(movingDown, upper)
            for readings in capturedReadings.dropFirst() {
                XCTAssertEqual(readings, capturedReadings.first,
                               "Relocating controls must never paint over usage rings or readings")
            }
        }
    }

    private var controlSnapshots: [ProviderSnapshot] {
        [ProviderSnapshot(id: "codex", displayName: "Codex", glyph: .openai,
            fidelity: .official, status: .ok, windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.49)]),
         ProviderSnapshot(id: "claude", displayName: "Claude", glyph: .claude,
            fidelity: .official, status: .ok, windows: [LimitWindow(id: "weekly", label: "Weekly", usedFraction: 0.04)])]
    }

    func testAccountMenuHoldsHoverNotchOpenWithoutChangingThePin() {
        let model = NotchViewModel()
        model.isPresentingAccountMenu = true
        XCTAssertTrue(model.staysOpen)
        XCTAssertFalse(model.isPinned)
        model.isPresentingAccountMenu = false
        XCTAssertFalse(model.staysOpen)
    }

    func testUsedAndRemainingReadingsHandleLimitsMissingAndCountOnlyData() {
        func snapshot(_ fraction: Double?) -> ProviderSnapshot {
            ProviderSnapshot(id: "codex", displayName: "Codex", glyph: .openai,
                fidelity: .official, status: .ok,
                windows: fraction.map { [LimitWindow(id: "w", label: "Weekly", usedFraction: $0)] } ?? [])
        }
        for (used, usedText, leftText) in [(0.59, "59%", "41%"), (0.0, "0%", "100%"),
            (1.0, "100%", "0%"), (1.2, "120%", "0%"), (0.003, "0.3%", "99.7%"), (0.999, "100%", "0.1%")] {
            XCTAssertEqual(NotchPercentageMode.used.text(for: snapshot(used)), usedText)
            XCTAssertEqual(NotchPercentageMode.remaining.text(for: snapshot(used)), leftText)
            XCTAssertEqual(NotchPercentageMode.remaining.fraction(for: used)!, max(0, 1 - used), accuracy: 0.0001)
        }
        XCTAssertEqual(NotchPercentageMode.remaining.text(for: snapshot(nil)), "—")
        XCTAssertNil(NotchPercentageMode.remaining.fraction(for: nil))
        XCTAssertEqual(NotchPercentageMode.remaining.text(for: snapshot(.nan)), "—")
        let countOnly = ProviderSnapshot(id: "p", displayName: "Provider", glyph: .openai,
            fidelity: .official, status: .ok, windows: [LimitWindow(id: "count", label: "Requests", remaining: 42)])
        XCTAssertEqual(NotchPercentageMode.remaining.text(for: countOnly), "42")
        XCTAssertEqual(NotchPercentageMode.used.text(for: countOnly), "42")
        XCTAssertEqual(NotchPercentageMode.remaining.accessibleReading(for: snapshot(0.59)), "41% remaining")
    }

    func testSettingsAndAccountActionsReachTheSwiftUIControls() {
        let controller = NotchWindowController()
        var settingsOpens = 0
        var accountID: String?
        controller.onOpenSettings = { settingsOpens += 1 }
        controller.onSwitchAccount = { accountID = $0 }
        controller.model.onOpenSettings?()
        controller.model.onSwitchAccount?("codex")
        XCTAssertEqual(settingsOpens, 1)
        XCTAssertEqual(accountID, "codex")
        controller.onOpenSettings = nil
        XCTAssertNil(controller.model.onOpenSettings)
    }

    func testProviderSelectionMigratesExistingProvidersAndPersistsAnEmptyList() throws {
        let suite = "CodexMeter.ProviderSelection.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        XCTAssertEqual(NotchProviderSelection.load(defaults: defaults, existing: ["codex", "claude", "copilot", "ollama-local", "unknown"]),
                       ["codex", "claude", "copilot", "ollama-local"])
        XCTAssertEqual(NotchProviderSelection.load(defaults: defaults, existing: []),
                       ["codex", "claude", "copilot", "ollama-local"])
        NotchProviderSelection.save(["claude"], defaults: defaults)
        XCTAssertEqual(NotchProviderSelection.load(defaults: defaults, existing: ["codex", "copilot"]), ["claude"])
        NotchProviderSelection.save([], defaults: defaults)
        XCTAssertTrue(NotchProviderSelection.load(defaults: defaults, existing: ["codex", "claude"]).isEmpty)
    }

    func testTooltipBudgetContainsItsActualContentIncludingTodayPlanAndActions() throws {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        for grouped in [false, true] {
            for sessionCount in [0, 1, 7] {
                for hasToday in [false, true] {
                    let windows = (0..<3).map { index in
                        LimitWindow(id: "window-\(index)", group: grouped ? (index < 2 ? "Codex" : "GPT-5.3-Codex-Spark") : nil,
                            label: index == 0 ? "Weekly" : "5 hours", usedFraction: 0.5,
                            resetsAt: now.addingTimeInterval(86_400), duration: 604_800)
                    }
                    let snapshot = ProviderSnapshot(id: "codex", displayName: "Codex", glyph: .openai,
                        fidelity: .official, status: .ok, windows: windows,
                        todaysTokens: hasToday ? 12_556_351 : nil, accountPlan: "Pro 20x")
                    let sessions = (0..<sessionCount).map { index in
                        AgentSession(id: "session-\(index)", name: "Workspace with a long name \(index)",
                            detail: "Terminal · CodexMeter", state: .busy, waitingFor: nil, since: now)
                    }
                    let activity = sessionCount == 0 ? nil : ActivitySummary(sessions: sessions)
                    let card = TooltipCard(snapshot: snapshot, activity: activity, now: now,
                                           sessionCap: 4, onSwitchAccount: {})
                    let natural = ImageRenderer(content: card.cardContent.frame(width: NotchLayout.cardTextWidth))
                    let naturalImage = try XCTUnwrap(natural.nsImage)
                    let budget = NotchLayout.cardHeight(for: snapshot, sessionCount: sessionCount,
                        sessionCap: 4, now: now, showsAccountAction: true)
                    XCTAssertLessThanOrEqual(naturalImage.size.height + 2 * NotchLayout.cardPadding, budget + 1,
                        "grouped=\(grouped), sessions=\(sessionCount), today=\(hasToday): natural content extends under the mask")
                    if hasToday, let directory = ProcessInfo.processInfo.environment["CODEXMETER_NOTCH_CAPTURE_DIR"] {
                        let url = URL(fileURLWithPath: directory, isDirectory: true)
                        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
                        let renderer = ImageRenderer(content: card.padding(16).background(Color.gray))
                        renderer.scale = 3
                        let image = try XCTUnwrap(renderer.nsImage)
                        let tiff = try XCTUnwrap(image.tiffRepresentation)
                        let png = try XCTUnwrap(NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]))
                        try png.write(to: url.appendingPathComponent("tooltip-\(grouped ? "grouped" : "plain")-sessions\(sessionCount).png"))
                    }
                }
            }
        }
    }
}
